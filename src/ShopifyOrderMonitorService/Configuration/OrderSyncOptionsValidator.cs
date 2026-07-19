using Microsoft.Extensions.Options;

namespace ShopifyOrderMonitorService.Configuration;

/// <summary>Validates <see cref="OrderSyncOptions"/> at startup so misconfiguration fails fast.</summary>
public sealed class OrderSyncOptionsValidator : IValidateOptions<OrderSyncOptions>
{
    public ValidateOptionsResult Validate(string? name, OrderSyncOptions options)
    {
        if (options.Interval < TimeSpan.FromSeconds(30))
            return ValidateOptionsResult.Fail("OrderSync:Interval must be at least 30 seconds.");

        if (options.OrdersPageSize < 1 || options.LineItemsPageSize < 1 || options.ShippingLinesPageSize < 1)
            return ValidateOptionsResult.Fail("OrderSync page sizes must be at least 1.");

        if (options.EstimatedPageCost > OrderSyncOptions.MaxSingleQueryCost)
            return ValidateOptionsResult.Fail(
                $"The configured page sizes are too large: an orders page would cost about {options.EstimatedPageCost} " +
                $"points, over Shopify's {OrderSyncOptions.MaxSingleQueryCost}-point limit for a single query. " +
                "Lower OrdersPageSize or LineItemsPageSize.");

        return ValidateOptionsResult.Success;
    }
}
