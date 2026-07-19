using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopifyOrderMonitorService.Configuration;
using ShopifyOrderMonitorService.Shopify;

namespace ShopifyOrderMonitorService.OrderSync;

/// <summary>
/// Reads recently updated orders from Shopify, writes one JSON file per order, and remembers how
/// far it got so a restart resumes rather than re-downloading everything.
/// </summary>
public sealed class OrderSyncEngine
{
    readonly OrderSyncOptions _options;
    readonly ShopifyApiClient _shopify;
    readonly OrderCheckpointStore _checkpoints;
    readonly OrderFileWriter _writer;
    readonly ILogger<OrderSyncEngine> _logger;

    public OrderSyncEngine(
        IOptions<OrderSyncOptions> options,
        ShopifyApiClient shopify,
        OrderCheckpointStore checkpoints,
        OrderFileWriter writer,
        ILogger<OrderSyncEngine> logger)
    {
        _options = options.Value;
        _shopify = shopify;
        _checkpoints = checkpoints;
        _writer = writer;
        _logger = logger;
    }

    /// <summary>Runs one poll-and-capture cycle. A failure here must not bring down the caller's loop.</summary>
    public async Task RunCycleAsync(CancellationToken ct)
    {
        var checkpoint = _checkpoints.Load();

        // First run reaches back InitialLookback. After that, start a little BEFORE the latest order
        // update we saw — see BuildFilter for why that overlap is what keeps this correct.
        var since = checkpoint is { } last
            ? last - _options.Overlap
            : DateTimeOffset.UtcNow - _options.InitialLookback;

        var filter = BuildFilter(since);
        _logger.LogInformation("Polling orders matching: {Filter}", filter);

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

                if (await _writer.WriteAsync(order, ct).ConfigureAwait(false))
                {
                    written++;
                    _logger.LogInformation("Wrote {Name} ({NumericId}) updated {UpdatedAt:u}.",
                        OrderFields.NameOf(order), OrderFields.NumericId(order), OrderFields.UpdatedAt(order));
                }
                else
                {
                    skipped++;
                }

                matched++;
                var updatedAt = OrderFields.UpdatedAt(order);
                if (highWaterMark is null || updatedAt > highWaterMark) highWaterMark = updatedAt;
            }

            // Save progress after each page. Results are sorted oldest-first by updatedAt, so the mark
            // only moves forward; the files for this page are already on disk; and unchanged overlap
            // re-reads are a no-op. So a crash costs at most one page of re-reads and can never lose
            // an update.
            if (highWaterMark is { } mark && mark != checkpoint)
            {
                _checkpoints.Save(mark);
                checkpoint = mark;
            }

            hasNextPage = page.HasNextPage;
            cursor = page.EndCursor;
        }
        while (hasNextPage && !ct.IsCancellationRequested);

        _logger.LogInformation(
            "Cycle finished: {Matched} order(s) matched, {Written} file(s) written, {Skipped} unchanged file(s) skipped. Watermark now {Watermark:u}.",
            matched, written, skipped, highWaterMark);
    }

    async Task<(List<JsonObject> Orders, bool HasNextPage, string? EndCursor)> ReadPageAsync(
        string filter, string? cursor, CancellationToken ct)
    {
        var variables = new Dictionary<string, object?>
        {
            ["filter"] = filter,
            ["first"] = _options.OrdersPageSize,
            ["after"] = cursor,
            ["lineItemsFirst"] = _options.LineItemsPageSize,
            ["shippingLinesFirst"] = _options.ShippingLinesPageSize,
        };

        var data = await _shopify.QueryAsync(GraphQlQueries.OrdersPage, "OrdersPage", variables, ct).ConfigureAwait(false);
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

            var id = OrderFields.GlobalId(order);
            var cursor = pageInfo["endCursor"]?.GetValue<string>();
            var guard = 0;
            _logger.LogInformation("Order {NumericId} has more than {LineItemsPageSize} line items; fetching the rest.",
                OrderFields.NumericId(order), _options.LineItemsPageSize);

            while (cursor is not null)
            {
                if (++guard > 50)
                    throw new InvalidOperationException($"Order {id}: still more line items after 50 pages. Refusing to write a truncated order.");

                var variables = new Dictionary<string, object?> { ["id"] = id, ["first"] = _options.LineItemsPageSize, ["after"] = cursor };
                var data = await _shopify.QueryAsync(GraphQlQueries.OrderLineItems, "OrderLineItemsPage", variables, ct).ConfigureAwait(false);

                var page = data["order"]?["lineItems"];
                if (page is null)
                {
                    _logger.LogWarning("Order {Id} disappeared while paging its line items.", id);
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

    // ">=" plus the overlap subtracted upstream means the boundary update is re-read, not risked.
    // The timestamp is quoted because it contains colons, which the search syntax treats as separators.
    string BuildFilter(DateTimeOffset since)
    {
        var timestamp = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var filter = $"updated_at:>='{timestamp}'";
        if (!string.IsNullOrWhiteSpace(_options.AdditionalFilter))
            filter = $"{filter} AND ({_options.AdditionalFilter!.Trim()})";
        return filter;
    }
}
