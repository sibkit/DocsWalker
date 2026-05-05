using DocsWalker.Core.Graph;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Уникальность: id узла глобально уникален в docs/; title section уникален в пределах
/// document (см. docs/Правила оформления.yml/«Идентификация узлов»). Проверка по id
/// тривиальна на in-memory словаре <see cref="GraphModel.ById"/>, но включена для
/// согласованности контракта валидации (валидатор может работать на любом состоянии графа).
/// </summary>
internal static class UniqueCheck
{
    public static void Run(GraphModel graph, List<ValidationError> errors)
    {
        var seenIds = new HashSet<int>();
        foreach (var node in graph.ById.Values)
        {
            if (!seenIds.Add(node.Id))
                errors.Add(new ValidationError(
                    "duplicate_id",
                    $"id={node.Id} встречается в графе более одного раза.",
                    node.SourceFile, node.Id));
        }

        // Уникальность id внутри одного ChildrenBlock — отдельная проверка от глобальной
        // duplicate_id: id может быть уникален в графе, но дважды упомянут в одном блоке
        // родителя (например, после ручной правки YAML). Без этой проверки граф «тихо
        // разъезжается» — узел отдан как ребёнок дважды.
        foreach (var parent in graph.ById.Values)
        {
            if (parent.Blocks is null) continue;
            foreach (var b in parent.Blocks)
            {
                if (b is not ChildrenBlock cb) continue;
                var seen = new HashSet<int>();
                foreach (var childId in cb.ChildIds)
                {
                    if (!seen.Add(childId))
                        errors.Add(new ValidationError(
                            "duplicate_child_in_block",
                            $"У узла id={parent.Id} в children-блоке '{cb.Name}' id={childId} упомянут более одного раза.",
                            parent.SourceFile, parent.Id,
                            Hint: "Удали повторяющийся id из ChildrenBlock родителя; обычно это след ручной правки YAML."));
                }
            }
        }

        var rootByNode = new Dictionary<int, int>();
        foreach (var node in graph.ById.Values)
            rootByNode[node.Id] = FindRootDoc(graph, node);

        var seenTitles = new Dictionary<(int Doc, string Title), int>();
        foreach (var section in graph.GetByType("section"))
        {
            if (!rootByNode.TryGetValue(section.Id, out var doc)) continue;
            var key = (doc, section.Title);
            if (seenTitles.TryGetValue(key, out var prev))
                errors.Add(new ValidationError(
                    "duplicate_section_title",
                    $"В документе id={doc} секция с title='{section.Title}' встречается дважды (id={prev} и id={section.Id}).",
                    section.SourceFile, section.Id,
                    Hint: "Title секции должен быть уникален в пределах документа; переименуй одну из секций через update-node patch.title."));
            else
                seenTitles[key] = section.Id;
        }
    }

    private static int FindRootDoc(GraphModel graph, Node node)
    {
        var current = node;
        var safety = graph.NodeCount + 1;
        while (current.ParentId is int pid && safety-- > 0)
        {
            var parent = graph.GetById(pid);
            if (parent is null) return current.Id;
            current = parent;
        }
        return current.Id;
    }
}
