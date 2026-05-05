using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Целостность связей: каждая явная связь в out_refs ссылается на тип, объявленный
/// в Схеме как ref_type с system=false; целевой узел существует; цикл по path-связям
/// невозможен. Path-цикл формально невозможен (path — производная YAML-вложенности),
/// но проверка стоит на случай ошибок построения графа в памяти.
/// </summary>
internal static class RefsCheck
{
    public static void Run(SchemaDocument schema, GraphModel graph, List<ValidationError> errors)
    {
        var refTypes = new Dictionary<string, RefType>(StringComparer.Ordinal);
        foreach (var t in schema.Types)
            if (t is RefType rt) refTypes[rt.Name] = rt;

        foreach (var node in graph.ById.Values)
        {
            if (node.ExplicitOutRefs is null) continue;
            foreach (var r in node.ExplicitOutRefs)
            {
                if (!refTypes.TryGetValue(r.TypeName, out var rt))
                {
                    errors.Add(new ValidationError(
                        "unknown_ref_type",
                        $"Узел id={node.Id}: тип связи '{r.TypeName}' не объявлен в Схеме как ref_type.",
                        node.SourceFile, node.Id,
                        Hint: "Объяви новый ref_type через add-ref-type перед create-ref, либо сверь существующие имена через get-schema."));
                    continue;
                }
                if (rt.System)
                    errors.Add(new ValidationError(
                        "system_ref_in_explicit",
                        $"Узел id={node.Id}: системный тип связи '{r.TypeName}' не должен явно записываться в out_refs.",
                        node.SourceFile, node.Id,
                        Hint: "Системная связь 'path' формируется автоматически из YAML-вложенности и не должна попадать в out_refs."));
                if (graph.GetById(r.ToId) is null)
                    errors.Add(new ValidationError(
                        "ref_target_not_found",
                        $"Узел id={node.Id}: явная связь '{r.TypeName}' указывает на отсутствующий узел id={r.ToId}.",
                        node.SourceFile, node.Id,
                        Hint: "Целевой узел исчез из графа; либо удали связь через delete-ref, либо подставь корректный to_id."));
            }
        }

        // Path-cycle detection: для каждого узла подняться по parent_id до root,
        // запоминая посещённых; повтор → цикл.
        foreach (var node in graph.ById.Values)
        {
            var visited = new HashSet<int> { node.Id };
            var current = node;
            while (current.ParentId is int pid)
            {
                if (!visited.Add(pid))
                {
                    errors.Add(new ValidationError(
                        "path_cycle",
                        $"Узел id={node.Id}: обнаружен цикл по path-связям (повторно встречен id={pid}).",
                        node.SourceFile, node.Id,
                        Hint: "Цепочка parent_id зациклена; такое состояние возникает только при ручной правке YAML — восстанови корректную иерархию."));
                    break;
                }
                var parent = graph.GetById(pid);
                if (parent is null)
                {
                    errors.Add(new ValidationError(
                        "dangling_parent",
                        $"Узел id={node.Id}: parent_id={pid} указывает на отсутствующий узел.",
                        node.SourceFile, node.Id,
                        Hint: "Родительский узел отсутствует; восстанови parent_id, либо удали узел через delete-node."));
                    break;
                }
                current = parent;
            }
        }
    }
}
