using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LsmWriteDb.Storage;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationalColumnType
{
    Text,
    Int,
    BigInt,
    Boolean,
    Double
}

public sealed record RelationalColumnDefinition(
    string Name,
    RelationalColumnType Type,
    bool IsPrimaryKey = false,
    bool IsNullable = false);

public sealed class RelationalSchemaException : ArgumentException
{
    public RelationalSchemaException(string message) : base(message) { }
}

public sealed record RelationalTableSchema(
    string Table,
    IReadOnlyList<RelationalColumnDefinition> Columns)
{
    public RelationalColumnDefinition PrimaryKey => Columns.Single(column => column.IsPrimaryKey);

    public void ValidateDefinition()
    {
        TableNames.Normalize(Table);
        if (Columns.Count == 0)
            throw new RelationalSchemaException("A relational table must define at least one column.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in Columns)
        {
            ValidateIdentifier(column.Name, "Column name");
            if (!names.Add(column.Name))
                throw new RelationalSchemaException($"Duplicate column '{column.Name}'.");
            if (column.IsPrimaryKey && column.IsNullable)
                throw new RelationalSchemaException("A primary key cannot be nullable.");
        }

        if (Columns.Count(column => column.IsPrimaryKey) != 1)
            throw new RelationalSchemaException("A relational table must define exactly one primary key.");
    }

    public void ValidateRow(string key, string json)
    {
        ValidateDefinition();
        ValidatePrimaryKey(key);

        using var document = ParseRowJson(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new RelationalSchemaException("Relational row values must be JSON objects.");

        var properties = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);
        if (properties.ContainsKey(PrimaryKey.Name))
            throw new RelationalSchemaException($"Primary key '{PrimaryKey.Name}' belongs in the table key, not the JSON value.");

        foreach (var property in properties.Keys)
        {
            if (!Columns.Any(column => string.Equals(column.Name, property, StringComparison.OrdinalIgnoreCase)))
                throw new RelationalSchemaException($"Unknown column '{property}' for relational table '{Table}'.");
        }

        foreach (var column in Columns.Where(column => !column.IsPrimaryKey))
        {
            if (!properties.TryGetValue(column.Name, out var value))
            {
                if (!column.IsNullable)
                    throw new RelationalSchemaException($"Required column '{column.Name}' is missing.");
                continue;
            }

            if (value.ValueKind == JsonValueKind.Null)
            {
                if (!column.IsNullable)
                    throw new RelationalSchemaException($"Column '{column.Name}' cannot be null.");
                continue;
            }

            if (!MatchesType(value, column.Type))
                throw new RelationalSchemaException($"Column '{column.Name}' must have type {column.Type.ToString().ToLowerInvariant()}.");
        }
    }

    private static JsonDocument ParseRowJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new RelationalSchemaException($"Relational row values must be valid JSON: {ex.Message}");
        }
    }

    private void ValidatePrimaryKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new RelationalSchemaException("Primary key is required.");

        if (PrimaryKey.Type == RelationalColumnType.Int && !int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || PrimaryKey.Type == RelationalColumnType.BigInt && !long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || PrimaryKey.Type == RelationalColumnType.Boolean && !bool.TryParse(key, out _)
            || PrimaryKey.Type == RelationalColumnType.Double && !double.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            throw new RelationalSchemaException($"Primary key '{PrimaryKey.Name}' must have type {PrimaryKey.Type.ToString().ToLowerInvariant()}.");
        }
    }

    private static bool MatchesType(JsonElement value, RelationalColumnType type)
    {
        return type switch
        {
            RelationalColumnType.Text => value.ValueKind == JsonValueKind.String,
            RelationalColumnType.Int => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            RelationalColumnType.BigInt => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            RelationalColumnType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            RelationalColumnType.Double => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out _),
            _ => false
        };
    }

    private static void ValidateIdentifier(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !((value[0] == '_') || char.IsLetter(value[0]))
            || value.Any(character => !(character == '_' || char.IsLetterOrDigit(character))))
        {
            throw new RelationalSchemaException($"{label} '{value}' is invalid.");
        }
    }
}