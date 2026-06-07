using System.Text.Json;

namespace BogDb.Mcp.Server.Services;

internal static class JsonArgumentReader
{
    public static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Argument '{propertyName}' is required and must be a string.");
        return property.GetString()!;
    }

    public static int? GetOptionalInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
            throw new InvalidOperationException($"Argument '{propertyName}' must be an integer.");
        return value;
    }

    public static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Argument '{propertyName}' must be a string.");
        return property.GetString();
    }

    public static double? GetOptionalDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var value))
            throw new InvalidOperationException($"Argument '{propertyName}' must be a number.");
        return value;
    }

    public static IReadOnlyDictionary<string, object?>? GetOptionalObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;
        if (property.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Argument '{propertyName}' must be an object.");

        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in property.EnumerateObject())
            dict[item.Name] = ConvertValue(item.Value);
        return dict;
    }

    private static object? ConvertValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when value.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertValue).ToList(),
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => ConvertValue(p.Value), StringComparer.OrdinalIgnoreCase),
            _ => value.ToString()
        };
    }
}
