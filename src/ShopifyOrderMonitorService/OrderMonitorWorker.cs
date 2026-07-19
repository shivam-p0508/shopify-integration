using Microsoft.Extensions.Options;

namespace ShopifyOrderMonitorService;

public sealed class OrderMonitorWorker : BackgroundService
{
    private readonly ILogger<OrderMonitorWorker> _logger;
    private readonly OrderMonitorOptions _options;

    public OrderMonitorWorker(
        ILogger<OrderMonitorWorker> logger,
        IOptions<OrderMonitorOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PollingIntervalSeconds));

        _logger.LogInformation(
            "Shopify order monitor service started with polling interval of {IntervalSeconds} seconds.",
            interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested &&
               await timer.WaitForNextTickAsync(stoppingToken))
        {
            _logger.LogInformation(
                "Order monitoring placeholder executed at {Timestamp}. Add Shopify-specific logic in a future change.",
                DateTimeOffset.Now);
        }
    }
}
