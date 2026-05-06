using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Соответствие <see cref="SchemaDocument"/> мета-схеме v4 и self-consistency Схемы.
/// Под refs-модель проверяет: имена типов snake_case, дубли, корректные ссылки в
/// path_targets и target_types, зарезервированные имена (root, path), уникальность
/// имён связей внутри типа.
/// </summary>
internal static class MetaSchemaCheck
{
    public static void Run(MetaSchemaDocument meta, SchemaDocument schema, List<ValidationError> errors)
    {
        // Версия мета-схемы должна быть v4 (refs-модель). Если SchemaLoader пропустил
        // другую версию — здесь подстраховываемся.
        if (meta.Version != SchemaLoader.SupportedMetaSchemaVersion)
            errors.Add(new ValidationError(
                "unsupported_meta_schema_version",
                $"Поддерживается только meta_schema_version={SchemaLoader.SupportedMetaSchemaVersion}; в мета-схеме — {meta.Version}."));

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
            if (string.Equals(t.Name, Node.RootTypeName, StringComparison.Ordinal))
            {
                errors.Add(new ValidationError(
                    "reserved_type_name",
                    $"Имя '{Node.RootTypeName}' зарезервировано — нельзя объявлять как тип в Схеме."));
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

        foreach (var t in declared.Values)
        {
            ValidatePathTargets(t, declared, errors);
            ValidateOutRefs(t, declared, errors);
        }
    }

    private static void ValidatePathTargets(
        TypeDefinition t,
        Dictionary<string, TypeDefinition> declared,
        List<ValidationError> errors)
    {
        if (t.PathTargets.Count == 0)
        {
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{t.Name}': path_targets не может быть пустым (изолированных узлов не бывает)."));
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in t.PathTargets)
        {
            if (!seen.Add(target))
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    $"Тип '{t.Name}': path_targets содержит '{target}' дважды."));

            if (string.Equals(target, Node.RootTypeName, StringComparison.Ordinal))
                continue;
            if (!declared.ContainsKey(target))
                errors.Add(new ValidationError(
                    "unknown_type",
                    $"Тип '{t.Name}': path_targets ссылается на неизвестный тип '{target}'."));
        }
    }

    private static void ValidateOutRefs(
        TypeDefinition t,
        Dictionary<string, TypeDefinition> declared,
        List<ValidationError> errors)
    {
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rd in t.OutRefs)
        {
            if (string.IsNullOrEmpty(rd.Name))
            {
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    $"Тип '{t.Name}': встречена связь без имени."));
                continue;
            }
            if (string.Equals(rd.Name, Node.PathRefName, StringComparison.Ordinal))
            {
                errors.Add(new ValidationError(
                    "reserved_ref_name",
                    $"Тип '{t.Name}': имя связи '{Node.PathRefName}' зарезервировано — встроенная связь, не объявляется."));
                continue;
            }
            if (!seenNames.Add(rd.Name))
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    $"Тип '{t.Name}': связь '{rd.Name}' объявлена дважды."));
            if (!IsLatinSnakeCase(rd.Name))
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    $"Тип '{t.Name}': имя связи '{rd.Name}' должно быть на латинице, snake_case."));

            if (rd.TargetTypes.Count == 0)
            {
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    $"Тип '{t.Name}', связь '{rd.Name}': target_types не может быть пустым."));
                continue;
            }

            var seenTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in rd.TargetTypes)
            {
                if (!seenTargets.Add(target))
                    errors.Add(new ValidationError(
                        "invalid_meta_schema",
                        $"Тип '{t.Name}', связь '{rd.Name}': target_types содержит '{target}' дважды."));

                if (string.Equals(target, Node.RootTypeName, StringComparison.Ordinal))
                    continue;
                if (!declared.ContainsKey(target))
                    errors.Add(new ValidationError(
                        "unknown_type",
                        $"Тип '{t.Name}', связь '{rd.Name}': target_types ссылается на неизвестный тип '{target}'."));
            }
        }
    }

    /// <summary>
    /// Идентификатор: только латиница в нижнем регистре, цифры и подчёркивание,
    /// не начинается с цифры (см. docs/Правила оформления.yml/«Структурные snake_case»).
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
