#:property PublishAot=false

// ╔══════════════════════════════════════════════════════════════════════════╗
// ║  Shopify order capture — one file, no project, no NuGet.                    ║
// ║                                                                            ║
// ║  1. Fill in SHOP, CLIENT_ID and CLIENT_SECRET just below.                  ║
// ║  2. Make sure the .NET 10 SDK is installed:   dotnet --version             ║
// ║  3. Run it:                                    dotnet run OrderSync.cs      ║
// ║                                                                            ║
// ║  It polls the Shopify Admin GraphQL API (2026-07) every 15 minutes and     ║
// ║  writes one JSON file per order under ./data/orders/. Leave it running;    ║
// ║  press Ctrl+C to stop. It remembers where it got to, so restarting is safe.║
// ╚══════════════════════════════════════════════════════════════════════════╝

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

// ─── FILL THESE IN ───────────────────────────────────────────────────────────
const string SHOP          = "your-store";          // the part before .myshopify.com
const string CLIENT_ID     = "your-client-id";
const string CLIENT_SECRET = "your-client-secret";   // safer: leave blank and set the
                                                     // environment variable Shopify__ClientSecret
// ─────────────────────────────────────────────────────────────────────────────

var config = AppConfig.Load(SHOP, CLIENT_ID, CLIENT_SECRET);

if (config.Validate() is { } problem)
{
    Log.Error(problem);
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;                 // shut down cleanly rather than being killed mid-write
    Log.Info("Shutdown requested — finishing the current step, then stopping.");
    cts.Cancel();
};

try
{
    using var sync = new OrderSync(config);
    await sync.RunAsync(cts.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Log.Info("Stopped.");
    return 0;
}
catch (Exception ex)
{
    Log.Error("Fatal: " + ex.Message);
    return 1;
}


// ============================================================================
//  Configuration
// ============================================================================
sealed class AppConfig
{
    public required string Shop { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required string ApiVersion { get; init; }

    public TimeSpan Interval { get; init; }
    public TimeSpan Overlap { get; init; }
    public TimeSpan InitialLookback { get; init; }

    public int OrdersPageSize { get; init; }
    public int LineItemsPageSize { get; init; }
    public int ShippingLinesPageSize { get; init; }

    public string OutputDirectory { get; init; } = "data/orders";
    public string CheckpointPath { get; init; } = "data/state/checkpoint.json";
    public bool PartitionByDate { get; init; }
    public bool Overwrite { get; init; }
    public bool RunOnStartup { get; init; }
    public string? AdditionalFilter { get; init; }

    // Shopify rejects any single query costing more than this, on every plan, before it runs.
    public const int MaxSingleQueryCost = 1000;

    /// <summary>Normalises "your-store", "your-store.myshopify.com", or a pasted URL to a bare host.</summary>
    public string ShopDomain
    {
        get
        {
            var s = Shop.Trim();
            if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s["https://".Length..];
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s["http://".Length..];
            var slash = s.IndexOf('/');
            if (slash >= 0) s = s[..slash];
            if (!s.Contains('.')) s += ".myshopify.com";
            return s;
        }
    }

    public string TokenEndpoint => $"https://{ShopDomain}/admin/oauth/access_token";
    public string GraphQlEndpoint => $"https://{ShopDomain}/admin/api/{ApiVersion}/graphql.json";

    // Cost model: a scalar is free, an object costs 1, a connection is sized by its `first` argument.
    // These are conservative upper bounds; every response reports the real cost, which then takes over.
    public int EstimatedPageCost
    {
        get
        {
            const int perLineItem = 1 + 2 + 2 + 1 + 1 + 1;   // node + 2 money bags + variant + product + attrs
            const int perShipping = 1 + 2 + 2;               // node + 2 money bags
            const int perOrder    = 1 + 10 + 1 + 2 + 1;      // node + 5 money bags + customer + 2 addresses + attrs
            return OrdersPageSize * (perOrder + ShippingLinesPageSize * perShipping + LineItemsPageSize * perLineItem) + 2;
        }
    }

    public int LineItemsPageCost => 1 + LineItemsPageSize * (1 + 2 + 2 + 1 + 1 + 1) + 2;

    public static AppConfig Load(string shop, string clientId, string clientSecret) => new()
    {
        Shop            = Env("Shopify__Shop") ?? shop,
        ClientId        = Env("Shopify__ClientId") ?? clientId,
        ClientSecret    = Env("Shopify__ClientSecret") ?? clientSecret,
        ApiVersion      = Env("Shopify__ApiVersion") ?? "2026-07",

        Interval        = EnvTime("Sync__Interval") ?? TimeSpan.FromMinutes(15),
        Overlap         = EnvTime("Sync__Overlap") ?? TimeSpan.FromMinutes(10),
        InitialLookback = EnvTime("Sync__InitialLookback") ?? TimeSpan.FromDays(1),

        OrdersPageSize        = EnvInt("Sync__OrdersPageSize") ?? 4,
        LineItemsPageSize     = EnvInt("Sync__LineItemsPageSize") ?? 20,
        ShippingLinesPageSize = EnvInt("Sync__ShippingLinesPageSize") ?? 3,

        OutputDirectory  = Env("Sync__OutputDirectory") ?? "data/orders",
        CheckpointPath   = Env("Sync__CheckpointPath") ?? "data/state/checkpoint.json",
        PartitionByDate  = EnvBool("Sync__PartitionByDate") ?? true,
        Overwrite        = EnvBool("Sync__Overwrite") ?? false,
        RunOnStartup     = EnvBool("Sync__RunOnStartup") ?? true,
        AdditionalFilter = Env("Sync__AdditionalFilter"),
    };

    public string? Validate()
    {
        if (IsBlankOrPlaceholder(Shop, "your-store"))
            return "SHOP is not set. Put your store handle (the part before .myshopify.com) in the SHOP line near the top of the file, or set the environment variable Shopify__Shop.";
        if (IsBlankOrPlaceholder(ClientId, "your-client-id"))
            return "CLIENT_ID is not set. Fill in the CLIENT_ID line near the top of the file, or set Shopify__ClientId.";
        if (IsBlankOrPlaceholder(ClientSecret, "your-client-secret"))
            return "CLIENT_SECRET is not set. Fill in the CLIENT_SECRET line near the top of the file, or (better) set the environment variable Shopify__ClientSecret.";

        if (!System.Text.RegularExpressions.Regex.IsMatch(ApiVersion, @"^\d{4}-(01|04|07|10)$"))
            return $"ApiVersion '{ApiVersion}' is not a valid Shopify version. It looks like YYYY-MM where MM is 01, 04, 07, or 10 (e.g. 2026-07).";

        if (Interval < TimeSpan.FromSeconds(30))
            return "Interval must be at least 30 seconds.";

        if (OrdersPageSize < 1 || LineItemsPageSize < 1 || ShippingLinesPageSize < 1)
            return "Page sizes must be at least 1.";

        if (EstimatedPageCost > MaxSingleQueryCost)
            return $"The page sizes are too large: an orders page would cost about {EstimatedPageCost} points, over Shopify's {MaxSingleQueryCost}-point limit for a single query. Lower OrdersPageSize or LineItemsPageSize.";

        return null;
    }

    static bool IsBlankOrPlaceholder(string value, string placeholder) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), placeholder, StringComparison.OrdinalIgnoreCase);

    static string? Env(string key)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    static TimeSpan? EnvTime(string key) =>
        Env(key) is { } v && TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out var t) ? t : null;

    static int? EnvInt(string key) =>
        Env(key) is { } v && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    static bool? EnvBool(string key) =>
        Env(key) is { } v && bool.TryParse(v, out var b) ? b : null;
}


// ============================================================================
//  Console logging
// ============================================================================
static class Log
{
    static readonly object Gate = new();

    public static void Info(string message) => Write("INFO ", message, ConsoleColor.Gray);
    public static void Warn(string message) => Write("WARN ", message, ConsoleColor.Yellow);
    public static void Error(string message) => Write("ERROR", message, ConsoleColor.Red);

    static void Write(string level, string message, ConsoleColor colour)
    {
        lock (Gate)
        {
            var previous = Console.ForegroundColor;
            try { Console.ForegroundColor = colour; } catch { /* redirected output */ }
            Console.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {message}");
            try { Console.ForegroundColor = previous; } catch { }
        }
    }
}


// ============================================================================
//  Shopify client: token acquisition + GraphQL with retry / throttle handling
// ============================================================================
sealed class ShopifyClient : IDisposable
{
    const int MaxAttempts = 6;

    readonly AppConfig _config;
    readonly HttpClient _http;
    readonly SemaphoreSlim _tokenLock = new(1, 1);
    string? _token;
    DateTimeOffset _tokenExpiry;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ShopifyClient(AppConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Add("User-Agent", "ShopifyOrderSync/1.0");
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenLock.Dispose();
    }

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
                ["client_id"] = _config.ClientId,
                ["client_secret"] = _config.ClientSecret,
            });

            using var response = await _http.PostAsync(_config.TokenEndpoint, body, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(DescribeTokenFailure(response.StatusCode, text));

            var token = JsonSerializer.Deserialize<TokenResponse>(text, Json)
                        ?? throw new InvalidOperationException("The token endpoint returned an empty response.");

            _token = token.AccessToken;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn).AddMinutes(-10);
            Log.Info($"Acquired access token for {_config.ShopDomain}. Granted scopes: {token.Scope}");
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
                using var request = new HttpRequestMessage(HttpMethod.Post, _config.GraphQlEndpoint)
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
                Log.Warn($"{operationName} attempt {attempt}/{MaxAttempts} failed ({Short(ex)}); retrying in {delay.TotalSeconds:0.0}s.");
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    InvalidateToken();
                    Log.Warn($"{operationName}: access token rejected; refreshing and retrying.");
                    continue;
                }

                JsonNode? root;
                try { root = JsonNode.Parse(bodyText); }
                catch (JsonException ex)
                {
                    last = ex;
                    var delay = Backoff(attempt);
                    Log.Warn($"{operationName} attempt {attempt}/{MaxAttempts}: response was not readable JSON; retrying in {delay.TotalSeconds:0.0}s.");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                var errors = root?["errors"] as JsonArray;

                if (IsThrottled(errors))
                {
                    var delay = ThrottleDelay(root, attempt);
                    Log.Warn($"{operationName} throttled by Shopify; waiting {delay.TotalSeconds:0.0}s before retrying.");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                if (IsTransient(response.StatusCode))
                {
                    var delay = RetryAfter(response) ?? Backoff(attempt);
                    last = new InvalidOperationException($"HTTP {(int)response.StatusCode}");
                    Log.Warn($"{operationName} attempt {attempt}/{MaxAttempts}: HTTP {(int)response.StatusCode}; retrying in {delay.TotalSeconds:0.0}s.");
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
        404 => "The token endpoint was not found (HTTP 404) — check the store handle in SHOP. "
             + "Shopify said: " + Truncate(body),
        _   => $"Could not get an access token (HTTP {(int)status}). Shopify said: " + Truncate(body),
    };

    sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("scope")] public string Scope { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}


// ============================================================================
//  The sync itself: read orders, write files, remember progress, repeat
// ============================================================================
sealed class OrderSync : IDisposable
{
    readonly AppConfig _config;
    readonly ShopifyClient _shopify;
    static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public OrderSync(AppConfig config)
    {
        _config = config;
        _shopify = new ShopifyClient(config);
    }

    public void Dispose() => _shopify.Dispose();

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Info($"Starting. Store {_config.ShopDomain}, API version {_config.ApiVersion}, polling every {_config.Interval}, " +
                 $"re-scanning the last {_config.Overlap} each cycle.");
        Log.Info($"Writing orders to {Path.GetFullPath(_config.OutputDirectory)}");
        Log.Info($"Estimated cost per orders page: {_config.EstimatedPageCost} points (Shopify rejects any single query over {AppConfig.MaxSingleQueryCost}).");

        if (_config.InitialLookback > TimeSpan.FromDays(60))
            Log.Warn("InitialLookback is over 60 days. The read_orders scope only exposes the last 60 days; " +
                     "reaching further back needs the read_all_orders scope.");

        if (_config.RunOnStartup)
            await RunCycleSafely(ct).ConfigureAwait(false);

        // PeriodicTimer keeps at most one pending tick, so a slow cycle never builds a backlog —
        // the next one just starts as soon as the current finishes.
        using var timer = new PeriodicTimer(_config.Interval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            await RunCycleSafely(ct).ConfigureAwait(false);
    }

    async Task RunCycleSafely(CancellationToken ct)
    {
        try
        {
            await RunCycleAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One failed cycle must not stop the service. The checkpoint only advances for pages that
            // fully succeeded, so the next cycle picks up exactly where this one stopped.
            Log.Error($"Sync cycle failed: {ex.Message}. Will try again at the next interval ({_config.Interval}).");
        }
    }

    async Task RunCycleAsync(CancellationToken ct)
    {
        var checkpoint = LoadCheckpoint();

        // First run reaches back InitialLookback. After that, start a little BEFORE the last order we
        // saw — see BuildFilter for why that overlap is what keeps this correct.
        var since = checkpoint is { } last
            ? last - _config.Overlap
            : DateTimeOffset.UtcNow - _config.InitialLookback;

        var filter = BuildFilter(since);
        Log.Info($"Polling orders matching: {filter}");

        DateTimeOffset? highWaterMark = checkpoint;
        int matched = 0, written = 0, skipped = 0;
        string? cursor = null;
        bool hasNextPage;

        do
        {
            var page = await ReadPageAsync(filter, cursor, ct).ConfigureAwait(false);

            foreach (var order in page.Orders)
            {
                ct.ThrowIfCancellationRequested();

                await CompleteLineItemsAsync(order, ct).ConfigureAwait(false);

                if (await WriteOrderAsync(order, ct).ConfigureAwait(false))
                {
                    written++;
                    Log.Info($"Captured {NameOf(order)} ({NumericId(order)}) created {CreatedAt(order):u}.");
                }
                else
                {
                    skipped++;
                }

                matched++;
                var createdAt = CreatedAt(order);
                if (highWaterMark is null || createdAt > highWaterMark) highWaterMark = createdAt;
            }

            // Save progress after each page. Results are sorted oldest-first, so the mark only moves
            // forward; the files for this page are already on disk; and re-writing is a no-op. So a
            // crash costs at most one page of re-reads and can never lose an order.
            if (highWaterMark is { } mark && mark != checkpoint)
            {
                SaveCheckpoint(mark);
                checkpoint = mark;
            }

            hasNextPage = page.HasNextPage;
            cursor = page.EndCursor;
        }
        while (hasNextPage && !ct.IsCancellationRequested);

        Log.Info($"Cycle finished: {matched} order(s) matched, {written} new file(s), {skipped} already captured. " +
                 $"Watermark now {highWaterMark:u}.");
    }

    async Task<(List<JsonObject> Orders, bool HasNextPage, string? EndCursor)> ReadPageAsync(
        string filter, string? cursor, CancellationToken ct)
    {
        var variables = new Dictionary<string, object?>
        {
            ["filter"] = filter,
            ["first"] = _config.OrdersPageSize,
            ["after"] = cursor,
            ["lineItemsFirst"] = _config.LineItemsPageSize,
            ["shippingLinesFirst"] = _config.ShippingLinesPageSize,
        };

        var data = await _shopify.QueryAsync(Queries.OrdersPage, "NewOrdersPage", variables, ct).ConfigureAwait(false);
        var connection = data["orders"] ?? throw new InvalidOperationException("The orders query returned no orders connection.");

        var orders = new List<JsonObject>();
        if (connection["nodes"] is JsonArray nodes)
            foreach (var node in nodes)
                if (node is JsonObject order)
                    orders.Add((JsonObject)order.DeepClone());   // detach from the response tree

        var pageInfo = connection["pageInfo"];
        var hasNextPage = pageInfo?["hasNextPage"]?.GetValue<bool>() ?? false;
        var endCursor = pageInfo?["endCursor"]?.GetValue<string>();
        return (orders, hasNextPage, endCursor);
    }

    // If an order has more line items than fit in one page, follow the nested cursor to gather the
    // rest, then strip the pagination bookkeeping so it never lands in the output file.
    async Task CompleteLineItemsAsync(JsonObject order, CancellationToken ct)
    {
        if (order["lineItems"] is not JsonObject lineItems) return;

        try
        {
            var pageInfo = lineItems["pageInfo"] as JsonObject;
            if (pageInfo?["hasNextPage"]?.GetValue<bool>() != true || lineItems["nodes"] is not JsonArray nodes)
                return;

            var id = GlobalId(order);
            var cursor = pageInfo["endCursor"]?.GetValue<string>();
            var guard = 0;
            Log.Info($"Order {NumericId(order)} has more than {_config.LineItemsPageSize} line items; fetching the rest.");

            while (cursor is not null)
            {
                if (++guard > 50)
                    throw new InvalidOperationException($"Order {id}: still more line items after 50 pages. Refusing to write a truncated order.");

                var variables = new Dictionary<string, object?> { ["id"] = id, ["first"] = _config.LineItemsPageSize, ["after"] = cursor };
                var data = await _shopify.QueryAsync(Queries.OrderLineItems, "OrderLineItemsPage", variables, ct).ConfigureAwait(false);

                var page = data["order"]?["lineItems"];
                if (page is null)
                {
                    Log.Warn($"Order {id} disappeared while paging its line items.");
                    return;
                }

                if (page["nodes"] is JsonArray more)
                    foreach (var node in more)
                        if (node is not null)
                            nodes.Add(node.DeepClone());   // a node can only have one parent

                cursor = page["pageInfo"]?["hasNextPage"]?.GetValue<bool>() == true
                    ? page["pageInfo"]?["endCursor"]?.GetValue<string>()
                    : null;
            }
        }
        finally
        {
            lineItems.Remove("pageInfo");
        }
    }

    // Written atomically (temp file, then rename) so a watcher never sees a half-written order, and
    // idempotently (skip if it already exists) so the overlap re-scan is free of side effects.
    async Task<bool> WriteOrderAsync(JsonObject order, CancellationToken ct)
    {
        var directory = _config.PartitionByDate
            ? Path.Combine(_config.OutputDirectory, CreatedAt(order).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            : _config.OutputDirectory;
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"order-{NumericId(order)}.json");
        if (File.Exists(path) && !_config.Overwrite) return false;

        var temp = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, order, Pretty, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    DateTimeOffset? LoadCheckpoint()
    {
        var path = _config.CheckpointPath;
        if (!File.Exists(path))
        {
            Log.Info($"No checkpoint at {path}; treating this as a first run.");
            return null;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            var raw = node?["lastCreatedAt"]?.GetValue<string>();
            return raw is null ? null : DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        catch (Exception ex)
        {
            // Refuse to guess: starting over could re-download 60 days, skipping ahead could lose orders.
            throw new InvalidOperationException(
                $"The checkpoint file '{path}' could not be read ({ex.Message}). Inspect or delete it before " +
                "restarting; deleting it makes the next run fall back to InitialLookback.");
        }
    }

    void SaveCheckpoint(DateTimeOffset mark)
    {
        var path = _config.CheckpointPath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(
            new Dictionary<string, object> { ["lastCreatedAt"] = mark, ["updatedAt"] = DateTimeOffset.UtcNow }, Pretty);

        var temp = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    // ">=" plus the overlap subtracted upstream means the boundary order is re-read, not risked.
    // The timestamp is quoted because it contains colons, which the search syntax treats as separators.
    string BuildFilter(DateTimeOffset since)
    {
        var timestamp = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var filter = $"created_at:>='{timestamp}'";
        if (!string.IsNullOrWhiteSpace(_config.AdditionalFilter))
            filter = $"{filter} AND ({_config.AdditionalFilter!.Trim()})";
        return filter;
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Could not remove temporary file {path}."); }
    }

    // ── small readers for the few fields the sync logic needs ──────────────
    static string GlobalId(JsonObject order) => order["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Order has no id.");
    static string NameOf(JsonObject order) => order["name"]?.GetValue<string>() ?? "(unnamed)";

    static string NumericId(JsonObject order)
    {
        var legacy = ReadString(order["legacyResourceId"]);
        if (!string.IsNullOrEmpty(legacy)) return legacy!;
        var gid = GlobalId(order);
        var slash = gid.LastIndexOf('/');
        return slash >= 0 && slash < gid.Length - 1 ? gid[(slash + 1)..] : throw new InvalidOperationException($"Cannot derive a numeric id from '{gid}'.");
    }

    static DateTimeOffset CreatedAt(JsonObject order)
    {
        var raw = order["createdAt"]?.GetValue<string>() ?? throw new InvalidOperationException($"Order {GlobalId(order)} has no createdAt.");
        return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    static string? ReadString(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        if (value.TryGetValue<long>(out var number)) return number.ToString(CultureInfo.InvariantCulture);
        return null;
    }
}


// ============================================================================
//  GraphQL documents
// ============================================================================
static class Queries
{
    // sortKey: CREATED_AT is load-bearing. The connection's default is PROCESSED_AT, and Shopify's
    // guidance is to sort by the field you filter on; a mismatch is slow or fails. Ascending order
    // also guarantees each page's newest createdAt only moves forward, so progress can be saved per page.
    public const string OrdersPage = """
        query NewOrdersPage($filter: String!, $first: Int!, $after: String, $lineItemsFirst: Int!, $shippingLinesFirst: Int!) {
          orders(first: $first, after: $after, query: $filter, sortKey: CREATED_AT, reverse: false) {
            pageInfo { hasNextPage endCursor }
            nodes {
              id
              legacyResourceId
              name
              confirmationNumber
              poNumber
              createdAt
              updatedAt
              processedAt
              cancelledAt
              cancelReason
              closedAt
              test
              currencyCode
              presentmentCurrencyCode
              displayFinancialStatus
              displayFulfillmentStatus
              returnStatus
              email
              phone
              note
              tags
              sourceName
              sourceIdentifier
              paymentGatewayNames
              totalPriceSet { ...MoneyFields }
              subtotalPriceSet { ...MoneyFields }
              totalTaxSet { ...MoneyFields }
              totalShippingPriceSet { ...MoneyFields }
              totalDiscountsSet { ...MoneyFields }
              customAttributes { key value }
              customer { id legacyResourceId firstName lastName email phone }
              billingAddress { ...AddressFields }
              shippingAddress { ...AddressFields }
              shippingLines(first: $shippingLinesFirst) {
                nodes {
                  title
                  code
                  source
                  originalPriceSet { ...MoneyFields }
                  discountedPriceSet { ...MoneyFields }
                }
              }
              lineItems(first: $lineItemsFirst) {
                pageInfo { hasNextPage endCursor }
                nodes { ...LineItemFields }
              }
            }
          }
        }

        fragment MoneyFields on MoneyBag { shopMoney { amount currencyCode } }

        fragment AddressFields on MailingAddress {
          firstName lastName company address1 address2 city province provinceCode country countryCodeV2 zip phone
        }

        fragment LineItemFields on LineItem {
          id name title variantTitle sku vendor quantity currentQuantity requiresShipping taxable isGiftCard
          originalUnitPriceSet { ...MoneyFields }
          discountedTotalSet { ...MoneyFields }
          customAttributes { key value }
          variant { id legacyResourceId sku title }
          product { id legacyResourceId handle }
        }
        """;

    public const string OrderLineItems = """
        query OrderLineItemsPage($id: ID!, $first: Int!, $after: String) {
          order(id: $id) {
            lineItems(first: $first, after: $after) {
              pageInfo { hasNextPage endCursor }
              nodes { ...LineItemFields }
            }
          }
        }

        fragment MoneyFields on MoneyBag { shopMoney { amount currencyCode } }

        fragment LineItemFields on LineItem {
          id name title variantTitle sku vendor quantity currentQuantity requiresShipping taxable isGiftCard
          originalUnitPriceSet { ...MoneyFields }
          discountedTotalSet { ...MoneyFields }
          customAttributes { key value }
          variant { id legacyResourceId sku title }
          product { id legacyResourceId handle }
        }
        """;
}
