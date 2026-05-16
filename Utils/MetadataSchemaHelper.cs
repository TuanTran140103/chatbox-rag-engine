using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Json.Schema;
using Qdrant.Client.Grpc;

namespace MarkdownGenQAs.Utils;

public static class MetadataSchemaHelper
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static JsonSerializerOptions GetJsonSerializerOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
    }

    public static (bool IsValid, string? ErrorMessage) ValidateJsonAgainstSchema(string json, string jsonSchema)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            JsonSchema schema;
            try
            {
                schema = JsonSchema.FromText(jsonSchema);
            }
            catch (Exception ex)
            {
                return (false, $"Invalid JSON Schema: {ex.Message}");
            }

            var options = new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical
            };
            var result = schema.Evaluate(doc.RootElement, options);

            if (result.IsValid)
                return (true, null);

            var errors = new List<string>();
            CollectErrors(result, errors);
            return (false, string.Join("; ", errors));
        }
        catch (JsonException ex)
        {
            return (false, $"Invalid JSON: {ex.Message}");
        }
    }

    public static (string json, string schema) CoerceRequiredNulls(string json, string jsonSchema)
    {
        using var schemaDoc = JsonDocument.Parse(jsonSchema);

        var required = new HashSet<string>();
        if (schemaDoc.RootElement.TryGetProperty("required", out var req))
        {
            foreach (var item in req.EnumerateArray())
                required.Add(item.GetString()!);
        }

        if (required.Count == 0) return (json, jsonSchema);

        var defaults = new Dictionary<string, object?>();
        var enumFields = new HashSet<string>();

        if (schemaDoc.RootElement.TryGetProperty("properties", out var props))
        {
            foreach (var prop in props.EnumerateObject())
            {
                if (!required.Contains(prop.Name)) continue;

                if (HasEnumConstraint(prop.Value))
                {
                    defaults[prop.Name] = "other";
                    enumFields.Add(prop.Name);
                }
                else
                {
                    defaults[prop.Name] = GetDefaultForType(prop.Value);
                }
            }
        }

        if (defaults.Count == 0) return (json, jsonSchema);

        var node = JsonNode.Parse(json);
        var changed = false;
        if (node is JsonObject obj)
        {
            foreach (var (field, defaultVal) in defaults)
            {
                if (obj.TryGetPropertyValue(field, out var val) && (val is null || val.ToString() == ""))
                {
                    obj[field] = JsonValue.Create(defaultVal);
                    changed = true;
                }
            }
        }

        var fixedJson = changed ? node!.ToJsonString(IndentedJsonOptions) : json;

        var fixedSchema = jsonSchema;
        if (enumFields.Count > 0 && changed)
        {
            fixedSchema = PatchEnumWithOther(jsonSchema, enumFields);
        }

        return (fixedJson, fixedSchema);
    }

    public static string GenerateDefaultJson(string jsonSchema)
    {
        using var schemaDoc = JsonDocument.Parse(jsonSchema);
        var obj = new JsonObject();

        if (schemaDoc.RootElement.TryGetProperty("properties", out var props))
        {
            foreach (var prop in props.EnumerateObject())
            {
                if (HasEnumConstraint(prop.Value))
                    obj[prop.Name] = JsonValue.Create("other");
                else
                    obj[prop.Name] = JsonValue.Create(GetDefaultForType(prop.Value));
            }
        }

        return obj.ToJsonString(IndentedJsonOptions);
    }

    private static string PatchEnumWithOther(string jsonSchema, HashSet<string> enumFields)
    {
        var node = JsonNode.Parse(jsonSchema);
        if (node is not JsonObject root) return jsonSchema;

        if (!root.TryGetPropertyValue("properties", out var propsNode) || propsNode is not JsonObject props)
            return jsonSchema;

        foreach (var field in enumFields)
        {
            if (!props.TryGetPropertyValue(field, out var propNode) || propNode is not JsonObject prop)
                continue;

            AddOtherToEnum(prop);
        }

        return node.ToJsonString(IndentedJsonOptions);
    }

    private static void AddOtherToEnum(JsonObject prop)
    {
        if (prop.TryGetPropertyValue("enum", out var enumNode) && enumNode is JsonArray enumArr)
        {
            if (!enumArr.Any(e => e?.ToString() == "other"))
                enumArr.Add(JsonValue.Create("other"));
            return;
        }

        if (prop.TryGetPropertyValue("oneOf", out var oneOfNode) && oneOfNode is JsonArray oneOfArr)
        {
            foreach (var branch in oneOfArr.OfType<JsonObject>())
            {
                if (branch.TryGetPropertyValue("enum", out var brEnum) && brEnum is JsonArray brArr)
                {
                    if (!brArr.Any(e => e?.ToString() == "other"))
                        brArr.Add(JsonValue.Create("other"));
                }
            }
        }
    }

    private static bool HasEnumConstraint(JsonElement propValue)
    {
        if (propValue.TryGetProperty("enum", out _))
            return true;

        if (propValue.TryGetProperty("oneOf", out var oneOfEl))
        {
            foreach (var branch in oneOfEl.EnumerateArray())
            {
                if (branch.TryGetProperty("enum", out _))
                    return true;
            }
        }

        return false;
    }

    private static object GetDefaultForType(JsonElement propValue)
    {
        if (propValue.TryGetProperty("format", out var formatEl))
        {
            var fmt = formatEl.GetString();
            if (string.Equals(fmt, "date", StringComparison.OrdinalIgnoreCase))
                return "0001-01-01";
            if (string.Equals(fmt, "date-time", StringComparison.OrdinalIgnoreCase))
                return "0001-01-01T00:00:00";
            if (string.Equals(fmt, "time", StringComparison.OrdinalIgnoreCase))
                return "00:00:00";
        }

        var type = ResolveJsonType(propValue);
        return type switch
        {
            "integer" or "number" => 0,
            "boolean"             => false,
            "array"               => Array.Empty<object>(),
            _                     => ""
        };
    }

    private static string? ResolveJsonType(JsonElement propValue)
    {
        if (propValue.TryGetProperty("type", out var typeEl))
        {
            if (typeEl.ValueKind == JsonValueKind.String)
                return typeEl.GetString();

            if (typeEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in typeEl.EnumerateArray())
                {
                    var val = t.GetString();
                    if (val != "null") return val;
                }
            }
        }

        if (propValue.TryGetProperty("oneOf", out var oneOfEl))
        {
            foreach (var branch in oneOfEl.EnumerateArray())
            {
                var result = ResolveJsonType(branch);
                if (result != null) return result;
            }
        }

        if (propValue.TryGetProperty("format", out var formatEl))
        {
            var fmt = formatEl.GetString();
            if (fmt == "date" || fmt == "date-time" || fmt == "time")
                return "string";
        }

        if (propValue.TryGetProperty("properties", out _))
            return "object";

        if (propValue.TryGetProperty("items", out _))
            return "array";

        return null;
    }

    private static void CollectErrors(EvaluationResults results, List<string> errors)
    {
        if (results.Errors is { Count: > 0 })
        {
            foreach (var kvp in results.Errors)
                errors.Add($"[{kvp.Key}] {kvp.Value}");
        }

        if (results.Details is { Count: > 0 })
        {
            foreach (var detail in results.Details)
            {
                if (results.IsValid && IsOneOfBranch(detail))
                    continue;

                CollectErrors(detail, errors);
            }
        }
    }

    private static bool IsOneOfBranch(EvaluationResults results)
    {
        return results.SchemaLocation?.OriginalString?.Contains("/oneOf/") == true
            || results.SchemaLocation?.OriginalString?.Contains("/anyOf/") == true;
    }

    public static Dictionary<string, Value> ConvertToPayload(string? jsonValues, string? jsonSchema = null)
    {
        var payload = new Dictionary<string, Value>();
        if (string.IsNullOrWhiteSpace(jsonValues)) return payload;

        var schemaTypes = jsonSchema != null ? ParseSchemaTypes(jsonSchema) : new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(jsonValues);
        FlattenJsonElement(doc.RootElement, string.Empty, schemaTypes, payload);
        return payload;
    }

    public static PayloadSchemaType GetPayloadSchemaType(string jsonSchema, string fieldPath)
    {
        var types = ParseSchemaTypes(jsonSchema);
        if (!types.TryGetValue(fieldPath, out var typeStr)) return PayloadSchemaType.Keyword;

        return typeStr switch
        {
            "integer" => PayloadSchemaType.Integer,
            "number" => PayloadSchemaType.Float,
            "boolean" => PayloadSchemaType.Bool,
            _ => PayloadSchemaType.Keyword
        };
    }

    private static Dictionary<string, string> ParseSchemaTypes(string jsonSchema)
    {
        var types = new Dictionary<string, string>();
        using var schemaDoc = JsonDocument.Parse(jsonSchema);
        if (schemaDoc.RootElement.TryGetProperty("properties", out var props))
            ParseSchemaProperties(props, string.Empty, types);
        return types;
    }

    private static void ParseSchemaProperties(JsonElement properties, string prefix, Dictionary<string, string> types)
    {
        foreach (var prop in properties.EnumerateObject())
        {
            var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
            var typeStr = ResolveSchemaType(prop.Value);

            if (typeStr != null)
                types[key] = typeStr;

            if (typeStr == "object" && prop.Value.TryGetProperty("properties", out var nestedProps))
                ParseSchemaProperties(nestedProps, key, types);
        }
    }

    private static string? ResolveSchemaType(JsonElement propValue)
    {
        if (propValue.TryGetProperty("type", out var typeEl))
        {
            if (typeEl.ValueKind == JsonValueKind.String)
                return typeEl.GetString();
            if (typeEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in typeEl.EnumerateArray())
                {
                    var val = t.GetString();
                    if (val != "null") return val;
                }
            }
        }
        if (propValue.TryGetProperty("oneOf", out var oneOfEl))
        {
            foreach (var branch in oneOfEl.EnumerateArray())
            {
                var result = ResolveSchemaType(branch);
                if (result != null) return result;
            }
        }
        if (propValue.TryGetProperty("properties", out _))
            return "object";
        if (propValue.TryGetProperty("items", out _))
            return "array";
        if (propValue.TryGetProperty("format", out _))
            return "string";
        return null;
    }

    private static void FlattenJsonElement(JsonElement element, string prefix, Dictionary<string, string> schemaTypes, Dictionary<string, Value> payload)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                    FlattenJsonElement(prop.Value, key, schemaTypes, payload);
                }
                break;

            case JsonValueKind.Array:
                var list = new List<Value>();
                foreach (var item in element.EnumerateArray())
                {
                    var converted = ConvertToQdrantValue(item, null);
                    if (converted != null)
                        list.Add(converted);
                }
                if (list.Count > 0)
                    payload[prefix] = list.ToArray();
                break;

            case JsonValueKind.String:
                payload[prefix] = ConvertStringToTypedValue(element.GetString()!, schemaTypes.GetValueOrDefault(prefix));
                break;

            case JsonValueKind.Number:
                if (element.TryGetInt64(out long longVal))
                    payload[prefix] = longVal;
                else
                    payload[prefix] = element.GetDouble();
                break;

            case JsonValueKind.True:
                payload[prefix] = true;
                break;

            case JsonValueKind.False:
                payload[prefix] = false;
                break;
        }
    }

    private static Value ConvertStringToTypedValue(string value, string? schemaType)
    {
        return schemaType switch
        {
            "integer" when long.TryParse(value, out var l) => l,
            "number" when double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            "boolean" when bool.TryParse(value, out var b) => b,
            _ => value
        };
    }

    private static Value? ConvertToQdrantValue(JsonElement element, string? schemaType)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => ConvertStringToTypedValue(element.GetString()!, schemaType),
            JsonValueKind.Number when element.TryGetInt64(out long l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
