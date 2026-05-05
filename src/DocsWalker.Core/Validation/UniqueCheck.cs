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
                    section.SourceFile, section.Id));
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
