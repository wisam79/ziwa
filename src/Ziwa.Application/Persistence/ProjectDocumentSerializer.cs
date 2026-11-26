using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ziwa.Domain.Models;
using Ziwa.Domain.Validation;

namespace Ziwa.Application.Persistence;

public static class ProjectDocumentSerializer
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxDocumentBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        IgnoreReadOnlyProperties = true,
        MaxDepth = 128,
        Converters = { new ImmutableArrayConverterFactory() },
    };

    public static byte[] Serialize(ZiwaDocument document)
    {
        Validate(document);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        if (data.Length > MaxDocumentBytes)
        {
            throw new InvalidDataException("Project document exceeds the supported size limit.");
        }

        return data;
    }

    public static ZiwaDocument Deserialize(ReadOnlySpan<byte> data, IEnumerable<IDocumentMigrator>? migrators = null)
    {
        if (data.Length > MaxDocumentBytes)
        {
            throw new InvalidDataException("Project document exceeds the supported size limit.");
        }

        ValidateUniqueProperties(data);
        try
        {
            JsonObject root = JsonNode.Parse(data, documentOptions: new JsonDocumentOptions { MaxDepth = 128 }) as JsonObject
                ?? throw new InvalidDataException("Project document must be an object.");
            root = ApplyMigrations(root, CurrentSchemaVersion, migrators ?? []);
            ZiwaDocument document = root.Deserialize<ZiwaDocument>(Options)
                ?? throw new InvalidDataException("Project document is empty.");
            Validate(document);
            return document;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException("Project document is invalid or uses an unsupported type.", ex);
        }
    }

    public static JsonObject ApplyMigrations(JsonObject document, int currentVersion, IEnumerable<IDocumentMigrator> migrators)
    {
        JsonObject result = (JsonObject)document.DeepClone();
        int version = result["schemaVersion"]?.GetValue<int>() ?? 0;
        if (version < 1 || version > currentVersion)
        {
            throw new InvalidDataException("Unsupported project schema version.");
        }

        IDocumentMigrator[] steps = migrators.ToArray();
        while (version < currentVersion)
        {
            IDocumentMigrator[] matches = steps.Where(m => m.FromVersion == version && m.ToVersion == version + 1).ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidDataException("A unique sequential project migration is required.");
            }

            result = matches[0].Migrate(result) ?? throw new InvalidDataException("Project migration returned no document.");
            version++;
            if (result["schemaVersion"]?.GetValue<int>() != version)
            {
                throw new InvalidDataException("Project migration did not advance the schema version.");
            }
        }

        return result;
    }

    public static void ValidateUniqueProperties(ReadOnlySpan<byte> data)
    {
        try
        {
            var reader = new Utf8JsonReader(data, new JsonReaderOptions { MaxDepth = 128 });
            var objects = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) { objects.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase)); }
                else if (reader.TokenType == JsonTokenType.EndObject) { objects.Pop(); }
                else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                {
                    throw new InvalidDataException("Duplicate JSON property in persistence data.");
                }
            }
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid persistence JSON.", ex); }
    }

    public static string CanonicalFingerprint(ZiwaDocument document)
    {
        JsonNode? node = JsonNode.Parse(Serialize(document), documentOptions: new JsonDocumentOptions { MaxDepth = 128 });
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(Sort(node), Options);
        return Convert.ToHexStringLower(SHA256.HashData(data));
    }

    private static JsonNode? Sort(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var sorted = new JsonObject();
            foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sorted.Add(pair.Key, Sort(pair.Value));
            }

            return sorted;
        }

        if (node is JsonArray array)
        {
            return new JsonArray(array.Select(Sort).ToArray());
        }

        return node?.DeepClone();
    }

    private sealed class ImmutableArrayConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ImmutableArray<>);
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(ImmutableArrayConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()), nonPublic: true)!;
    }

    private sealed class ImmutableArrayConverter<T> : JsonConverter<ImmutableArray<T>>
    {
        public override bool HandleNull => true;
        public override ImmutableArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? default : (JsonSerializer.Deserialize<T[]>(ref reader, options) ?? []).ToImmutableArray();
        public override void Write(Utf8JsonWriter writer, ImmutableArray<T> value, JsonSerializerOptions options)
        {
            if (value.IsDefault) { writer.WriteNullValue(); }
            else { JsonSerializer.Serialize(writer, value.ToArray(), options); }
        }
    }

    private static void Validate(ZiwaDocument document)
    {
        ValidationResult validation = SchemaValidator.ValidateDocument(document);
        if (!validation.IsValid || document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Invalid project document: {string.Join("; ", validation.Errors)}");
        }
    }
}
