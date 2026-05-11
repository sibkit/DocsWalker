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
/// Парсер мета-схемы и Схемы под refs-модель v6 (tree-scopes + root-as-declared-type).
/// Использует event-stream API SharpYaml (см. docs/Стек.yml/«YAML-парсер»). Никакой
/// reflection — AOT-совместимо.
/// </summary>
public static class SchemaLoader
{
    public const int SupportedMetaSchemaVersion = 6;

    public static MetaSchemaDocument LoadMetaSchema(string filePath) =>
        LoadFile(filePath, ParseMetaSchema);

    public static SchemaDocument LoadSchema(string filePath) =>
        LoadFile(filePath, ParseSchema);

    /// <summary>
    /// Разбирает Схему из готовой YAML-строки. <paramref name="virtualPath"/> подставляется
    /// в сообщения об ошибках для согласованности с file-based загрузкой; используется
    /// командой <c>update-schema</c>, где YAML приходит через MCP-arguments.
    /// </summary>
    public static SchemaDocument LoadSchemaFromString(string yamlText, string virtualPath) =>
        LoadFromString(yamlText, virtualPath, ParseSchema);

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
        return ParseWithReader(reader, filePath, parse);
    }

    private static T LoadFromString<T>(string yamlText, string virtualPath, Func<YamlReader, string, T> parse)
    {
        using var reader = new StringReader(yamlText);
        return ParseWithReader(reader, virtualPath, parse);
    }

    private static T ParseWithReader<T>(TextReader reader, string filePath, Func<YamlReader, string, T> parse)
    {
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
        // Sections: остальные верхнеуровневые ключи (schema_root / tree_definition /
        // type_definition / ref_def и любые расширения) — generic YAML→JsonNode для
        // полной отдачи через get-meta-schema (см. SchemaJson.ToJson(MetaSchemaDocument)).
        var sections = new Dictionary<string, JsonNode>(StringComparer.Ordinal);

        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();
            switch (key)
            {
                case "meta_schema_version": version = ReadInt(r, key); break;
                case "name": name = r.NextScalarValue(); break;
                case "description": description = r.NextScalarValue(); break;
                case "primitive_types": primitiveTypes = ReadStringList(r); break;
                default: sections[key] = ReadAnyValue(r); break;
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
            primitiveTypes!,
            sections);
    }

    /// <summary>
    /// Generic YAML→JsonNode для произвольных значений: mapping → JsonObject,
    /// sequence → JsonArray, скаляр → JsonValue с эвристикой типа (true/false → bool,
    /// целое → long, иначе → string). Используется для секций мета-схемы, форма
    /// которых не имеет фиксированного DTO в коде, но должна полностью отдаваться
    /// клиенту через <c>get-meta-schema</c>.
    /// </summary>
    private static JsonNode ReadAnyValue(YamlReader r)
    {
        var ev = r.Peek();
        return ev switch
        {
            MappingStart  => ReadAnyMapping(r),
            SequenceStart => ReadAnySequence(r),
            Scalar        => ReadAnyScalar(r),
            _ => throw NewError(
                "yaml_parse_error",
                $"Неожиданное событие YAML при чтении значения: {ev?.GetType().Name ?? "null"}."),
        };
    }

    private static JsonObject ReadAnyMapping(YamlReader r)
    {
        r.Expect<MappingStart>();
        var obj = new JsonObject();
        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();
            obj[key] = ReadAnyValue(r);
        }
        r.Expect<MappingEnd>();
        return obj;
    }

    private static JsonArray ReadAnySequence(YamlReader r)
    {
        r.Expect<SequenceStart>();
        var arr = new JsonArray();
        while (r.Peek() is not SequenceEnd)
        {
            arr.Add(ReadAnyValue(r));
        }
        r.Expect<SequenceEnd>();
        return arr;
    }

    private static JsonNode ReadAnyScalar(YamlReader r)
    {
        var raw = r.NextScalarValue();
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
            return JsonValue.Create(true);
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
            return JsonValue.Create(false);
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return JsonValue.Create(i);
        return JsonValue.Create(raw)!;
    }

    private static SchemaDocument ParseSchema(YamlReader r, string filePath)
    {
        r.Expect<StreamStart>();
        r.Expect<DocumentStart>();
        r.Expect<MappingStart>();

        string? description = null;
        IReadOnlyList<TreeDefinition>? trees = null;
        IReadOnlyList<TypeDefinition>? types = null;
        string? defaultAddressableTree = null;

        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();
            switch (key)
            {
                case "description": description = r.NextScalarValue(); break;
                case "trees": trees = ReadTreeList(r); break;
                case "types": types = ReadTypeList(r); break;
                case "default_addressable_tree": defaultAddressableTree = r.NextScalarValue(); break;
                default: r.SkipValue(); break;
            }
        }

        r.Expect<MappingEnd>();
        r.Expect<DocumentEnd>();
        r.Expect<StreamEnd>();

        Require(filePath, "description", description is not null);
        Require(filePath, "trees", trees is not null);
        Require(filePath, "types", types is not null);

        return new SchemaDocument(description!, trees!, types!, defaultAddressableTree);
    }

    private static IReadOnlyList<TreeDefinition> ReadTreeList(YamlReader r)
    {
        r.Expect<SequenceStart>();
        var list = new List<TreeDefinition>();
        while (r.Peek() is MappingStart)
        {
            list.Add(ReadTree(r));
        }
        r.Expect<SequenceEnd>();
        return list;
    }

    private static TreeDefinition ReadTree(YamlReader r)
    {
        r.Expect<MappingStart>();
        string? name = null;
        string? description = null;
        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();
            switch (key)
            {
                case "name": name = r.NextScalarValue(); break;
                case "description": description = r.NextScalarValue(); break;
                default: r.SkipValue(); break;
            }
        }
        r.Expect<MappingEnd>();
        if (name is null)
            throw NewError("invalid_schema", "tree_definition без поля 'name'.");
        return new TreeDefinition(name, description);
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

        return new TypeDefinition(
            name,
            description,
            titleSource.Value,
            textRequired.Value,
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
        string? tree = null;
        Cardinality? cardinality = null;
        bool? required = null;
        bool? uniqueSiblingTitles = null;
        string? description = null;

        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();
            switch (key)
            {
                case "name": name = r.NextScalarValue(); break;
                case "target_types": targetTypes = ReadStringList(r); break;
                case "tree": tree = r.NextScalarValue(); break;
                case "cardinality": cardinality = ParseCardinality(r.NextScalarValue()); break;
                case "required": required = ReadBool(r, key); break;
                case "unique_sibling_titles": uniqueSiblingTitles = ReadBool(r, key); break;
                case "description": description = r.NextScalarValue(); break;
                default: r.SkipValue(); break;
            }
        }

        r.Expect<MappingEnd>();

        if (name is null)
            throw NewError("invalid_schema", "ref_def без поля 'name'.");
        if (targetTypes is null)
            throw NewError("invalid_schema", $"ref_def '{name}' без поля 'target_types'.");

        if (tree is not null)
        {
            if (cardinality is not null)
                throw NewError(
                    "invalid_schema",
                    $"ref_def '{name}': при заданном tree поле 'cardinality' указывать запрещено (подразумевается one).");
            if (required is not null)
                throw NewError(
                    "invalid_schema",
                    $"ref_def '{name}': при заданном tree поле 'required' указывать запрещено (подразумевается true).");
            // tree-связь автоматически one + required.
            return new RefDef(name, targetTypes, tree, Cardinality.One, true, description, uniqueSiblingTitles ?? false);
        }

        if (uniqueSiblingTitles is not null)
            throw NewError(
                "invalid_schema",
                $"ref_def '{name}': поле 'unique_sibling_titles' допустимо только при заданном tree (для horizontal-связей запрещено).");

        // Дефолты non-tree refs: cardinality=many, required=false (см. docs/DocsWalker.yml,
        // секция «omit defaults»). Отсутствие поля в YAML = дефолт; повторное чтение
        // describe-type/get-schema, где эти значения опущены сериализатором, остаётся
        // round-trip-совместимым.
        return new RefDef(name, targetTypes, null, cardinality ?? Cardinality.Many, required ?? false, description);
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
    public static JsonObject ToJson(MetaSchemaDocument doc)
    {
        var obj = new JsonObject
        {
            ["meta_schema_version"] = doc.Version,
            ["name"] = doc.Name,
            ["description"] = doc.Description,
            ["primitive_types"] = StringsToJson(doc.PrimitiveTypes),
        };
        // Sections узлы хранятся в DTO в общем владении; DeepClone() — чтобы новый
        // JsonObject не «забрал» parent у исходного дерева. Иначе повторный вызов
        // ToJson на том же документе упадёт — нода уже принадлежит другому дереву.
        foreach (var kv in doc.Sections)
        {
            obj[kv.Key] = kv.Value.DeepClone();
        }
        return obj;
    }

    public static JsonObject ToJson(SchemaDocument doc)
    {
        var obj = new JsonObject
        {
            ["description"] = doc.Description,
        };
        var trees = new JsonArray();
        foreach (var tr in doc.Trees) trees.Add((JsonNode?)TreeDefinitionToJson(tr));
        obj["trees"] = trees;
        var types = new JsonArray();
        foreach (var t in doc.Types) types.Add((JsonNode?)TypeDefinitionToJson(t));
        obj["types"] = types;
        if (doc.DefaultAddressableTree is not null)
            obj["default_addressable_tree"] = doc.DefaultAddressableTree;
        return obj;
    }

    private static JsonObject TreeDefinitionToJson(TreeDefinition t)
    {
        var obj = new JsonObject
        {
            ["name"] = t.Name,
        };
        if (t.Description is not null) obj["description"] = t.Description;
        return obj;
    }

    /// <summary>
    /// Сериализация type_definition без <c>title_source</c>: это контракт «движок ↔ docs/»,
    /// LLM его не знает (см. docs/DocsWalker.yml/«LLM не видит файлы»). Поле существует в
    /// исходнике <c>Схема.yml</c> и валидируется мета-схемой, но в публичный JSON
    /// <c>get-schema</c> не выводится — выровнено с <see cref="ReadApi.DescribeType"/>.
    /// </summary>
    private static JsonObject TypeDefinitionToJson(TypeDefinition t)
    {
        var obj = new JsonObject
        {
            ["name"] = t.Name,
        };
        if (t.Description is not null) obj["description"] = t.Description;
        obj["text_required"] = t.TextRequired;
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
        };
        if (rd.Tree is not null)
        {
            obj["tree"] = rd.Tree;
            if (rd.UniqueSiblingTitles) obj["unique_sibling_titles"] = true;
        }
        else
        {
            // Опускаем дефолты non-tree refs (cardinality=many, required=false):
            // отсутствие поля = дефолт, парсер расшифровывает обратно.
            if (rd.Cardinality != Cardinality.Many)
                obj["cardinality"] = CardinalityToString(rd.Cardinality);
            if (rd.Required) obj["required"] = true;
        }
        if (rd.Description is not null) obj["description"] = rd.Description;
        return obj;
    }

    private static JsonArray StringsToJson(IReadOnlyList<string> strings)
    {
        var arr = new JsonArray();
        foreach (var s in strings) arr.Add((JsonNode?)s);
        return arr;
    }

    private static string CardinalityToString(Cardinality c) => c switch
    {
        Cardinality.One => "one",
        Cardinality.Many => "many",
        _ => c.ToString(),
    };
}
