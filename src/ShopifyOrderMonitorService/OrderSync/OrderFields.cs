using System.Globalization;
using System.Text.Json.Nodes;

namespace ShopifyOrderMonitorService.OrderSync;

/// <summary>Small, focused readers for the few fields the sync logic needs from a raw order node.</summary>
static class OrderFields
{
    public static string GlobalId(JsonObject order) =>
        order["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Order has no id.");

    public static string NameOf(JsonObject order) => order["name"]?.GetValue<string>() ?? "(unnamed)";

    public static string NumericId(JsonObject order)
    {
        var legacy = ReadString(order["legacyResourceId"]);
        if (!string.IsNullOrEmpty(legacy)) return legacy!;

        var gid = GlobalId(order);
        var slash = gid.LastIndexOf('/');
        return slash >= 0 && slash < gid.Length - 1
            ? gid[(slash + 1)..]
            : throw new InvalidOperationException($"Cannot derive a numeric id from '{gid}'.");
    }

    public static DateTimeOffset CreatedAt(JsonObject order)
    {
        var raw = order["createdAt"]?.GetValue<string>()
                  ?? throw new InvalidOperationException($"Order {GlobalId(order)} has no createdAt.");
        return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    static string? ReadString(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        if (value.TryGetValue<long>(out var number)) return number.ToString(CultureInfo.InvariantCulture);
        return null;
    }
}
