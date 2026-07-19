using System.Text.Json.Serialization;

namespace ShopifyOrderMonitorService.Shopify;

/// <summary>The JSON body returned by Shopify's OAuth token endpoint.</summary>
sealed class TokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("scope")] public string Scope { get; set; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
}
