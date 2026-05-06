using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Целостность связей в refs-модели + tree-scopes:
/// 1) целевой узел каждой связи существует;
/// 2) для каждого объявленного дерева <c>tree: X</c> — отсутствие циклов в подграфе scope;
/// 3) для встроенного дерева <c>path</c> — у каждого не-root узла ровно одна
///    объявленная связь с <c>tree=path</c>; родитель существует; цепочка достигает root
///    (связность хранилища).
/// Имя связи и target_types проверяет <see cref="SchemaCheck"/>.
/// </summary>
internal static class RefsCheck
{
    public static void Run(SchemaDocument schema, GraphModel graph, List<ValidationError> errors)
    {
        // Индекс типов для разрешения tree-связей узла.
        var typeByName = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var t in schema.Types) typeByName[t.Name] = t;

        // 1) Существование цели у каждой связи.
        foreach (var node in graph.ById.Values)
        {
            if (node.Id == Node.RootId) continue;
            foreach (var (refName, targets) in node.OutRefs)
            {
                foreach (var targetId in targets)
                {
                    if (targetId == Node.RootId) continue;
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

        // 2) Per-scope cycle detection. Для каждого узла собираем словарь tree-name → ref-name
        //    из объявленного типа; затем для каждого scope отдельно делаем DFS.
        var scopeRefNames = BuildScopeRefNamesByType(schema);
        foreach (var (scopeName, _) in schema.Trees.Select(t => (t.Name, t.Description)))
        {
            CheckScopeCycles(graph, typeByName, scopeRefNames, scopeName, errors);
        }

        // 3) Path-tree специфика: ровно один path-ref у не-root + связность.
        CheckPathTree(graph, typeByName, scopeRefNames, errors);
    }

    /// <summary>
    /// Строит словарь типа → (имя_дерева → имя_связи). Используется при обходе графа
    /// для извлечения исходящего scope-ref'а конкретного узла без линейного поиска по OutRefs.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> BuildScopeRefNamesByType(SchemaDocument schema)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var t in schema.Types)
        {
            var byScope = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rd in t.OutRefs)
            {
                if (rd.Tree is null) continue;
                byScope[rd.Tree] = rd.Name;
            }
            result[t.Name] = byScope;
        }
        return result;
    }

    private static void CheckScopeCycles(
        GraphModel graph,
        Dictionary<string, TypeDefinition> typeByName,
        Dictionary<string, Dictionary<string, string>> scopeRefNames,
        string scope,
        List<ValidationError> errors)
    {
        // Состояние: 0 = не посещён, 1 = в текущем стеке (gray), 2 = завершён (black).
        var state = new Dictionary<int, int>();

        foreach (var startNode in graph.ById.Values)
        {
            if (state.GetValueOrDefault(startNode.Id) != 0) continue;
            // Iterative DFS, чтобы не упереться в стек-overflow на длинных цепочках.
            var stack = new Stack<int>();
            stack.Push(startNode.Id);
            while (stack.Count > 0)
            {
                var id = stack.Peek();
                var s = state.GetValueOrDefault(id);
                if (s == 0)
                {
                    state[id] = 1;
                    var parentId = ResolveScopeParent(graph, typeByName, scopeRefNames, id, scope);
                    if (parentId is int pid && pid != Node.RootId)
                    {
                        var ps = state.GetValueOrDefault(pid);
                        if (ps == 1)
                        {
                            var node = graph.GetById(id);
                            errors.Add(new ValidationError(
                                "tree_cycle",
                                $"Дерево '{scope}': обнаружен цикл (узел id={id} достиг id={pid}, уже находящегося в активной цепочке).",
                                node?.SourceFile, id,
                                Hint: scope == TreeDefinition.PathTreeName
                                    ? "Цепочка path зациклена; обычно — след ручной правки YAML, восстанови корректную иерархию."
                                    : "Цикл в tree-scope обычно возникает после некорректного move-node; перенаправь scope-ref в нециклический узел."));
                            state[id] = 2;
                            stack.Pop();
                            continue;
                        }
                        if (ps == 0)
                        {
                            stack.Push(pid);
                            continue;
                        }
                        // ps == 2: уже завершён, цикла нет.
                    }
                    state[id] = 2;
                    stack.Pop();
                }
                else
                {
                    state[id] = 2;
                    stack.Pop();
                }
            }
        }
    }

    /// <summary>
    /// Возвращает id родителя узла в scope <paramref name="scope"/>, либо null если узел —
    /// корень дерева (тип не объявляет scope-ref) или scope-ref не заполнен.
    /// </summary>
    private static int? ResolveScopeParent(
        GraphModel graph,
        Dictionary<string, TypeDefinition> typeByName,
        Dictionary<string, Dictionary<string, string>> scopeRefNames,
        int nodeId,
        string scope)
    {
        if (nodeId == Node.RootId) return null;
        var node = graph.GetById(nodeId);
        if (node is null) return null;
        if (!scopeRefNames.TryGetValue(node.TypeName, out var byScope)) return null;
        if (!byScope.TryGetValue(scope, out var refName)) return null;
        if (!node.OutRefs.TryGetValue(refName, out var targets) || targets.Count == 0) return null;
        return targets[0];
    }

    /// <summary>
    /// Path-tree специфика, не сводимая к общему cycle-чеку:
    ///   • у каждого не-root узла ровно одна path-связь (cardinality=one не более чем у других);
    ///   • родительский узел path-связи существует;
    ///   • достижимость от root (связность хранилища).
    /// </summary>
    private static void CheckPathTree(
        GraphModel graph,
        Dictionary<string, TypeDefinition> typeByName,
        Dictionary<string, Dictionary<string, string>> scopeRefNames,
        List<ValidationError> errors)
    {
        var visited = new HashSet<int> { Node.RootId };
        var queue = new Queue<int>();
        queue.Enqueue(Node.RootId);
        while (queue.Count > 0)
        {
            var pid = queue.Dequeue();
            foreach (var child in graph.GetChildren(pid))
            {
                if (visited.Add(child.Id)) queue.Enqueue(child.Id);
            }
        }

        foreach (var node in graph.ById.Values)
        {
            if (node.Id == Node.RootId) continue;

            // dangling parent: path-связь указывает на отсутствующий узел.
            if (node.OutRefs.TryGetValue(Node.PathRefName, out var targets) && targets.Count > 0)
            {
                var parentId = targets[0];
                if (parentId != Node.RootId && graph.GetById(parentId) is null)
                {
                    errors.Add(new ValidationError(
                        "dangling_parent",
                        $"Узел id={node.Id}: path указывает на отсутствующий узел id={parentId}.",
                        node.SourceFile, node.Id,
                        Hint: "Родительский узел отсутствует; восстанови path или удали узел через delete-node."));
                }
            }

            // Связность: каждый не-root узел достижим от root по path-edges.
            if (!visited.Contains(node.Id))
            {
                errors.Add(new ValidationError(
                    "path_disconnected",
                    $"Узел id={node.Id}: не достижим от root по дереву 'path'.",
                    node.SourceFile, node.Id,
                    Hint: "У узла отсутствует или сломана связь path. Проверь out_refs[path] и восстанови корректного родителя."));
            }
        }
    }
}
