namespace ShopifyOrderMonitorService;

public sealed class OrderMonitorOptions
{
    public const string SectionName = "OrderMonitor";

    public int PollingIntervalSeconds { get; set; } = 30;
}
