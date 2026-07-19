namespace ShopifyOrderMonitorService.Configuration;

/// <summary>Settings that control how often and how much the order sync reads from Shopify.</summary>
public sealed class OrderSyncOptions
{
    public const string SectionName = "OrderSync";

    // Shopify rejects any single query costing more than this, on every plan, before it runs.
    public const int MaxSingleQueryCost = 1000;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan Overlap { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan InitialLookback { get; set; } = TimeSpan.FromDays(1);

    public int OrdersPageSize { get; set; } = 4;

    public int LineItemsPageSize { get; set; } = 20;

    public int ShippingLinesPageSize { get; set; } = 3;

    public string OutputDirectory { get; set; } = "data/orders";

    public string CheckpointPath { get; set; } = "data/state/checkpoint.json";

    public bool PartitionByDate { get; set; } = true;

    public bool Overwrite { get; set; }

    public bool RunOnStartup { get; set; } = true;

    public string? AdditionalFilter { get; set; }

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
}
