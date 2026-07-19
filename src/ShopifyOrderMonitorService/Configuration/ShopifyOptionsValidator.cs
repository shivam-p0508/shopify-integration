using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ShopifyOrderMonitorService.Configuration;

/// <summary>Validates <see cref="ShopifyOptions"/> at startup so misconfiguration fails fast.</summary>
public sealed class ShopifyOptionsValidator : IValidateOptions<ShopifyOptions>
{
    public ValidateOptionsResult Validate(string? name, ShopifyOptions options)
    {
        if (IsBlankOrPlaceholder(options.Shop, "your-store"))
            return ValidateOptionsResult.Fail(
                "Shopify:Shop is not set. Provide your store handle (the part before .myshopify.com).");

        if (IsBlankOrPlaceholder(options.ClientId, "your-client-id"))
            return ValidateOptionsResult.Fail("Shopify:ClientId is not set.");

        if (IsBlankOrPlaceholder(options.ClientSecret, "your-client-secret"))
            return ValidateOptionsResult.Fail(
                "Shopify:ClientSecret is not set. Prefer setting it via the Shopify__ClientSecret environment variable.");

        if (!Regex.IsMatch(options.ApiVersion, @"^\d{4}-(01|04|07|10)$"))
            return ValidateOptionsResult.Fail(
                $"Shopify:ApiVersion '{options.ApiVersion}' is not a valid Shopify version. It looks like YYYY-MM " +
                "where MM is 01, 04, 07, or 10 (e.g. 2026-07).");

        return ValidateOptionsResult.Success;
    }

    static bool IsBlankOrPlaceholder(string value, string placeholder) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), placeholder, StringComparison.OrdinalIgnoreCase);
}
