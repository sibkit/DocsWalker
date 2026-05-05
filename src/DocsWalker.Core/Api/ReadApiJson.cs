using System.Text.Json.Nodes;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Validation;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Api;

/// <summary>
/// Сериализация результатов <see cref="ReadApi"/> в JSON для CLI / MCP.
/// </summary>
public static class ReadApiJson
{
    public static JsonArray ListDocumentsToJson(IReadOnlyList<DocumentSummary> docs)
    {
        var arr = new JsonArray();
        foreach (var d in docs)
        {
            var obj = new JsonObject
            {
                ["id"] = d.Id,
                ["title"] = d.Title,
            };
            arr.Add((JsonNode?)obj);
        }
        return arr;
    }

    public static JsonArray MapToJson(IReadOnlyList<MapNode> nodes)
    {
        var arr = new JsonArray();
        foreach (var n in nodes) arr.Add((JsonNode?)MapNodeToJson(n));
        return arr;
    }

    private static JsonObject MapNodeToJson(MapNode n)
    {
        var obj = new JsonObject
        {
            ["id"] = n.Id,
            ["type"] = n.TypeName,
            ["title"] = n.Title,
        };
        var children = new JsonArray();
        foreach (var c in n.Children) children.Add((JsonNode?)MapNodeToJson(c));
        obj["children"] = children;
        return obj;
    }

    public static JsonArray NodesToJson(IReadOnlyList<Node> nodes, GraphModel graph)
    {
        var arr = new JsonArray();
        foreach (var n in nodes) arr.Add((JsonNode?)NodeToJson(n, graph));
        return arr;
    }

    /// <summary>
    /// Полный узел: id, type, title, parent_id, поля/блоки/inline-значение,
    /// out_refs (explicit + default; system path вынесен в parent_id).
    /// </summary>
    public static JsonObject NodeToJson(Node node, GraphModel graph)
    {
        var obj = new JsonObject
        {
            ["id"] = node.Id,
            ["type"] = node.TypeName,
            ["title"] = node.Title,
        };
        if (node.ParentId is int pid) obj["parent_id"] = pid;
        else obj["parent_id"] = null;

        if (node.ParentBlockName is not null)
            obj["parent_block"] = node.ParentBlockName;

        if (node.InlineValue is not null)
            obj["value"] = node.InlineValue;

        if (node.Fields is not null && node.Fields.Count > 0)
            obj["fields"] = FieldsToJson(node.Fields);

        if (node.Blocks is not null && node.Blocks.Count > 0)
            obj["blocks"] = BlocksToJson(node.Blocks);

        obj["out_refs"] = OutRefsToJson(node, graph);
        return obj;
    }

    private static JsonArray FieldsToJson(IReadOnlyList<FieldValue> fields)
    {
        var arr = new JsonArray();
        foreach (var f in fields)
        {
            var obj = new JsonObject { ["name"] = f.Name };
            if (f.Items is not null)
            {
                var items = new JsonArray();
                foreach (var s in f.Items) items.Add((JsonNode?)JsonValue.Create(s));
                obj["items"] = items;
            }
            else
            {
                obj["value"] = f.Scalar;
            }
            arr.Add((JsonNode?)obj);
        }
        return arr;
    }

    private static JsonArray BlocksToJson(IReadOnlyList<NodeBlock> blocks)
    {
        var arr = new JsonArray();
        foreach (var b in blocks)
        {
            var obj = new JsonObject { ["name"] = b.Name };
            switch (b)
            {
                case TextBlock tb:
                    var items = new JsonArray();
                    foreach (var s in tb.Items) items.Add((JsonNode?)JsonValue.Create(s));
                    obj["kind"] = "text";
                    obj["items"] = items;
                    break;
                case ChildrenBlock cb:
                    var ids = new JsonArray();
                    foreach (var id in cb.ChildIds) ids.Add((JsonNode?)JsonValue.Create(id));
                    obj["kind"] = "children";
                    obj["child_ids"] = ids;
                    break;
                case OutRefsBlock orb:
                    var refs = new JsonArray();
                    foreach (var r in orb.Refs) refs.Add((JsonNode?)RefToJson(r));
                    obj["kind"] = "out_refs";
                    obj["refs"] = refs;
                    break;
            }
            arr.Add((JsonNode?)obj);
        }
        return arr;
    }

    /// <summary>
    /// Все исходящие связи узла, кроме системной path (она представлена parent_id).
    /// Включает explicit (из out_refs YAML) и default (от родителя по имени блока,
    /// здесь — узел → его дети по имени блока ребёнка).
    /// </summary>
    private static JsonArray OutRefsToJson(Node node, GraphModel graph)
    {
        var arr = new JsonArray();
        foreach (var r in graph.GetOutRefs(node.Id))
        {
            if (r.Origin == RefOrigin.System) continue;
            arr.Add((JsonNode?)RefToJson(r));
        }
        return arr;
    }

    private static JsonObject RefToJson(Ref r) => new()
    {
        ["type"] = r.TypeName,
        ["origin"] = ReadApi.OriginToString(r.Origin),
        ["from_id"] = r.FromId,
        ["to_id"] = r.ToId,
    };

    public static JsonObject SubtreeToJson(NodeSubtree subtree, GraphModel graph)
    {
        var obj = NodeToJson(subtree.Node, graph);
        var children = new JsonArray();
        foreach (var c in subtree.Children) children.Add((JsonNode?)SubtreeToJson(c, graph));
        obj["children"] = children;
        return obj;
    }

    public static JsonObject RefSetToJson(RefSet set)
    {
        var inArr = new JsonArray();
        foreach (var v in set.In) inArr.Add((JsonNode?)RefViewToJson(v));
        var outArr = new JsonArray();
        foreach (var v in set.Out) outArr.Add((JsonNode?)RefViewToJson(v));
        return new JsonObject
        {
            ["in"] = inArr,
            ["out"] = outArr,
        };
    }

    private static JsonObject RefViewToJson(RefView v) => new()
    {
        ["direction"] = v.Direction,
        ["type"] = v.TypeName,
        ["origin"] = ReadApi.OriginToString(v.Origin),
        ["other_id"] = v.OtherId,
        ["other_title"] = v.OtherTitle,
        ["other_path"] = v.OtherPath,
    };

    /// <summary>
    /// Сериализация результата check-integrity. Для LLM ключевая часть — массив errors,
    /// и флаг ok=true (=> errors пуст). Структура каждой ошибки совпадает с error-body
    /// CLI, за исключением того, что лежит в success-result, а не в error-envelope.
    /// </summary>
    public static JsonObject ValidationResultToJson(ValidationResult result)
    {
        var arr = new JsonArray();
        foreach (var e in result.Errors)
        {
            var obj = new JsonObject
            {
                ["code"] = e.Code,
                ["message"] = e.Message,
            };
            if (e.FilePath is not null) obj["path"] = e.FilePath;
            if (e.NodeId is int id) obj["node_id"] = id;
            if (e.Hint is not null) obj["hint"] = e.Hint;
            arr.Add((JsonNode?)obj);
        }
        return new JsonObject
        {
            ["ok"] = result.IsValid,
            ["errors"] = arr,
        };
    }

    public static JsonArray SearchToJson(IReadOnlyList<SearchHit> hits)
    {
        var arr = new JsonArray();
        foreach (var h in hits)
        {
            var fragments = new JsonArray();
            foreach (var f in h.Fragments) fragments.Add((JsonNode?)JsonValue.Create(f));
            arr.Add((JsonNode?)new JsonObject
            {
                ["id"] = h.Id,
                ["type"] = h.TypeName,
                ["title"] = h.Title,
                ["fragments"] = fragments,
            });
        }
        return arr;
    }
}
