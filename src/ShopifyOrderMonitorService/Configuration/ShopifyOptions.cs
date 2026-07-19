namespace ShopifyOrderMonitorService.Configuration;

/// <summary>Credentials and connection settings for the Shopify Admin API.</summary>
public sealed class ShopifyOptions
{
    public const string SectionName = "Shopify";

    /// <summary>The store handle (the part before .myshopify.com), a bare domain, or a full URL.</summary>
    public string Shop { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";

    public string ApiVersion { get; set; } = "2026-07";

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
}
