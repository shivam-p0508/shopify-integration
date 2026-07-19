using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopifyOrderMonitorService.Configuration;

namespace ShopifyOrderMonitorService.OrderSync;

/// <summary>Writes one JSON file per order to disk, atomically and idempotently.</summary>
public sealed class OrderFileWriter
{
    static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly OrderSyncOptions _options;
    readonly ILogger<OrderFileWriter> _logger;

    public OrderFileWriter(IOptions<OrderSyncOptions> options, ILogger<OrderFileWriter> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    // Written atomically (temp file, then rename) so a watcher never sees a half-written order, and
    // idempotently (skip if the stored copy is already current) so the overlap re-scan is free of side effects.
    public async Task<bool> WriteAsync(JsonObject order, CancellationToken ct)
    {
        var directory = _options.PartitionByDate
            ? Path.Combine(_options.OutputDirectory, OrderFields.CreatedAt(order).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            : _options.OutputDirectory;
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"order-{OrderFields.NumericId(order)}.json");
        if (File.Exists(path) && !_options.Overwrite && await ExistingFileIsCurrentAsync(path, order, ct).ConfigureAwait(false)) return false;

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
            FileHelpers.TryDelete(_logger, temp);
            throw;
        }
    }

    async Task<bool> ExistingFileIsCurrentAsync(string path, JsonObject order, CancellationToken ct)
    {
        try
        {
            var existing = JsonNode.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)) as JsonObject;
            if (existing is null) return false;
            return OrderFields.UpdatedAt(existing) >= OrderFields.UpdatedAt(order);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not compare existing order file {Path}; rewriting it.", path);
            return false;
        }
    }
}
