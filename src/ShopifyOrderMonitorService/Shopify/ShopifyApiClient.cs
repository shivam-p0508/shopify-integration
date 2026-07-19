using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopifyOrderMonitorService.Configuration;

namespace ShopifyOrderMonitorService.Shopify;

/// <summary>
/// Shopify Admin API client: token acquisition plus GraphQL with retry / throttle handling.
/// </summary>
public sealed class ShopifyApiClient
{
    const int MaxAttempts = 6;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly ShopifyOptions _options;
    readonly HttpClient _http;
    readonly ILogger<ShopifyApiClient> _logger;
    readonly SemaphoreSlim _tokenLock = new(1, 1);
    string? _token;
    DateTimeOffset _tokenExpiry;

    public ShopifyApiClient(IHttpClientFactory httpClientFactory, IOptions<ShopifyOptions> options, ILogger<ShopifyApiClient> logger)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The named <see cref="HttpClient"/> registered for this client in the DI container.</summary>
    public const string HttpClientName = "Shopify";

    // ── OAuth 2.0 client credentials grant ────────────────────────────────
    // Right for an app your own organisation built and installed in a store it owns. No browser
    // redirect. The token lasts ~24h; it is cached and renewed 10 minutes early.
    async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiry) return _token;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiry) return _token;

            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
            });

            using var response = await _http.PostAsync(_options.TokenEndpoint, body, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(DescribeTokenFailure(response.StatusCode, text));

            var token = JsonSerializer.Deserialize<TokenResponse>(text, Json)
                        ?? throw new InvalidOperationException("The token endpoint returned an empty response.");

            _token = token.AccessToken;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn).AddMinutes(-10);
            _logger.LogInformation("Acquired access token for {ShopDomain}. Granted scopes: {Scope}", _options.ShopDomain, token.Scope);
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    void InvalidateToken() => _token = null;

    /// <summary>
    /// Runs a GraphQL query and returns its <c>data</c> node, retrying transient failures.
    /// <para>
    /// The retry logic is hand-written for one specific reason: Shopify signals throttling with
    /// <b>HTTP 200</b> and an error in the response body, not a 429. A retry policy that only looks at
    /// status codes misses it completely and hammers the API.
    /// </para>
    /// </summary>
    public async Task<JsonNode> QueryAsync(
        string query, string operationName, Dictionary<string, object?> variables, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { query, variables, operationName }, Json);
        Exception? last = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            HttpResponseMessage response;
            string bodyText;
            try
            {
                var token = await GetTokenAsync(ct).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post, _options.GraphQlEndpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-Shopify-Access-Token", token);

                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                bodyText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException) && !ct.IsCancellationRequested)
            {
                last = ex;
                var delay = Backoff(attempt);
                _logger.LogWarning("{Operation} attempt {Attempt}/{MaxAttempts} failed ({Reason}); retrying in {Delay:0.0}s.",
                    operationName, attempt, MaxAttempts, Short(ex), delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    InvalidateToken();
                    _logger.LogWarning("{Operation}: access token rejected; refreshing and retrying.", operationName);
                    continue;
                }

                JsonNode? root;
                try { root = JsonNode.Parse(bodyText); }
                catch (JsonException ex)
                {
                    last = ex;
                    var delay = Backoff(attempt);
                    _logger.LogWarning("{Operation} attempt {Attempt}/{MaxAttempts}: response was not readable JSON; retrying in {Delay:0.0}s.",
                        operationName, attempt, MaxAttempts, delay.TotalSeconds);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                var errors = root?["errors"] as JsonArray;

                if (IsThrottled(errors))
                {
                    var delay = ThrottleDelay(root, attempt);
                    _logger.LogWarning("{Operation} throttled by Shopify; waiting {Delay:0.0}s before retrying.", operationName, delay.TotalSeconds);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                if (IsTransient(response.StatusCode))
                {
                    var delay = RetryAfter(response) ?? Backoff(attempt);
                    last = new InvalidOperationException($"HTTP {(int)response.StatusCode}");
                    _logger.LogWarning("{Operation} attempt {Attempt}/{MaxAttempts}: HTTP {StatusCode}; retrying in {Delay:0.0}s.",
                        operationName, attempt, MaxAttempts, (int)response.StatusCode, delay.TotalSeconds);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                if (errors is { Count: > 0 })
                {
                    var messages = string.Join("; ", errors.Select(e => e?["message"]?.GetValue<string>() ?? "unknown error"));
                    throw new InvalidOperationException($"{operationName} returned errors: {messages}");
                }

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"{operationName}: HTTP {(int)response.StatusCode}. {Truncate(bodyText)}");

                return root?["data"]
                    ?? throw new InvalidOperationException($"{operationName}: the response contained no data.");
            }
        }

        throw new InvalidOperationException($"{operationName} failed after {MaxAttempts} attempts.", last);
    }

    static bool IsThrottled(JsonArray? errors)
    {
        if (errors is null) return false;
        foreach (var e in errors)
        {
            var code = e?["extensions"]?["code"]?.GetValue<string>();
            if (string.Equals(code, "THROTTLED", StringComparison.OrdinalIgnoreCase)) return true;
            var message = e?["message"]?.GetValue<string>();
            if (message is not null && message.Contains("throttl", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Wait exactly long enough for the cost bucket to refill, using the numbers Shopify hands back,
    // rather than guessing with plain exponential backoff.
    static TimeSpan ThrottleDelay(JsonNode? root, int attempt)
    {
        var cost = root?["extensions"]?["cost"];
        var requested = AsDouble(cost?["requestedQueryCost"]);
        var available = AsDouble(cost?["throttleStatus"]?["currentlyAvailable"]);
        var restore = AsDouble(cost?["throttleStatus"]?["restoreRate"]);

        if (requested is { } rq && available is { } av && restore is { } rr && rr > 0)
        {
            var seconds = Math.Max(0, rq - av) / rr;
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 0.5, 30));
        }
        return Backoff(attempt);
    }

    static bool IsTransient(HttpStatusCode status) =>
        (int)status is 408 or 429 or 500 or 502 or 503 or 504;

    static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(Math.Pow(2, attempt), 30);
        return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
    }

    static double? AsDouble(JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValue<double>(); }
        catch { try { return node.GetValue<long>(); } catch { return null; } }
    }

    static string Short(Exception ex) => ex.InnerException?.Message ?? ex.Message;
    static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    static string DescribeTokenFailure(HttpStatusCode status, string body) => (int)status switch
    {
        400 => "Could not get an access token (HTTP 400). The usual cause with the client-credentials "
             + "grant is that the app and the store are not in the same organisation in the Shopify Dev "
             + "Dashboard — that grant requires it. Check that both live under the same org. "
             + "Shopify said: " + Truncate(body),
        401 => "Access token request was rejected (HTTP 401) — the client secret looks wrong. "
             + "Shopify said: " + Truncate(body),
        404 => "The token endpoint was not found (HTTP 404) — check the configured store handle. "
             + "Shopify said: " + Truncate(body),
        _   => $"Could not get an access token (HTTP {(int)status}). Shopify said: " + Truncate(body),
    };
}
