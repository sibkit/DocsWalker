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

public static class SchemaLoader
{
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
        IReadOnlyList<string>? typeKinds = null;
        NodeType? schemaRoot = null;
        NodeType? typeDef = null;
        NodeType? fieldDef = null;
        NodeType? blockDef = null;

        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();
            switch (key)
            {
                case "meta_schema_version": version = ReadInt(r, key); break;
                case "name": name = r.NextScalarValue(); break;
                case "description": description = r.NextScalarValue(); break;
                case "primitive_types": primitiveTypes = ReadStringList(r); break;
                case "type_kinds": typeKinds = ReadStringList(r); break;
                case "schema_root": schemaRoot = ReadInlineNodeType(r, "schema_root"); break;
                case "type_definition": typeDef = ReadInlineNodeType(r, "type_definition"); break;
                case "field_definition": fieldDef = ReadInlineNodeType(r, "field_definition"); break;
                case "block_definition": blockDef = ReadInlineNodeType(r, "block_definition"); break;
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
        Require(filePath, "type_kinds", typeKinds is not null);
        Require(filePath, "schema_root", schemaRoot is not null);
        Require(filePath, "type_definition", typeDef is not null);
        Require(filePath, "field_definition", fieldDef is not null);
        Require(filePath, "block_definition", blockDef is not null);

        return new MetaSchemaDocument(
            version!.Value,
            name!,
            description!,
            primitiveTypes!,
            typeKinds!,
            schemaRoot!,
            typeDef!,
            fieldDef!,
            blockDef!);
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

    private static NodeType ReadInlineNodeType(YamlReader r, string explicitName)
    {
        r.Expect<MappingStart>();

        TypeKind? kind = null;
        string? description = null;
        bool? node = null;
        TitleSourceKind? titleSource = null;
        string? titleField = null;
        string? titleFormat = null;
        IReadOnlyList<FieldDefinition>? fields = null;
        string? keyType = null;
        string? valueType = null;
        string? of = null;
        IReadOnlyList<BlockDefinition>? blocks = null;
        IReadOnlyList<string>? constraints = null;

        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();
            switch (key)
            {
                case "kind": kind = ParseTypeKind(r.NextScalarValue()); break;
                case "description": description = r.NextScalarValue(); break;
                case "node": node = ReadBool(r, key); break;
                case "title_source": titleSource = ParseTitleSource(r.NextScalarValue()); break;
                case "title_field": titleField = r.NextScalarValue(); break;
                case "title_format": titleFormat = r.NextScalarValue(); break;
                case "fields": fields = ReadFieldList(r); break;
                case "key_type": keyType = r.NextScalarValue(); break;
                case "value_type": valueType = r.NextScalarValue(); break;
                case "of": of = r.NextScalarValue(); break;
                case "blocks": blocks = ReadBlockList(r); break;
                case "constraints": constraints = ReadStringList(r); break;
                default: r.SkipValue(); break;
            }
        }

        r.Expect<MappingEnd>();

        if (kind is null)
            throw NewError("invalid_meta_schema", $"Слот '{explicitName}' без поля 'kind'.");

        return new NodeType(
            explicitName, kind.Value, description, node, titleSource, titleField, titleFormat,
            fields, keyType, valueType, of, blocks, constraints);
    }

    private static IReadOnlyList<TypeDefinition> ReadTypeList(YamlReader r)
    {
        r.Expect<SequenceStart>();
        var list = new List<TypeDefinition>();
        while (r.Peek() is MappingStart)
        {
            list.Add(ReadTypeFromSequenceItem(r));
        }
        r.Expect<SequenceEnd>();
        return list;
    }

    private static TypeDefinition ReadTypeFromSequenceItem(YamlReader r)
    {
        r.Expect<MappingStart>();

        string? name = null;
        TypeKind? kind = null;
        string? description = null;
        bool? node = null;
        TitleSourceKind? titleSource = null;
        string? titleField = null;
        string? titleFormat = null;
        IReadOnlyList<FieldDefinition>? fields = null;
        string? keyType = null;
        string? valueType = null;
        string? of = null;
        IReadOnlyList<BlockDefinition>? blocks = null;
        IReadOnlyList<string>? constraints = null;
        RefDirection? direction = null;
        bool? system = null;

        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();
            switch (key)
            {
                case "name": name = r.NextScalarValue(); break;
                case "kind": kind = ParseTypeKind(r.NextScalarValue()); break;
                case "description": description = r.NextScalarValue(); break;
                case "node": node = ReadBool(r, key); break;
                case "title_source": titleSource = ParseTitleSource(r.NextScalarValue()); break;
                case "title_field": titleField = r.NextScalarValue(); break;
                case "title_format": titleFormat = r.NextScalarValue(); break;
                case "fields": fields = ReadFieldList(r); break;
                case "key_type": keyType = r.NextScalarValue(); break;
                case "value_type": valueType = r.NextScalarValue(); break;
                case "of": of = r.NextScalarValue(); break;
                case "blocks": blocks = ReadBlockList(r); break;
                case "constraints": constraints = ReadStringList(r); break;
                case "direction": direction = ParseRefDirection(r.NextScalarValue()); break;
                case "system": system = ReadBool(r, key); break;
                default: r.SkipValue(); break;
            }
        }

        r.Expect<MappingEnd>();

        if (name is null)
            throw NewError("invalid_schema", "Тип в types[] без поля 'name'.");
        if (kind is null)
            throw NewError("invalid_schema", $"Тип '{name}' без поля 'kind'.");

        return kind.Value switch
        {
            TypeKind.Mapping or TypeKind.SingleKeyMapping or TypeKind.List =>
                new NodeType(
                    name, kind.Value, description, node, titleSource, titleField, titleFormat,
                    fields, keyType, valueType, of, blocks, constraints),
            TypeKind.Primitive =>
                new Primitive(name, description, constraints),
            TypeKind.RefType =>
                new RefType(
                    name,
                    direction ?? throw NewError("invalid_schema", $"ref_type '{name}' без 'direction'."),
                    system ?? throw NewError("invalid_schema", $"ref_type '{name}' без 'system'."),
                    description),
            _ => throw NewError("invalid_schema", $"Неизвестный kind у типа '{name}'."),
        };
    }

    private static IReadOnlyList<FieldDefinition> ReadFieldList(YamlReader r)
    {
        r.Expect<SequenceStart>();
        var list = new List<FieldDefinition>();
        while (r.Peek() is MappingStart)
        {
            list.Add(ReadField(r));
        }
        r.Expect<SequenceEnd>();
        return list;
    }

    private static FieldDefinition ReadField(YamlReader r)
    {
        r.Expect<MappingStart>();

        string? name = null;
        string? type = null;
        string? of = null;
        string? defaultValue = null;
        string? description = null;
        IReadOnlyList<string>? values = null;
        bool? required = null;

        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();
            switch (key)
            {
                case "name": name = r.NextScalarValue(); break;
                case "type": type = r.NextScalarValue(); break;
                case "of": of = r.NextScalarValue(); break;
                case "values": values = ReadStringList(r); break;
                case "required": required = ReadBool(r, key); break;
                case "default": defaultValue = r.NextScalarValue(); break;
                case "description": description = r.NextScalarValue(); break;
                default: r.SkipValue(); break;
            }
        }

        r.Expect<MappingEnd>();

        if (name is null) throw NewError("invalid_field", "Поле в fields[] без 'name'.");
        if (type is null) throw NewError("invalid_field", $"Поле '{name}' без 'type'.");
        if (required is null) throw NewError("invalid_field", $"Поле '{name}' без 'required'.");

        return new FieldDefinition(name, type, of, values, required.Value, defaultValue, description);
    }

    private static IReadOnlyList<BlockDefinition> ReadBlockList(YamlReader r)
    {
        r.Expect<SequenceStart>();
        var list = new List<BlockDefinition>();
        while (r.Peek() is MappingStart)
        {
            list.Add(ReadBlock(r));
        }
        r.Expect<SequenceEnd>();
        return list;
    }

    private static BlockDefinition ReadBlock(YamlReader r)
    {
        r.Expect<MappingStart>();

        string? name = null;
        string? of = null;
        string? description = null;
        bool? required = null;

        while (r.Peek() is Scalar)
        {
            var key = r.NextScalarValue();
            switch (key)
            {
                case "name": name = r.NextScalarValue(); break;
                case "of": of = r.NextScalarValue(); break;
                case "required": required = ReadBool(r, key); break;
                case "description": description = r.NextScalarValue(); break;
                default: r.SkipValue(); break;
            }
        }

        r.Expect<MappingEnd>();

        if (name is null) throw NewError("invalid_block", "Блок в blocks[] без 'name'.");
        if (of is null) throw NewError("invalid_block", $"Блок '{name}' без 'of'.");
        if (required is null) throw NewError("invalid_block", $"Блок '{name}' без 'required'.");

        return new BlockDefinition(name, of, required.Value, description);
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

    private static TypeKind ParseTypeKind(string raw) => raw switch
    {
        "mapping" => TypeKind.Mapping,
        "single_key_mapping" => TypeKind.SingleKeyMapping,
        "list" => TypeKind.List,
        "primitive" => TypeKind.Primitive,
        "ref_type" => TypeKind.RefType,
        _ => throw NewError("invalid_schema", $"Неизвестный kind '{raw}'."),
    };

    private static TitleSourceKind ParseTitleSource(string raw) => raw switch
    {
        "filename" => TitleSourceKind.Filename,
        "inline_key" => TitleSourceKind.InlineKey,
        "field" => TitleSourceKind.Field,
        _ => throw NewError("invalid_schema", $"Неизвестный title_source '{raw}'."),
    };

    private static RefDirection ParseRefDirection(string raw) => raw switch
    {
        "child_to_parent" => RefDirection.ChildToParent,
        "from_to" => RefDirection.FromTo,
        _ => throw NewError("invalid_schema", $"Неизвестный direction '{raw}'."),
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

public static class SchemaJson
{
    public static JsonObject ToJson(MetaSchemaDocument doc) => new()
    {
        ["meta_schema_version"] = doc.Version,
        ["name"] = doc.Name,
        ["description"] = doc.Description,
        ["primitive_types"] = StringsToJson(doc.PrimitiveTypes),
        ["type_kinds"] = StringsToJson(doc.TypeKinds),
        ["schema_root"] = NodeTypeToJson(doc.SchemaRoot, includeName: false),
        ["type_definition"] = NodeTypeToJson(doc.TypeDefinitionSlot, includeName: false),
        ["field_definition"] = NodeTypeToJson(doc.FieldDefinitionSlot, includeName: false),
        ["block_definition"] = NodeTypeToJson(doc.BlockDefinitionSlot, includeName: false),
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

    private static JsonNode TypeDefinitionToJson(TypeDefinition t) => t switch
    {
        NodeType n => NodeTypeToJson(n, includeName: true),
        RefType r => RefTypeToJson(r),
        Primitive p => PrimitiveToJson(p),
        _ => throw new InvalidOperationException($"Неизвестный TypeDefinition: {t.GetType()}"),
    };

    private static JsonObject NodeTypeToJson(NodeType n, bool includeName)
    {
        var obj = new JsonObject();
        if (includeName) obj["name"] = n.Name;
        obj["kind"] = TypeKindToString(n.Kind);
        if (n.Description is not null) obj["description"] = n.Description;
        if (n.Node is not null) obj["node"] = n.Node.Value;
        if (n.TitleSource is not null) obj["title_source"] = TitleSourceToString(n.TitleSource.Value);
        if (n.TitleField is not null) obj["title_field"] = n.TitleField;
        if (n.TitleFormat is not null) obj["title_format"] = n.TitleFormat;
        if (n.KeyType is not null) obj["key_type"] = n.KeyType;
        if (n.ValueType is not null) obj["value_type"] = n.ValueType;
        if (n.Of is not null) obj["of"] = n.Of;
        if (n.Fields is not null)
        {
            var arr = new JsonArray();
            foreach (var f in n.Fields) arr.Add((JsonNode?)FieldToJson(f));
            obj["fields"] = arr;
        }
        if (n.Blocks is not null)
        {
            var arr = new JsonArray();
            foreach (var b in n.Blocks) arr.Add((JsonNode?)BlockToJson(b));
            obj["blocks"] = arr;
        }
        if (n.Constraints is not null) obj["constraints"] = StringsToJson(n.Constraints);
        return obj;
    }

    private static JsonObject RefTypeToJson(RefType r)
    {
        var obj = new JsonObject
        {
            ["name"] = r.Name,
            ["kind"] = "ref_type",
            ["direction"] = RefDirectionToString(r.Direction),
            ["system"] = r.System,
        };
        if (r.Description is not null) obj["description"] = r.Description;
        return obj;
    }

    private static JsonObject PrimitiveToJson(Primitive p)
    {
        var obj = new JsonObject
        {
            ["name"] = p.Name,
            ["kind"] = "primitive",
        };
        if (p.Description is not null) obj["description"] = p.Description;
        if (p.Constraints is not null) obj["constraints"] = StringsToJson(p.Constraints);
        return obj;
    }

    private static JsonObject FieldToJson(FieldDefinition f)
    {
        var obj = new JsonObject
        {
            ["name"] = f.Name,
            ["type"] = f.Type,
            ["required"] = f.Required,
        };
        if (f.Of is not null) obj["of"] = f.Of;
        if (f.Values is not null) obj["values"] = StringsToJson(f.Values);
        if (f.Default is not null) obj["default"] = f.Default;
        if (f.Description is not null) obj["description"] = f.Description;
        return obj;
    }

    private static JsonObject BlockToJson(BlockDefinition b)
    {
        var obj = new JsonObject
        {
            ["name"] = b.Name,
            ["of"] = b.Of,
            ["required"] = b.Required,
        };
        if (b.Description is not null) obj["description"] = b.Description;
        return obj;
    }

    private static JsonArray StringsToJson(IReadOnlyList<string> strings)
    {
        var arr = new JsonArray();
        foreach (var s in strings) arr.Add((JsonNode?)s);
        return arr;
    }

    private static string TypeKindToString(TypeKind k) => k switch
    {
        TypeKind.Mapping => "mapping",
        TypeKind.SingleKeyMapping => "single_key_mapping",
        TypeKind.List => "list",
        TypeKind.Primitive => "primitive",
        TypeKind.RefType => "ref_type",
        _ => k.ToString(),
    };

    private static string TitleSourceToString(TitleSourceKind t) => t switch
    {
        TitleSourceKind.Filename => "filename",
        TitleSourceKind.InlineKey => "inline_key",
        TitleSourceKind.Field => "field",
        _ => t.ToString(),
    };

    private static string RefDirectionToString(RefDirection d) => d switch
    {
        RefDirection.ChildToParent => "child_to_parent",
        RefDirection.FromTo => "from_to",
        _ => d.ToString(),
    };
}
