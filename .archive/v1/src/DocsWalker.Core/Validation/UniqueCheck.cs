using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Уникальность под refs-модель: id узла глобально уникален в docs/;
/// дубли внутри одного списка-связи в out_refs;
/// для каждого *addressable* дерева (tree-связь с <c>unique_sibling_titles=true</c>) —
/// title узла уникален среди siblings под одним parent в этом дереве
/// (см. docs/Правила оформления.yml/«Title уникален среди siblings»;
/// docs/.docswalker/meta-schema.yml/«ref_def.unique_sibling_titles»).
/// </summary>
internal static class UniqueCheck
{
    public static void Run(GraphModel graph, SchemaDocument schema, List<ValidationError> errors)
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

        // Title уникален среди siblings под одним parent в каждом addressable tree.
        // Для каждого RefDef с unique_sibling_titles=true (т. е. по конкретной tree-связи
        // в типе-источнике) собираем (tree, parent_id, title) и ищем коллизии.
        // Имя tree-ref'а используется как имя scope в OutRefs узла.
        foreach (var t in schema.Types)
        {
            foreach (var rd in t.OutRefs)
            {
                if (!rd.IsAddressable) continue;
                var refName = rd.Name;
                var treeName = rd.Tree!;

                var seenTitles = new Dictionary<(int Parent, string Title), int>();
                foreach (var node in graph.ById.Values)
                {
                    if (node.Id == Node.RootId) continue;
                    if (string.IsNullOrEmpty(node.Title)) continue;
                    if (!string.Equals(node.TypeName, t.Name, StringComparison.Ordinal)) continue;
                    if (!node.OutRefs.TryGetValue(refName, out var parents) || parents.Count == 0) continue;

                    var parentId = parents[0];
                    var key = (parentId, node.Title);
                    if (seenTitles.TryGetValue(key, out var prev))
                        errors.Add(new ValidationError(
                            "duplicate_sibling_title",
                            $"В дереве '{treeName}' под parent id={parentId} два узла с одинаковым title='{node.Title}' (id={prev} и id={node.Id}).",
                            node.SourceFile, node.Id,
                            Hint: "Title должен быть уникален среди siblings в addressable дереве; переименуй один из узлов через update-node."));
                    else
                        seenTitles[key] = node.Id;
                }
            }
        }
    }
}
