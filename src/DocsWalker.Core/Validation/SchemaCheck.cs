using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Соответствие узлов <see cref="GraphModel"/> Схеме под refs-модель.
/// Проверяет: тип узла объявлен; path соответствует path_targets;
/// каждое имя в OutRefs объявлено в типе как RefDef (или = path);
/// тип цели каждой связи входит в target_types; cardinality соблюдается;
/// required-связи заполнены; text_required соблюдён.
/// </summary>
internal static class SchemaCheck
{
    public static void Run(SchemaDocument schema, GraphModel graph, List<ValidationError> errors)
    {
        var byName = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var t in schema.Types) byName[t.Name] = t;

        foreach (var node in graph.ById.Values)
        {
            if (node.Id == Node.RootId)
                continue; // root — синглтон ядра, не описывается типом в Схеме.

            if (!byName.TryGetValue(node.TypeName, out var typeDef))
            {
                errors.Add(new ValidationError(
                    "unknown_type",
                    $"Узел id={node.Id}: тип '{node.TypeName}' не объявлен в Схеме.",
                    node.SourceFile, node.Id,
                    Hint: "Сверь имя типа со списком из get-schema."));
                continue;
            }

            CheckNode(node, typeDef, byName, graph, errors);
        }
    }

    private static void CheckNode(
        Node node,
        TypeDefinition type,
        Dictionary<string, TypeDefinition> byName,
        GraphModel graph,
        List<ValidationError> errors)
    {
        // title непустой — обязательно для refs-модели (см. «Title как path-сегмент»).
        if (string.IsNullOrEmpty(node.Title))
            errors.Add(new ValidationError(
                "missing_title",
                $"Узел id={node.Id} типа '{type.Name}': пустой title.",
                node.SourceFile, node.Id,
                Hint: "Title — 1–2-словный path-сегмент. Передавай его при create-node или меняй через update-node."));

        // text_required: пустой text у типа с text_required=true запрещён;
        // непустой text у типа с text_required=false формально допустим, но это
        // диагностический сигнал — для структурных типов text должен быть пустым.
        if (type.TextRequired && string.IsNullOrEmpty(node.Text))
            errors.Add(new ValidationError(
                "missing_text",
                $"Узел id={node.Id} типа '{type.Name}': text обязателен (text_required=true).",
                node.SourceFile, node.Id));
        if (!type.TextRequired && !string.IsNullOrEmpty(node.Text)
            && !string.Equals(type.Name, "document", StringComparison.Ordinal))
        {
            // document — особый случай: text=описание документа допустим даже если бы стояло false.
            // Прочие структурные типы (folder, section) не должны нести text.
            errors.Add(new ValidationError(
                "unexpected_text",
                $"Узел id={node.Id} типа '{type.Name}': text задан, но у типа text_required=false.",
                node.SourceFile, node.Id));
        }

        // path: должен быть, цель должна быть в path_targets.
        if (!node.OutRefs.TryGetValue(Node.PathRefName, out var pathTargets) || pathTargets.Count == 0)
        {
            errors.Add(new ValidationError(
                "missing_path",
                $"Узел id={node.Id} типа '{type.Name}': встроенная связь '{Node.PathRefName}' отсутствует.",
                node.SourceFile, node.Id));
        }
        else if (pathTargets.Count > 1)
        {
            errors.Add(new ValidationError(
                "invalid_path_cardinality",
                $"Узел id={node.Id} типа '{type.Name}': связь '{Node.PathRefName}' должна иметь ровно одну цель (cardinality=one), но имеет {pathTargets.Count}.",
                node.SourceFile, node.Id));
        }
        else
        {
            var parentId = pathTargets[0];
            var parentTypeName = parentId == Node.RootId
                ? Node.RootTypeName
                : graph.GetById(parentId)?.TypeName;
            if (parentTypeName is not null && !type.PathTargets.Contains(parentTypeName))
            {
                errors.Add(new ValidationError(
                    "invalid_path_target",
                    $"Узел id={node.Id} типа '{type.Name}': путь ведёт к узлу типа '{parentTypeName}', не входящему в path_targets ({string.Join(", ", type.PathTargets)}).",
                    node.SourceFile, node.Id));
            }
        }

        // OutRefs (кроме path): каждое имя объявлено в типе; cardinality, required, target_types.
        var refDefByName = new Dictionary<string, RefDef>(StringComparer.Ordinal);
        foreach (var rd in type.OutRefs) refDefByName[rd.Name] = rd;

        foreach (var (refName, targets) in node.OutRefs)
        {
            if (string.Equals(refName, Node.PathRefName, StringComparison.Ordinal))
                continue;

            if (!refDefByName.TryGetValue(refName, out var refDef))
            {
                errors.Add(new ValidationError(
                    "unknown_ref",
                    $"Узел id={node.Id} типа '{type.Name}': связь '{refName}' не объявлена в типе.",
                    node.SourceFile, node.Id,
                    Hint: $"Допустимые связи типа '{type.Name}' смотри в get-schema. Для добавления нового имени связи отредактируй docs/Схема.yml."));
                continue;
            }

            if (refDef.Cardinality == Cardinality.One && targets.Count > 1)
                errors.Add(new ValidationError(
                    "invalid_cardinality",
                    $"Узел id={node.Id} типа '{type.Name}', связь '{refName}': cardinality=one, но целей {targets.Count}.",
                    node.SourceFile, node.Id));

            foreach (var targetId in targets)
            {
                var targetNode = graph.GetById(targetId);
                if (targetNode is null) continue; // RefsCheck сообщит об отсутствии цели
                if (!refDef.TargetTypes.Contains(targetNode.TypeName))
                    errors.Add(new ValidationError(
                        "invalid_target_type",
                        $"Узел id={node.Id} типа '{type.Name}', связь '{refName}': цель id={targetId} имеет тип '{targetNode.TypeName}', не входящий в target_types ({string.Join(", ", refDef.TargetTypes)}).",
                        node.SourceFile, node.Id));
            }
        }

        // Required-связи: должны быть заполнены.
        foreach (var rd in type.OutRefs)
        {
            if (!rd.Required) continue;
            if (!node.OutRefs.TryGetValue(rd.Name, out var targets) || targets.Count == 0)
            {
                errors.Add(new ValidationError(
                    "missing_required_ref",
                    $"Узел id={node.Id} типа '{type.Name}': обязательная связь '{rd.Name}' не заполнена.",
                    node.SourceFile, node.Id,
                    Hint: $"Передай значение связи '{rd.Name}' при create-node либо добавь через create-ref."));
            }
        }
    }
}
