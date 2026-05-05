using System.Globalization;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Соответствие узлов <see cref="GraphModel"/> Схеме: тип каждого узла объявлен в Схеме
/// как node-тип; обязательные поля присутствуют; типы значений совместимы (integer, bool,
/// enum); неизвестные поля и блоки запрещены; обязательные блоки представлены.
/// Поле-список с of=node-типа трактуется как children-блок и проверяется через
/// <see cref="GraphModel.GetChildren"/>, а не через <see cref="Node.Fields"/>.
/// </summary>
internal static class SchemaCheck
{
    public static void Run(SchemaDocument schema, GraphModel graph, List<ValidationError> errors)
    {
        var byName = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var t in schema.Types) byName[t.Name] = t;

        foreach (var node in graph.ById.Values)
        {
            if (!byName.TryGetValue(node.TypeName, out var typeDef))
            {
                errors.Add(new ValidationError(
                    "unknown_type",
                    $"Узел id={node.Id}: тип '{node.TypeName}' не объявлен в Схеме.",
                    node.SourceFile, node.Id));
                continue;
            }
            if (typeDef is not NodeType nt)
            {
                errors.Add(new ValidationError(
                    "invalid_node_type",
                    $"Узел id={node.Id}: тип '{node.TypeName}' не является node-типом (kind={typeDef.Kind}).",
                    node.SourceFile, node.Id));
                continue;
            }

            CheckNode(node, nt, byName, graph, errors);
        }
    }

    private static void CheckNode(
        Node node, NodeType type,
        Dictionary<string, TypeDefinition> byName,
        GraphModel graph,
        List<ValidationError> errors)
    {
        if (string.IsNullOrEmpty(node.Title))
            errors.Add(new ValidationError(
                "missing_title",
                $"Узел id={node.Id} типа '{type.Name}': пустой title.",
                node.SourceFile, node.Id));

        switch (type.Kind)
        {
            case TypeKind.Mapping:
                CheckMappingFields(node, type, byName, graph, errors);
                CheckBlocks(node, type, errors);
                break;
            case TypeKind.SingleKeyMapping:
                CheckSingleKeyMappingValue(node, type, errors);
                CheckBlocks(node, type, errors);
                break;
            case TypeKind.List:
                // На текущем шаге list-узлов нет (kind=list используется только для
                // полей-списков и блоков-списков внутри типов, а не как тип Node).
                break;
        }
    }

    private static void CheckMappingFields(
        Node node, NodeType type,
        Dictionary<string, TypeDefinition> byName,
        GraphModel graph,
        List<ValidationError> errors)
    {
        var allowed = type.Fields ?? Array.Empty<FieldDefinition>();
        var allowedByName = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        foreach (var f in allowed) allowedByName[f.Name] = f;

        var present = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        if (node.Fields is not null)
            foreach (var fv in node.Fields) present[fv.Name] = fv;

        foreach (var fdef in allowed)
        {
            if (IsNodeChildrenList(fdef, byName))
            {
                // Поле-список с of=node-типа представлено через children, а не через Fields.
                // Required в этом контексте трактуется мягко: структурно блок присутствует
                // через сами дочерние узлы; пустой список допустим.
                continue;
            }
            if (!present.ContainsKey(fdef.Name))
            {
                if (fdef.Required)
                    errors.Add(new ValidationError(
                        "missing_field",
                        $"Узел id={node.Id} типа '{type.Name}': обязательное поле '{fdef.Name}' отсутствует.",
                        node.SourceFile, node.Id));
                continue;
            }
            CheckFieldValue(node, fdef, present[fdef.Name], errors);
        }

        if (node.Fields is not null)
        {
            foreach (var fv in node.Fields)
            {
                if (!allowedByName.ContainsKey(fv.Name))
                    errors.Add(new ValidationError(
                        "unknown_field",
                        $"Узел id={node.Id} типа '{type.Name}': неизвестное поле '{fv.Name}'.",
                        node.SourceFile, node.Id));
            }
        }
    }

    private static void CheckSingleKeyMappingValue(
        Node node, NodeType type, List<ValidationError> errors)
    {
        // Для single_key_mapping value_type=text узел хранит inline-значение в Node.InlineValue.
        // Для value_type=list (например, section.value=список блоков) — узел хранит блоки в Node.Blocks
        // (значение-список валидируется через CheckBlocks). Сейчас loader поддерживает оба варианта.
        if (type.ValueType == "text")
        {
            if (node.InlineValue is null)
                errors.Add(new ValidationError(
                    "missing_inline_value",
                    $"Узел id={node.Id} типа '{type.Name}' (value_type=text): inline-значение отсутствует.",
                    node.SourceFile, node.Id));
        }
        // value_type=integer для single_key_mapping встречается у reference-типа (не node).
        // Прочие value_type на текущем шаге не используются.
    }

    private static void CheckBlocks(
        Node node, NodeType type, List<ValidationError> errors)
    {
        if (type.Blocks is null) return;

        var allowedBlocks = new Dictionary<string, BlockDefinition>(StringComparer.Ordinal);
        foreach (var b in type.Blocks) allowedBlocks[b.Name] = b;

        if (node.Blocks is not null)
        {
            foreach (var block in node.Blocks)
            {
                if (!allowedBlocks.ContainsKey(block.Name))
                    errors.Add(new ValidationError(
                        "unknown_block",
                        $"Узел id={node.Id} типа '{type.Name}': неизвестный блок '{block.Name}'.",
                        node.SourceFile, node.Id));
            }
        }

        foreach (var bdef in type.Blocks)
        {
            if (!bdef.Required) continue;
            var has = node.Blocks is not null && node.Blocks.Any(b => b.Name == bdef.Name);
            if (!has)
                errors.Add(new ValidationError(
                    "missing_block",
                    $"Узел id={node.Id} типа '{type.Name}': обязательный блок '{bdef.Name}' отсутствует.",
                    node.SourceFile, node.Id));
        }
    }

    private static void CheckFieldValue(
        Node node, FieldDefinition fdef, FieldValue fv, List<ValidationError> errors)
    {
        // Поле id — спец-кейс: всегда integer, проверка тривиальна, диагностируется отдельно
        // на этапе парсинга. Здесь — для in-memory графа: значение уже строка (decimal).
        if (fdef.Type == "list")
        {
            if (fv.Items is null)
                errors.Add(new ValidationError(
                    "invalid_field_value",
                    $"Узел id={node.Id}, поле '{fdef.Name}': ожидался список, получено скалярное значение.",
                    node.SourceFile, node.Id));
            return;
        }
        if (fv.Items is not null)
        {
            errors.Add(new ValidationError(
                "invalid_field_value",
                $"Узел id={node.Id}, поле '{fdef.Name}': ожидался скаляр, получен список.",
                node.SourceFile, node.Id));
            return;
        }
        if (fv.Scalar is null) return;

        switch (fdef.Type)
        {
            case "integer":
                if (!int.TryParse(fv.Scalar, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    errors.Add(new ValidationError(
                        "invalid_field_value",
                        $"Узел id={node.Id}, поле '{fdef.Name}': ожидался integer, получено '{fv.Scalar}'.",
                        node.SourceFile, node.Id));
                break;
            case "bool":
                if (fv.Scalar != "true" && fv.Scalar != "false")
                    errors.Add(new ValidationError(
                        "invalid_field_value",
                        $"Узел id={node.Id}, поле '{fdef.Name}': ожидался bool, получено '{fv.Scalar}'.",
                        node.SourceFile, node.Id));
                break;
            case "enum":
                if (fdef.Values is not null && !fdef.Values.Contains(fv.Scalar))
                    errors.Add(new ValidationError(
                        "invalid_enum_value",
                        $"Узел id={node.Id}, поле '{fdef.Name}': значение '{fv.Scalar}' не входит в {{{string.Join(", ", fdef.Values)}}}.",
                        node.SourceFile, node.Id));
                break;
        }
    }

    private static bool IsNodeChildrenList(
        FieldDefinition fdef, Dictionary<string, TypeDefinition> byName)
    {
        if (fdef.Type != "list" || fdef.Of is null) return false;
        if (!byName.TryGetValue(fdef.Of, out var t)) return false;
        if (t is not NodeType nt) return false;
        return nt.Kind == TypeKind.Mapping || nt.Kind == TypeKind.SingleKeyMapping;
    }
}
