using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Соответствие <see cref="SchemaDocument"/> мета-схеме v6 и self-consistency Схемы.
/// Под refs-модель + tree-scopes + root-as-entry-point проверяет: имена типов snake_case,
/// дубли, корректные ссылки в target_types; уникальность имён связей внутри типа;
/// декларация деревьев (включая обязательное дерево <c>path</c>); для каждого не-root
/// типа — наличие обязательной связи <c>name=path</c> с <c>tree=path</c>; согласованность
/// tree/cardinality/required в RefDef. Тип <c>root</c> — синглтон ядра DocsWalker (узел
/// с id=0); в Схеме декларируется как обычный type_definition с особенностью отсутствия
/// path-ref.
/// </summary>
internal static class MetaSchemaCheck
{
    public static void Run(MetaSchemaDocument meta, SchemaDocument schema, List<ValidationError> errors)
    {
        // Версия мета-схемы должна быть v5 (refs-модель + tree-scopes). Если SchemaLoader
        // пропустил другую версию — здесь подстраховываемся.
        if (meta.Version != SchemaLoader.SupportedMetaSchemaVersion)
            errors.Add(new ValidationError(
                "unsupported_meta_schema_version",
                $"Поддерживается только meta_schema_version={SchemaLoader.SupportedMetaSchemaVersion}; в мета-схеме — {meta.Version}."));

        // Проверка декларации деревьев.
        var trees = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tr in schema.Trees)
        {
            if (string.IsNullOrEmpty(tr.Name))
            {
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    "В Схеме встречено tree_definition с пустым именем."));
                continue;
            }
            if (!trees.Add(tr.Name))
                errors.Add(new ValidationError(
                    "duplicate_tree_name",
                    $"Дерево '{tr.Name}' объявлено в Схеме дважды."));
            if (!IsLatinSnakeCase(tr.Name))
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    $"Имя дерева '{tr.Name}' должно быть на латинице, snake_case."));
        }
        if (!trees.Contains(TreeDefinition.PathTreeName))
            errors.Add(new ValidationError(
                "path_tree_missing",
                $"В Схеме отсутствует обязательное дерево '{TreeDefinition.PathTreeName}'."));

        // default_addressable_tree (если задан): проверяется после прохода по типам,
        // т. к. зависит от знания, какие деревья имеют addressable-tree-связь.
        // Сначала собираем имена addressable-деревьев.

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

        foreach (var t in declared.Values)
        {
            ValidateOutRefs(t, declared, trees, errors);
            ValidatePathRef(t, errors);
        }

        ValidateDefaultAddressableTree(schema, errors);
    }

    private static void ValidateDefaultAddressableTree(
        SchemaDocument schema,
        List<ValidationError> errors)
    {
        var name = schema.DefaultAddressableTree;
        if (name is null) return;

        // Имя должно быть объявлено в trees.
        var declaredTree = false;
        foreach (var tr in schema.Trees)
        {
            if (string.Equals(tr.Name, name, StringComparison.Ordinal))
            {
                declaredTree = true;
                break;
            }
        }
        if (!declaredTree)
        {
            errors.Add(new ValidationError(
                "unknown_tree_scope",
                $"default_addressable_tree='{name}' ссылается на дерево, не объявленное в trees."));
            return;
        }

        // Хотя бы одна tree-связь с этим именем должна быть addressable.
        var addressable = false;
        foreach (var t in schema.Types)
        {
            foreach (var rd in t.OutRefs)
            {
                if (rd.IsAddressable && string.Equals(rd.Tree, name, StringComparison.Ordinal))
                {
                    addressable = true;
                    break;
                }
            }
            if (addressable) break;
        }
        if (!addressable)
            errors.Add(new ValidationError(
                "default_tree_not_addressable",
                $"default_addressable_tree='{name}' указывает на дерево, у которого нет ни одной tree-связи с unique_sibling_titles=true."));
    }

    private static void ValidatePathRef(
        TypeDefinition t,
        List<ValidationError> errors)
    {
        // root — корневой синглтон ядра, по контракту мета-схемы (v6) у него нет path-ref:
        // у него нет родителя в дереве хранилища.
        if (string.Equals(t.Name, Node.RootTypeName, StringComparison.Ordinal))
        {
            if (t.FindPathRef() is not null)
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    $"Тип '{Node.RootTypeName}' не должен объявлять связь 'path' — root не имеет родителя в дереве хранилища."));
            return;
        }

        var pathRef = t.FindPathRef();
        if (pathRef is null)
        {
            errors.Add(new ValidationError(
                "missing_path_ref",
                $"Тип '{t.Name}': отсутствует обязательная связь 'path' (tree=path) — изолированных типов не бывает.",
                Hint: "Добавь в out_refs запись name: path, tree: path, target_types: [...]."));
            return;
        }
        if (!string.Equals(pathRef.Tree, TreeDefinition.PathTreeName, StringComparison.Ordinal))
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{t.Name}': связь 'path' обязана иметь tree=path."));
        if (pathRef.TargetTypes.Count == 0)
            errors.Add(new ValidationError(
                "invalid_meta_schema",
                $"Тип '{t.Name}': у связи 'path' пустой target_types — изолированных узлов не бывает."));
    }

    private static void ValidateOutRefs(
        TypeDefinition t,
        Dictionary<string, TypeDefinition> declared,
        HashSet<string> declaredTrees,
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
            if (!seenNames.Add(rd.Name))
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    $"Тип '{t.Name}': связь '{rd.Name}' объявлена дважды."));
            if (!IsLatinSnakeCase(rd.Name))
                errors.Add(new ValidationError(
                    "invalid_meta_schema",
                    $"Тип '{t.Name}': имя связи '{rd.Name}' должно быть на латинице, snake_case."));

            // Зарезервированное имя 'path' допустимо только при tree=path.
            if (string.Equals(rd.Name, Node.PathRefName, StringComparison.Ordinal)
                && !string.Equals(rd.Tree, TreeDefinition.PathTreeName, StringComparison.Ordinal))
            {
                errors.Add(new ValidationError(
                    "reserved_ref_name",
                    $"Тип '{t.Name}': имя 'path' зарезервировано за встроенной связью с tree=path; не используется как обычная связь."));
            }

            // tree, если задан, должен быть объявлен.
            if (rd.Tree is not null && !declaredTrees.Contains(rd.Tree))
                errors.Add(new ValidationError(
                    "unknown_tree_scope",
                    $"Тип '{t.Name}', связь '{rd.Name}': дерево '{rd.Tree}' не объявлено в schema.trees."));

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
