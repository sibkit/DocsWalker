using System.Text.Json.Nodes;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Validation;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Api;

/// <summary>
/// Сериализация результатов <see cref="ReadApi"/> в JSON для CLI / MCP.
/// Узел отдаётся в форме 5 концептуальных полей refs-модели:
/// <c>id, type, title, text, out_refs</c>.
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

    public static JsonArray NodesToJson(IReadOnlyList<Node> nodes)
    {
        var arr = new JsonArray();
        foreach (var n in nodes) arr.Add((JsonNode?)NodeToJson(n));
        return arr;
    }

    /// <summary>
    /// Узел в форме refs-модели: ровно <c>id, type, title, text, out_refs</c>.
    /// <c>out_refs</c> — объект <c>{name: [ids]}</c>, зеркало in-memory словаря;
    /// связь <c>path</c> присутствует у всех узлов кроме root наравне с прочими.
    /// </summary>
    public static JsonObject NodeToJson(Node node)
    {
        var obj = new JsonObject
        {
            ["id"] = node.Id,
            ["type"] = node.TypeName,
            ["title"] = node.Title,
            ["text"] = node.Text,
            ["out_refs"] = OutRefsToJson(node.OutRefs),
        };
        return obj;
    }

    private static JsonObject OutRefsToJson(IReadOnlyDictionary<string, IReadOnlyList<int>> outRefs)
    {
        var obj = new JsonObject();
        foreach (var name in outRefs.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var arr = new JsonArray();
            foreach (var id in outRefs[name]) arr.Add((JsonNode?)JsonValue.Create(id));
            obj[name] = arr;
        }
        return obj;
    }

    public static JsonObject SubtreeToJson(NodeSubtree subtree)
    {
        var obj = NodeToJson(subtree.Node);
        var children = new JsonArray();
        foreach (var c in subtree.Children) children.Add((JsonNode?)SubtreeToJson(c));
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
        ["name"] = v.Name,
        ["target_id"] = v.TargetId,
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
