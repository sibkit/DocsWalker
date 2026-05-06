using System.Globalization;
using System.Text.Json.Nodes;
using DocsWalker.Core.Yaml;
using SharpYaml.Events;

namespace DocsWalker.Core.Schema;

public sealed class SchemaLoadException : Exception
{
    public string Code { get; }
    public string? FilePath { get; }

    public SchemaLoadException(string code, string? filePath, string message)
        : base(message)
    {
        Code = code;
        FilePath = filePath;
    }
}

/// <summary>
/// Парсер мета-схемы и Схемы под refs-модель v4. Использует event-stream API SharpYaml
/// (см. docs/Стек.yml/«YAML-парсер»). Никакой reflection — AOT-совместимо.
/// </summary>
public static class SchemaLoader
{
    public const int SupportedMetaSchemaVersion = 4;

    public static MetaSchemaDocument LoadMetaSchema(string filePath) =>
        LoadFile(filePath, ParseMetaSchema);

    public static SchemaDocument LoadSchema(string filePath) =>
        LoadFile(filePath, ParseSchema);

    private static T LoadFile<T>(string filePath, Func<YamlReader, string, T> parse)
    {
        if (!File.Exists(filePath))
        {
            throw new SchemaLoadException(
                "file_not_found",
                filePath,
                $"Файл '{filePath}' не найден.");
        }

        using var stream = File.OpenRead(filePath);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var yr = new YamlReader(reader, filePath);
        try
        {
            return parse(yr, filePath);
        }
        catch (SchemaLoadException)
        {
            throw;
        }
        catch (YamlReadException ex)
        {
            throw new SchemaLoadException(ex.Code, ex.FilePath, ex.Message);
        }
        catch (Exception ex)
        {
            throw new SchemaLoadException(
                "yaml_parse_error",
                filePath,
                $"Ошибка разбора YAML: {ex.Message}");
        }
    }

    private static MetaSchemaDocument ParseMetaSchema(YamlReader r, string filePath)
    {
        r.Expect<StreamStart>();
        r.Expect<DocumentStart>();
        r.Expect<MappingStart>();

        int? version = null;
        string? name = null;
        string? description = null;
        IReadOnlyList<string>? primitiveTypes = null;

        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();
            switch (key)
            {
                case "meta_schema_version": version = ReadInt(r, key); break;
                case "name": name = r.NextScalarValue(); break;
                case "description": description = r.NextScalarValue(); break;
                case "primitive_types": primitiveTypes = ReadStringList(r); break;
                // Структура schema_root / type_definition / ref_def фиксирована v4 и
                // заложена в код валидатора; пропускаем — храним только верхние поля.
                default: r.SkipValue(); break;
            }
        }

        r.Expect<MappingEnd>();
        r.Expect<DocumentEnd>();
        r.Expect<StreamEnd>();

        Require(filePath, "meta_schema_version", version is not null);
        Require(filePath, "name", name is not null);
        Require(filePath, "description", description is not null);
        Require(filePath, "primitive_types", primitiveTypes is not null);

        if (version!.Value != SupportedMetaSchemaVersion)
            throw new SchemaLoadException(
                "unsupported_meta_schema_version",
                filePath,
                $"Поддерживается только meta_schema_version={SupportedMetaSchemaVersion}; в файле — {version.Value}.");

        return new MetaSchemaDocument(
            version.Value,
            name!,
            description!,
            primitiveTypes!);
    }

    private static SchemaDocument ParseSchema(YamlReader r, string filePath)
    {
        r.Expect<StreamStart>();
        r.Expect<DocumentStart>();
        r.Expect<MappingStart>();

        string? description = null;
        IReadOnlyList<TypeDefinition>? types = null;

        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();
            switch (key)
            {
                case "description": description = r.NextScalarValue(); break;
                case "types": types = ReadTypeList(r); break;
                default: r.SkipValue(); break;
            }
        }

        r.Expect<MappingEnd>();
        r.Expect<DocumentEnd>();
        r.Expect<StreamEnd>();

        Require(filePath, "description", description is not null);
        Require(filePath, "types", types is not null);

        return new SchemaDocument(description!, types!);
    }

    private static IReadOnlyList<TypeDefinition> ReadTypeList(YamlReader r)
    {
        r.Expect<SequenceStart>();
        var list = new List<TypeDefinition>();
        while (r.Peek() is MappingStart)
        {
            list.Add(ReadType(r));
        }
        r.Expect<SequenceEnd>();
        return list;
    }

    private static TypeDefinition ReadType(YamlReader r)
    {
        r.Expect<MappingStart>();

        string? name = null;
        string? description = null;
        TitleSource? titleSource = null;
        bool? textRequired = null;
        IReadOnlyList<string>? pathTargets = null;
        IReadOnlyList<RefDef>? outRefs = null;

        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();
            switch (key)
            {
                case "name": name = r.NextScalarValue(); break;
                case "description": description = r.NextScalarValue(); break;
                case "title_source": titleSource = ParseTitleSource(r.NextScalarValue()); break;
                case "text_required": textRequired = ReadBool(r, key); break;
                case "path_targets": pathTargets = ReadStringList(r); break;
                case "out_refs": outRefs = ReadRefDefList(r); break;
                default: r.SkipValue(); break;
            }
        }

        r.Expect<MappingEnd>();

        if (name is null)
            throw NewError("invalid_schema", "Тип в types[] без поля 'name'.");
        if (titleSource is null)
            throw NewError("invalid_schema", $"Тип '{name}' без поля 'title_source'.");
        if (textRequired is null)
            throw NewError("invalid_schema", $"Тип '{name}' без поля 'text_required'.");
        if (pathTargets is null)
            throw NewError("invalid_schema", $"Тип '{name}' без поля 'path_targets'.");

        return new TypeDefinition(
            name,
            description,
            titleSource.Value,
            textRequired.Value,
            pathTargets,
            outRefs ?? Array.Empty<RefDef>());
    }

    private static IReadOnlyList<RefDef> ReadRefDefList(YamlReader r)
    {
        r.Expect<SequenceStart>();
        var list = new List<RefDef>();
        while (r.Peek() is MappingStart)
        {
            list.Add(ReadRefDef(r));
        }
        r.Expect<SequenceEnd>();
        return list;
    }

    private static RefDef ReadRefDef(YamlReader r)
    {
        r.Expect<MappingStart>();

        string? name = null;
        IReadOnlyList<string>? targetTypes = null;
        Cardinality? cardinality = null;
        bool? required = null;
        string? description = null;

        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();
            switch (key)
            {
                case "name": name = r.NextScalarValue(); break;
                case "target_types": targetTypes = ReadStringList(r); break;
                case "cardinality": cardinality = ParseCardinality(r.NextScalarValue()); break;
                case "required": required = ReadBool(r, key); break;
                case "description": description = r.NextScalarValue(); break;
                default: r.SkipValue(); break;
            }
        }

        r.Expect<MappingEnd>();

        if (name is null)
            throw NewError("invalid_schema", "ref_def без поля 'name'.");
        if (targetTypes is null)
            throw NewError("invalid_schema", $"ref_def '{name}' без поля 'target_types'.");
        if (cardinality is null)
            throw NewError("invalid_schema", $"ref_def '{name}' без поля 'cardinality'.");
        if (required is null)
            throw NewError("invalid_schema", $"ref_def '{name}' без поля 'required'.");

        return new RefDef(name, targetTypes, cardinality.Value, required.Value, description);
    }

    private static IReadOnlyList<string> ReadStringList(YamlReader r)
    {
        r.Expect<SequenceStart>();
        var list = new List<string>();
        while (r.Peek() is Scalar)
        {
            list.Add(r.NextScalarValue());
        }
        r.Expect<SequenceEnd>();
        return list;
    }

    private static int ReadInt(YamlReader r, string fieldName)
    {
        var raw = r.NextScalarValue();
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw NewError("invalid_field", $"Поле '{fieldName}': ожидалось целое, получено '{raw}'.");
        return v;
    }

    private static bool ReadBool(YamlReader r, string fieldName)
    {
        var raw = r.NextScalarValue();
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;
        throw NewError("invalid_field", $"Поле '{fieldName}': ожидалось true/false, получено '{raw}'.");
    }

    private static TitleSource ParseTitleSource(string raw) => raw switch
    {
        "filename" => TitleSource.Filename,
        "dirname" => TitleSource.Dirname,
        "inline_key" => TitleSource.InlineKey,
        _ => throw NewError("invalid_schema", $"Неизвестный title_source '{raw}'."),
    };

    private static Cardinality ParseCardinality(string raw) => raw switch
    {
        "one" => Cardinality.One,
        "many" => Cardinality.Many,
        _ => throw NewError("invalid_schema", $"Неизвестная cardinality '{raw}'."),
    };

    private static void Require(string filePath, string field, bool ok)
    {
        if (!ok)
            throw new SchemaLoadException(
                "missing_field",
                filePath,
                $"В корне отсутствует обязательное поле '{field}'.");
    }

    private static SchemaLoadException NewError(string code, string message) =>
        new(code, null, message);
}

/// <summary>
/// Сериализация мета-схемы и схемы в JSON для CLI/MCP-вывода (get_meta_schema, get_schema).
/// AOT-совместимо: используем JsonNode без рефлексии.
/// </summary>
public static class SchemaJson
{
    public static JsonObject ToJson(MetaSchemaDocument doc) => new()
    {
        ["meta_schema_version"] = doc.Version,
        ["name"] = doc.Name,
        ["description"] = doc.Description,
        ["primitive_types"] = StringsToJson(doc.PrimitiveTypes),
    };

    public static JsonObject ToJson(SchemaDocument doc)
    {
        var obj = new JsonObject
        {
            ["description"] = doc.Description,
        };
        var types = new JsonArray();
        foreach (var t in doc.Types) types.Add((JsonNode?)TypeDefinitionToJson(t));
        obj["types"] = types;
        return obj;
    }

    private static JsonObject TypeDefinitionToJson(TypeDefinition t)
    {
        var obj = new JsonObject
        {
            ["name"] = t.Name,
        };
        if (t.Description is not null) obj["description"] = t.Description;
        obj["title_source"] = TitleSourceToString(t.TitleSource);
        obj["text_required"] = t.TextRequired;
        obj["path_targets"] = StringsToJson(t.PathTargets);
        if (t.OutRefs.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var rd in t.OutRefs) arr.Add((JsonNode?)RefDefToJson(rd));
            obj["out_refs"] = arr;
        }
        return obj;
    }

    private static JsonObject RefDefToJson(RefDef rd)
    {
        var obj = new JsonObject
        {
            ["name"] = rd.Name,
            ["target_types"] = StringsToJson(rd.TargetTypes),
            ["cardinality"] = CardinalityToString(rd.Cardinality),
            ["required"] = rd.Required,
        };
        if (rd.Description is not null) obj["description"] = rd.Description;
        return obj;
    }

    private static JsonArray StringsToJson(IReadOnlyList<string> strings)
    {
        var arr = new JsonArray();
        foreach (var s in strings) arr.Add((JsonNode?)s);
        return arr;
    }

    private static string TitleSourceToString(TitleSource t) => t switch
    {
        TitleSource.Filename => "filename",
        TitleSource.Dirname => "dirname",
        TitleSource.InlineKey => "inline_key",
        _ => t.ToString(),
    };

    private static string CardinalityToString(Cardinality c) => c switch
    {
        Cardinality.One => "one",
        Cardinality.Many => "many",
        _ => c.ToString(),
    };
}
