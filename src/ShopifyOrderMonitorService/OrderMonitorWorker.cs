using Microsoft.Extensions.Options;
using ShopifyOrderMonitorService.Configuration;
using ShopifyOrderMonitorService.OrderSync;

namespace ShopifyOrderMonitorService;

/// <summary>Hosts the recurring Shopify order sync cycle as a background service.</summary>
public sealed class OrderMonitorWorker : BackgroundService
{
    readonly ILogger<OrderMonitorWorker> _logger;
    readonly OrderSyncOptions _options;
    readonly OrderSyncEngine _orderSync;

    public OrderMonitorWorker(
        ILogger<OrderMonitorWorker> logger,
        IOptions<OrderSyncOptions> options,
        OrderSyncEngine orderSync)
    {
        _logger = logger;
        _options = options.Value;
        _orderSync = orderSync;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Shopify order monitor service started. Polling every {Interval}, re-scanning the last {Overlap} each cycle.",
            _options.Interval, _options.Overlap);

        if (_options.EstimatedPageCost > OrderSyncOptions.MaxSingleQueryCost)
            _logger.LogWarning(
                "The configured page sizes would cost about {EstimatedPageCost} points per orders page, over Shopify's " +
                "{MaxSingleQueryCost}-point limit for a single query. Lower OrdersPageSize or LineItemsPageSize.",
                _options.EstimatedPageCost, OrderSyncOptions.MaxSingleQueryCost);

        if (_options.RunOnStartup)
            await RunCycleSafelyAsync(stoppingToken);

        // PeriodicTimer keeps at most one pending tick, so a slow cycle never builds a backlog —
        // the next one just starts as soon as the current finishes.
        using var timer = new PeriodicTimer(_options.Interval);
        while (!stoppingToken.IsCancellationRequested &&
               await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCycleSafelyAsync(stoppingToken);
        }
    }

    async Task RunCycleSafelyAsync(CancellationToken ct)
    {
        try
        {
            await _orderSync.RunCycleAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One failed cycle must not stop the service. The checkpoint only advances for pages that
            // fully succeeded, so the next cycle picks up exactly where this one stopped.
            _logger.LogError(ex, "Sync cycle failed. Will try again at the next interval ({Interval}).", _options.Interval);
        }
    }
}
