using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Согласованность связки «ребёнок ↔ контейнер у родителя» по имени, заданному в
/// <see cref="Node.ParentBlockName"/>.
/// У родителя в Схеме контейнер с этим именем может быть представлен двумя способами:
///   1) BlockDefinition (для типа single_key_mapping, например section.definitions) —
///      у родителя в графе материализуется <see cref="ChildrenBlock"/>; список id в нём
///      должен включать id ребёнка, и наоборот — каждый id из блока должен ссылаться
///      на существующий узел с правильными ParentId/ParentBlockName.
///   2) FieldDefinition типа list / of=node-type (для типа mapping, например document.content) —
///      у родителя в графе нет ни Fields-, ни Blocks-записи: дети живут только через
///      ParentId. В этом случае проверка через ChildrenBlock не применима, остаётся
///      убедиться, что у родителя действительно объявлено поле с этим именем.
/// Если у родителя нет ни блока, ни поля с указанным именем — это нарушение
/// (parent_block_inconsistent), даже если граф формально парсится.
/// </summary>
internal static class ParentBlockCheck
{
    public static void Run(SchemaDocument schema, GraphModel graph, List<ValidationError> errors)
    {
        var byName = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var t in schema.Types) byName[t.Name] = t;

        // Прямая проверка: у каждого ребёнка с ParentBlockName есть «дом» у родителя.
        foreach (var node in graph.ById.Values)
        {
            if (node.ParentId is not int pid) continue;
            if (node.ParentBlockName is not string blockName) continue;

            var parent = graph.GetById(pid);
            if (parent is null) continue; // ловится отдельно в RefsCheck (dangling_parent).

            if (!byName.TryGetValue(parent.TypeName, out var parentType) || parentType is not NodeType pnt)
                continue; // нет инфо о типе родителя — другие проверки уже отметили это.

            if (HasChildrenBlockDef(pnt, blockName))
            {
                var block = FindChildrenBlock(parent, blockName);
                if (block is null)
                {
                    errors.Add(new ValidationError(
                        "parent_block_inconsistent",
                        $"Узел id={node.Id}: parent_block_name='{blockName}', но у родителя id={parent.Id} нет children-блока '{blockName}'.",
                        node.SourceFile, node.Id,
                        Hint: "Перенеси узел через move-node, либо синхронизируй ChildrenBlock родителя руками."));
                    continue;
                }
                if (!block.ChildIds.Contains(node.Id))
                {
                    errors.Add(new ValidationError(
                        "parent_block_inconsistent",
                        $"Узел id={node.Id}: указан в parent_block_name='{blockName}', но id отсутствует в ChildrenBlock родителя id={parent.Id}.",
                        node.SourceFile, node.Id,
                        Hint: "Восстанови id ребёнка в родительском ChildrenBlock — либо запиши узел через write-API, не редактируя YAML вручную."));
                }
            }
            else if (HasNodeChildrenFieldDef(pnt, blockName, byName))
            {
                // OK: дети живут через ParentId, у родителя нет материализованного контейнера —
                // согласованность исчерпывается проверкой parent.Id == node.ParentId, что уже истинно.
            }
            else
            {
                errors.Add(new ValidationError(
                    "parent_block_inconsistent",
                    $"Узел id={node.Id}: parent_block_name='{blockName}' не объявлен в типе родителя '{parent.TypeName}' (id={parent.Id}) ни как block, ни как field-список node-типа.",
                    node.SourceFile, node.Id,
                    Hint: "Сверь имя контейнера со схемой через describe-type --name={parent.TypeName}."));
            }
        }

        // Обратная проверка: каждый id в ChildrenBlock родителя ссылается на существующего ребёнка
        // с правильными ParentId и ParentBlockName.
        foreach (var parent in graph.ById.Values)
        {
            if (parent.Blocks is null) continue;
            foreach (var b in parent.Blocks)
            {
                if (b is not ChildrenBlock cb) continue;
                foreach (var childId in cb.ChildIds)
                {
                    var child = graph.GetById(childId);
                    if (child is null)
                    {
                        errors.Add(new ValidationError(
                            "parent_block_inconsistent",
                            $"У узла id={parent.Id} в children-блоке '{cb.Name}' указан id={childId}, но узла с таким id нет в графе.",
                            parent.SourceFile, parent.Id,
                            Hint: "Удали несуществующий id из ChildrenBlock родителя — либо проверь, что узел не пропал из YAML."));
                        continue;
                    }
                    if (child.ParentId != parent.Id || !string.Equals(child.ParentBlockName, cb.Name, StringComparison.Ordinal))
                    {
                        errors.Add(new ValidationError(
                            "parent_block_inconsistent",
                            $"У узла id={parent.Id} в children-блоке '{cb.Name}' указан id={childId}, но у этого узла parent_id={child.ParentId?.ToString() ?? "null"}, parent_block_name='{child.ParentBlockName ?? "null"}'.",
                            parent.SourceFile, parent.Id,
                            Hint: "Согласуй ParentId/ParentBlockName ребёнка с записью в ChildrenBlock родителя — обычно это устраняется через move-node."));
                    }
                }
            }
        }
    }

    private static bool HasChildrenBlockDef(NodeType t, string name)
    {
        if (t.Blocks is null) return false;
        foreach (var b in t.Blocks)
            if (string.Equals(b.Name, name, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool HasNodeChildrenFieldDef(
        NodeType t, string name, Dictionary<string, TypeDefinition> byName)
    {
        if (t.Fields is null) return false;
        foreach (var f in t.Fields)
        {
            if (!string.Equals(f.Name, name, StringComparison.Ordinal)) continue;
            if (f.Type != "list" || f.Of is null) return false;
            if (!byName.TryGetValue(f.Of, out var of)) return false;
            if (of is not NodeType ont) return false;
            return ont.Kind == TypeKind.Mapping || ont.Kind == TypeKind.SingleKeyMapping;
        }
        return false;
    }

    private static ChildrenBlock? FindChildrenBlock(Node node, string blockName)
    {
        if (node.Blocks is null) return null;
        foreach (var b in node.Blocks)
            if (b is ChildrenBlock cb && string.Equals(cb.Name, blockName, StringComparison.Ordinal))
                return cb;
        return null;
    }
}
