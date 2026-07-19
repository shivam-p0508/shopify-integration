using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopifyOrderMonitorService.Configuration;

namespace ShopifyOrderMonitorService.OrderSync;

/// <summary>Reads and writes the "how far have we got" checkpoint file, atomically.</summary>
public sealed class OrderCheckpointStore
{
    static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly OrderSyncOptions _options;
    readonly ILogger<OrderCheckpointStore> _logger;

    public OrderCheckpointStore(IOptions<OrderSyncOptions> options, ILogger<OrderCheckpointStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public DateTimeOffset? Load()
    {
        var path = _options.CheckpointPath;
        if (!File.Exists(path))
        {
            _logger.LogInformation("No checkpoint at {Path}; treating this as a first run.", path);
            return null;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            var raw = node?["lastUpdatedAt"]?.GetValue<string>()
                      ?? node?["lastCreatedAt"]?.GetValue<string>();
            return raw is null ? null : DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        catch (Exception ex)
        {
            // Refuse to guess: starting over could re-download 60 days, skipping ahead could lose orders.
            throw new InvalidOperationException(
                $"The checkpoint file '{path}' could not be read ({ex.Message}). Inspect or delete it before " +
                "restarting; deleting it makes the next run fall back to InitialLookback.");
        }
    }

    public void Save(DateTimeOffset mark)
    {
        var path = _options.CheckpointPath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["lastUpdatedAt"] = mark,
                ["lastCreatedAt"] = mark,
                ["updatedAt"] = DateTimeOffset.UtcNow,
            }, Pretty);

        var temp = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            FileHelpers.TryDelete(_logger, temp);
            throw;
        }
    }
}
