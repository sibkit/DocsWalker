using DocsWalker.Core.Schema;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Соответствие <see cref="SchemaDocument"/> мета-схеме (см. docs/.docswalker/meta-schema.yml).
/// Проверяет имена типов, обязательные поля, перекрёстные ссылки между type-определениями
/// и формальные constraints (fields допустимо только при kind=mapping; key_type/value_type
/// обязательны при kind=single_key_mapping; node=true требует title_source; и т. п.).
/// </summary>
internal static class MetaSchemaCheck
{
    public static void Run(MetaSchemaDocument meta, SchemaDocument schema, List<ValidationError> errors)
    {
        var primitives = new HashSet<string>(meta.PrimitiveTypes, StringComparer.Ordinal);

        var declared = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var t in schema.Types)
        {
            if (string.IsNullOrEmpty(t.Name))
            {
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    "В Схеме встречен тип с пустым именем."));
                continue;
            }
            if (declared.ContainsKey(t.Name))
            {
                errors.Add(new ValidationError(
                    "duplicate_type_name",
                    $"Тип '{t.Name}' объявлен в Схеме дважды."));
                continue;
            }
            declared[t.Name] = t;

            if (!IsLatinSnakeCase(t.Name))
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    $"Имя типа '{t.Name}' должно быть на латинице, snake_case."));
        }

        foreach (var t in schema.Types)
        {
            switch (t)
            {
                case NodeType n:
                    ValidateNodeType(n, declared, primitives, errors);
                    break;
                case RefType r:
                    ValidateRefType(r, errors);
                    break;
                case Primitive p:
                    ValidatePrimitive(p, errors);
                    break;
                default:
                    errors.Add(new ValidationError(
                        "invalid_meta_schema",
                        $"Тип '{t.Name}': неизвестная категория {t.GetType().Name}."));
                    break;
            }
        }
    }

    private static void ValidateNodeType(
        NodeType n,
        Dictionary<string, TypeDefinition> declared,
        HashSet<string> primitives,
        List<ValidationError> errors)
    {
        // fields допустимо только при kind=mapping
        if (n.Fields is not null && n.Kind != TypeKind.Mapping)
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{n.Name}': поле 'fields' допустимо только при kind=mapping."));

        // key_type обязательно и допустимо только при kind=single_key_mapping;
        // value_type допустимо только при kind=single_key_mapping; у single_key_mapping
        // значение задаётся либо value_type, либо blocks (но не оба сразу), и одно из двух обязательно.
        if (n.Kind == TypeKind.SingleKeyMapping)
        {
            if (n.KeyType is null)
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    $"Тип '{n.Name}' (single_key_mapping) без key_type."));
            var hasValueType = n.ValueType is not null;
            var hasBlocks = n.Blocks is not null;
            if (hasValueType && hasBlocks)
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    $"Тип '{n.Name}' (single_key_mapping): value_type и blocks одновременно не допускаются."));
            if (!hasValueType && !hasBlocks)
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    $"Тип '{n.Name}' (single_key_mapping): необходимо задать либо value_type, либо blocks."));
        }
        else
        {
            if (n.KeyType is not null)
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    $"Тип '{n.Name}': key_type допустим только при kind=single_key_mapping."));
            if (n.ValueType is not null)
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    $"Тип '{n.Name}': value_type допустим только при kind=single_key_mapping."));
        }

        // node=true допустим только при kind ∈ {mapping, single_key_mapping}
        if (n.Node == true && n.Kind != TypeKind.Mapping && n.Kind != TypeKind.SingleKeyMapping)
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{n.Name}': node=true допустим только при kind ∈ {{mapping, single_key_mapping}}."));

        // title_source обязательно при node=true
        if (n.Node == true && n.TitleSource is null)
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{n.Name}': title_source обязательно при node=true."));

        // title_format задаётся только при title_source=inline_key (и обязателен там)
        if (n.TitleFormat is not null && n.TitleSource != TitleSourceKind.InlineKey)
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{n.Name}': title_format задаётся только при title_source=inline_key."));
        if (n.TitleSource == TitleSourceKind.InlineKey && n.TitleFormat is null)
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{n.Name}': title_source=inline_key требует title_format."));

        // title_field задаётся только при title_source=field (и обязателен там)
        if (n.TitleField is not null && n.TitleSource != TitleSourceKind.Field)
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{n.Name}': title_field задаётся только при title_source=field."));
        if (n.TitleSource == TitleSourceKind.Field && n.TitleField is null)
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{n.Name}': title_source=field требует title_field."));

        // Поля
        if (n.Fields is not null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in n.Fields)
            {
                ValidateFieldDefinition(n, f, declared, primitives, seen, errors);
            }
        }

        // Блоки
        if (n.Blocks is not null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in n.Blocks)
            {
                ValidateBlockDefinition(n, b, declared, primitives, seen, errors);
            }
        }
    }

    private static void ValidateFieldDefinition(
        NodeType n,
        FieldDefinition f,
        Dictionary<string, TypeDefinition> declared,
        HashSet<string> primitives,
        HashSet<string> seen,
        List<ValidationError> errors)
    {
        if (string.IsNullOrEmpty(f.Name))
        {
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{n.Name}': встречено поле без имени."));
            return;
        }
        if (!seen.Add(f.Name))
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{n.Name}': поле '{f.Name}' объявлено дважды."));
        if (!IsLatinSnakeCase(f.Name))
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{n.Name}': имя поля '{f.Name}' должно быть на латинице, snake_case."));

        if (!declared.ContainsKey(f.Type) && !primitives.Contains(f.Type))
            errors.Add(new ValidationError(
                "unknown_type",
                $"Тип '{n.Name}', поле '{f.Name}': тип '{f.Type}' не объявлен в Схеме и не примитив."));

        if (f.Type == "list" && f.Of is null)
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{n.Name}', поле '{f.Name}': при type=list обязательно поле 'of'."));
        if (f.Type == "enum" && f.Values is null)
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{n.Name}', поле '{f.Name}': при type=enum обязательно поле 'values'."));
        if (f.Default is not null && f.Required)
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{n.Name}', поле '{f.Name}': default допустим только при required=false."));
        if (f.Of is not null && !declared.ContainsKey(f.Of) && !primitives.Contains(f.Of))
            errors.Add(new ValidationError(
                "unknown_type",
                $"Тип '{n.Name}', поле '{f.Name}': of='{f.Of}' не объявлен в Схеме и не примитив."));
    }

    private static void ValidateBlockDefinition(
        NodeType n,
        BlockDefinition b,
        Dictionary<string, TypeDefinition> declared,
        HashSet<string> primitives,
        HashSet<string> seen,
        List<ValidationError> errors)
    {
        if (string.IsNullOrEmpty(b.Name))
        {
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{n.Name}': встречен блок без имени."));
            return;
        }
        if (!seen.Add(b.Name))
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{n.Name}': блок '{b.Name}' объявлен дважды."));
        if (!IsLatinSnakeCase(b.Name))
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{n.Name}': имя блока '{b.Name}' должно быть на латинице, snake_case."));
        if (!declared.ContainsKey(b.Of) && !primitives.Contains(b.Of))
            errors.Add(new ValidationError(
                "unknown_type",
                $"Тип '{n.Name}', блок '{b.Name}': of='{b.Of}' не объявлен в Схеме и не примитив."));
    }

    private static void ValidateRefType(RefType r, List<ValidationError> errors)
    {
        // RefType хранит direction как обязательный enum (см. SchemaLoader);
        // отсутствие direction отлавливается ещё на этапе парсинга Схемы.
        // Системный путь — единственный известный системный тип; других быть не должно.
        if (r.System && r.Name != "path")
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Системный ref_type '{r.Name}': единственный допустимый системный тип — 'path'."));
    }

    private static void ValidatePrimitive(Primitive p, List<ValidationError> errors)
    {
        // Имя уже проверено на snake_case в общей секции.
    }

    /// <summary>
    /// Проверка идентификатора на формат: только латиница в нижнем регистре, цифры
    /// и подчёркивание, не начинается с цифры. Используется для имён типов, полей и блоков
    /// в Схеме (см. docs/Правила оформления.yml/«Язык ключей»).
    /// </summary>
    internal static bool IsLatinSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name[0] >= '0' && name[0] <= '9') return false;
        foreach (var c in name)
        {
            if (c >= 'a' && c <= 'z') continue;
            if (c >= '0' && c <= '9') continue;
            if (c == '_') continue;
            return false;
        }
        return true;
    }
}
