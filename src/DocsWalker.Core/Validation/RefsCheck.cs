using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Целостность связей в refs-модели: целевой узел каждой связи существует;
/// цикл по path-связям невозможен; ровно одна path-связь на узел (кроме root).
/// Имя связи и target_types проверяет <see cref="SchemaCheck"/>.
/// </summary>
internal static class RefsCheck
{
    public static void Run(SchemaDocument schema, GraphModel graph, List<ValidationError> errors)
    {
        foreach (var node in graph.ById.Values)
        {
            if (node.Id == Node.RootId) continue;

            foreach (var (refName, targets) in node.OutRefs)
            {
                foreach (var targetId in targets)
                {
                    if (targetId == Node.RootId) continue; // root всегда существует
                    if (graph.GetById(targetId) is null)
                    {
                        errors.Add(new ValidationError(
                            "ref_target_not_found",
                            $"Узел id={node.Id}: связь '{refName}' указывает на отсутствующий узел id={targetId}.",
                            node.SourceFile, node.Id,
                            Hint: "Целевой узел отсутствует; удали связь через delete-ref или подставь корректный to_id."));
                    }
                }
            }
        }

        // Path-cycle detection: для каждого узла подняться по path до root.
        foreach (var node in graph.ById.Values)
        {
            if (node.Id == Node.RootId) continue;

            var visited = new HashSet<int> { node.Id };
            var current = node;
            while (current.ParentId is int pid && pid != Node.RootId)
            {
                if (!visited.Add(pid))
                {
                    errors.Add(new ValidationError(
                        "path_cycle",
                        $"Узел id={node.Id}: обнаружен цикл по path-связям (повторно встречен id={pid}).",
                        node.SourceFile, node.Id,
                        Hint: "Цепочка path зациклена; такое состояние возникает только при ручной правке YAML — восстанови корректную иерархию."));
                    break;
                }
                var parent = graph.GetById(pid);
                if (parent is null)
                {
                    errors.Add(new ValidationError(
                        "dangling_parent",
                        $"Узел id={node.Id}: path указывает на отсутствующий узел id={pid}.",
                        node.SourceFile, node.Id,
                        Hint: "Родительский узел отсутствует; восстанови path или удали узел через delete-node."));
                    break;
                }
                current = parent;
            }
        }
    }
}
