using System.Text.Json;

namespace LsmWriteDb.Indexes;

internal static class JsonValueAccessor
{
    public static bool TryReadComparableValue(string json, IReadOnlyList<string> path, out string value)
    {
        if (path.Count == 0)
        {
            value = json;
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var current = document.RootElement;

            foreach (var property in path)
            {
                if (current.ValueKind != JsonValueKind.Object
                    || !current.TryGetProperty(property, out var next))
                {
                    value = string.Empty;
                    return false;
                }

                current = next;
            }

            value = ToComparableValue(current);
            return true;
        }
        catch (JsonException)
        {
            value = string.Empty;
            return false;
        }
    }

    public static bool IsValidJson(string value, out string? error)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ToComparableValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => element.GetRawText()
        };
    }
}
