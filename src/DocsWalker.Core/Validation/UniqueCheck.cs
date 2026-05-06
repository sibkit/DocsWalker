using DocsWalker.Core.Graph;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Уникальность под refs-модель: id узла глобально уникален в docs/;
/// title уникален среди узлов одного типа, разделяющих общего path-родителя
/// (см. docs/Правила оформления.yml/«Title уникален среди siblings»);
/// дубли внутри одного списка-связи в out_refs.
/// </summary>
internal static class UniqueCheck
{
    public static void Run(GraphModel graph, List<ValidationError> errors)
    {
        // duplicate_id формально невозможен (Graph.Add бросает на коллизии), но
        // для согласованности контракта валидатора повторно проверяем.
        var seenIds = new HashSet<int>();
        foreach (var node in graph.ById.Values)
        {
            if (!seenIds.Add(node.Id))
                errors.Add(new ValidationError(
                    "duplicate_id",
                    $"id={node.Id} встречается в графе более одного раза.",
                    node.SourceFile, node.Id));
        }

        // Дубли внутри одного списка-связи: out_refs[name] не должен содержать одну цель дважды.
        foreach (var parent in graph.ById.Values)
        {
            foreach (var (refName, targets) in parent.OutRefs)
            {
                var seenTargets = new HashSet<int>();
                foreach (var t in targets)
                {
                    if (!seenTargets.Add(t))
                        errors.Add(new ValidationError(
                            "duplicate_target_in_ref",
                            $"У узла id={parent.Id} связь '{refName}': цель id={t} упомянута более одного раза.",
                            parent.SourceFile, parent.Id,
                            Hint: "Удали повторяющийся id из связи; обычно это след ручной правки YAML."));
                }
            }
        }

        // Title уникален среди siblings одного типа в одном path-родителе.
        var seenTitles = new Dictionary<(int Parent, string Type, string Title), int>();
        foreach (var node in graph.ById.Values)
        {
            if (node.Id == Node.RootId) continue;
            if (string.IsNullOrEmpty(node.Title)) continue;

            var parentId = node.ParentId;
            if (parentId is null) continue;

            var key = (parentId.Value, node.TypeName, node.Title);
            if (seenTitles.TryGetValue(key, out var prev))
                errors.Add(new ValidationError(
                    "duplicate_sibling_title",
                    $"В path-родителе id={parentId} два узла типа '{node.TypeName}' с одинаковым title='{node.Title}' (id={prev} и id={node.Id}).",
                    node.SourceFile, node.Id,
                    Hint: "Title должен быть уникален среди siblings одного типа; переименуй один из узлов через update-node."));
            else
                seenTitles[key] = node.Id;
        }
    }
}
