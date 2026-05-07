using System.Text.Json.Nodes;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Tokens;
using DocsWalker.Core.Validation;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Api;

/// <summary>
/// Сериализация результатов <see cref="ReadApi"/> в JSON для CLI / MCP.
/// Узел отдаётся в форме 5 концептуальных полей refs-модели:
/// <c>id, type, title, text, out_refs</c>; плюс метрики <c>tokens</c> /
/// <c>subtree_tokens</c> в map- и subtree-ответах.
/// </summary>
public static class ReadApiJson
{
    /// <summary>
    /// Имена полей, признаваемых параметром <c>--fields</c>. Поле <c>id</c> присутствует
    /// в ответе всегда — без него ответ бесполезен и не сопоставим с запросом.
    /// </summary>
    public static readonly IReadOnlyCollection<string> AllNodeFields =
        new[] { "id", "type", "title", "text", "out_refs", "tokens", "subtree_tokens" };

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
            ["tokens"] = n.Tokens,
        };
        // subtree_tokens опускается при равенстве tokens — у листа в текущем поддереве
        // эти числа всегда совпадают. По контракту LLM-guide отсутствие поля ⇒ узел
        // листовой и subtree_tokens = tokens.
        if (n.SubtreeTokens != n.Tokens) obj["subtree_tokens"] = n.SubtreeTokens;
        // children опускается у листового узла: отсутствие массива ⇒ нет path-детей.
        if (n.Children.Count > 0)
        {
            var children = new JsonArray();
            foreach (var c in n.Children) children.Add((JsonNode?)MapNodeToJson(c));
            obj["children"] = children;
        }
        return obj;
    }

    public static JsonArray NodesToJson(IReadOnlyList<Node> nodes)
    {
        var arr = new JsonArray();
        foreach (var n in nodes) arr.Add((JsonNode?)NodeToJson(n));
        return arr;
    }

    /// <summary>
    /// Узел в форме refs-модели: <c>id, type, title, text, out_refs</c> плюс метрика
    /// <c>tokens</c> (BPE-счёт самого узла). <c>out_refs</c> — объект <c>{name: [ids]}</c>,
    /// зеркало in-memory словаря; связь <c>path</c> присутствует у всех узлов кроме root
    /// наравне с прочими.
    /// </summary>
    public static JsonObject NodeToJson(Node node) => NodeToJson(node, fields: null);

    /// <summary>
    /// Узел с whitelist'ом полей. <paramref name="fields"/>=null → все поля. Поле <c>id</c>
    /// в ответе присутствует всегда — без него потребитель не сможет сопоставить узел
    /// с запросом; явно опустить нельзя.
    /// </summary>
    public static JsonObject NodeToJson(Node node, IReadOnlyCollection<string>? fields)
    {
        var obj = new JsonObject { ["id"] = node.Id };
        if (Include(fields, "type"))      obj["type"] = node.TypeName;
        if (Include(fields, "title"))     obj["title"] = node.Title;
        if (Include(fields, "text"))      obj["text"] = node.Text;
        if (Include(fields, "out_refs"))  obj["out_refs"] = OutRefsToJson(node.OutRefs);
        if (Include(fields, "tokens"))    obj["tokens"] = TokenCounter.CountNode(node);
        return obj;
    }

    private static bool Include(IReadOnlyCollection<string>? fields, string name) =>
        fields is null || fields.Contains(name);

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

    public static JsonObject SubtreeToJson(NodeSubtree subtree) =>
        SubtreeToJson(subtree, fields: null);

    /// <summary>
    /// Сериализация get-by-path и аналогов с whitelist'ом полей. <paramref name="fields"/>=null
    /// → все поля включая <c>tokens</c>/<c>subtree_tokens</c>; иначе — только перечисленные
    /// (<c>id</c> всегда). <c>subtree_tokens</c> учитывает ровно те узлы, что фактически
    /// попали в результат (с учётом <c>depth</c> на стороне Core).
    /// Не делегирует <see cref="NodeToJson(Node,IReadOnlyCollection{string}?)"/>, чтобы
    /// переиспользовать уже посчитанный <see cref="NodeSubtree.Tokens"/> и не считать
    /// токены второй раз для каждого узла.
    /// </summary>
    public static JsonObject SubtreeToJson(NodeSubtree subtree, IReadOnlyCollection<string>? fields)
    {
        var n = subtree.Node;
        var obj = new JsonObject { ["id"] = n.Id };
        if (Include(fields, "type"))           obj["type"] = n.TypeName;
        if (Include(fields, "title"))          obj["title"] = n.Title;
        if (Include(fields, "text"))           obj["text"] = n.Text;
        if (Include(fields, "out_refs"))       obj["out_refs"] = OutRefsToJson(n.OutRefs);
        if (Include(fields, "tokens"))         obj["tokens"] = subtree.Tokens;
        // subtree_tokens опускается при равенстве tokens — листовой случай в текущем
        // поддереве (нет children либо все обрезаны depth/whitelist'ом). LLM
        // подставляет subtree_tokens = tokens по отсутствию поля.
        if (Include(fields, "subtree_tokens") && subtree.SubtreeTokens != subtree.Tokens)
            obj["subtree_tokens"] = subtree.SubtreeTokens;
        // children опускается у листа.
        if (subtree.Children.Count > 0)
        {
            var children = new JsonArray();
            foreach (var c in subtree.Children) children.Add((JsonNode?)SubtreeToJson(c, fields));
            obj["children"] = children;
        }
        return obj;
    }

    /// <summary>
    /// Сериализация get-subtree с указанием scope: оборачивает поддерево в объект
    /// <c>{tree: <name>, root: {...subtree...}}</c>, чтобы LLM подтверждала, по какому
    /// дереву прошёл обход.
    /// </summary>
    public static JsonObject SubtreeToJson(NodeSubtree subtree, string tree) =>
        SubtreeToJson(subtree, tree, fields: null);

    public static JsonObject SubtreeToJson(NodeSubtree subtree, string tree, IReadOnlyCollection<string>? fields) => new()
    {
        ["tree"] = tree,
        ["root"] = SubtreeToJson(subtree, fields),
    };

    /// <summary>
    /// Сериализация get-ancestors: <c>{tree: <name>, ancestors: [...node...]}</c>,
    /// от ближайшего родителя к корню дерева.
    /// </summary>
    public static JsonObject AncestorsToJson(IReadOnlyList<Node> ancestors, string tree)
    {
        var arr = new JsonArray();
        foreach (var n in ancestors) arr.Add((JsonNode?)NodeToJson(n));
        return new JsonObject
        {
            ["tree"] = tree,
            ["ancestors"] = arr,
        };
    }

    public static JsonObject RefSetToJson(RefSet set) => new()
    {
        ["in"] = RefMapToJson(set.In),
        ["out"] = RefMapToJson(set.Out),
    };

    public static JsonObject RefMapToJson(IReadOnlyDictionary<string, IReadOnlyList<int>> map)
    {
        var obj = new JsonObject();
        foreach (var name in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var ids = new JsonArray();
            foreach (var id in map[name].OrderBy(x => x))
                ids.Add((JsonNode?)JsonValue.Create(id));
            obj[name] = ids;
        }
        return obj;
    }

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

    /// <summary>
    /// Сериализация <see cref="UsageGuideResponse"/> для get-usage-guide.
    /// Порядок ключей: mental_model → trees → commands → graph_snapshot, чтобы
    /// LLM сначала прочитала «как думать», потом «по каким полям ходить»,
    /// потом «что вызывать», потом «что в графе сейчас есть».
    /// </summary>
    public static JsonObject UsageGuideToJson(UsageGuideResponse u)
    {
        var trees = new JsonArray();
        foreach (var t in u.Trees)
        {
            var tobj = new JsonObject { ["name"] = t.Name };
            if (t.Description is not null) tobj["description"] = t.Description;
            trees.Add((JsonNode?)tobj);
        }

        var commands = new JsonArray();
        foreach (var c in u.Commands) commands.Add((JsonNode?)CommandToJson(c));

        var rootChildren = new JsonArray();
        foreach (var rc in u.Snapshot.RootChildren)
        {
            rootChildren.Add((JsonNode?)new JsonObject
            {
                ["id"] = rc.Id,
                ["type"] = rc.Type,
                ["title"] = rc.Title,
            });
        }

        return new JsonObject
        {
            ["mental_model"] = u.MentalModel,
            ["trees"] = trees,
            ["commands"] = commands,
            ["graph_snapshot"] = new JsonObject
            {
                ["total_nodes"] = u.Snapshot.TotalNodes,
                ["root_children"] = rootChildren,
                ["schema_types_count"] = u.Snapshot.SchemaTypesCount,
            },
        };
    }

    private static JsonObject CommandToJson(UsageGuideCommand c)
    {
        var obj = new JsonObject
        {
            ["name"] = c.Name,
            ["kind"] = c.Kind,
        };
        if (c.Description is not null) obj["description"] = c.Description;
        // parameters/examples опускаются, если коллекция пуста: у команд без параметров
        // (get-meta-schema, get-schema, get-map, check-integrity, get-usage-guide) поле
        // не выводится; то же для команд без примеров.
        if (c.Parameters.Count > 0)
        {
            var parameters = new JsonArray();
            foreach (var p in c.Parameters)
            {
                var pobj = new JsonObject
                {
                    ["name"] = p.Name,
                    ["type"] = p.Type,
                    ["required"] = p.Required,
                };
                if (p.Description is not null) pobj["description"] = p.Description;
                parameters.Add((JsonNode?)pobj);
            }
            obj["parameters"] = parameters;
        }
        if (c.Examples.Count > 0)
        {
            var examples = new JsonArray();
            foreach (var ex in c.Examples) examples.Add((JsonNode?)ex);
            obj["examples"] = examples;
        }
        return obj;
    }

    /// <summary>
    /// Сериализация <see cref="TypeDescription"/> для describe-type. Поля <c>tree</c> /
    /// <c>cardinality</c> / <c>required</c> в каждом ref выводятся условно: tree-refs
    /// получают только <c>tree</c> (cardinality/required подразумеваются), остальные —
    /// только <c>cardinality</c>+<c>required</c> (без tree).
    /// </summary>
    public static JsonObject TypeDescriptionToJson(TypeDescription d)
    {
        var obj = new JsonObject { ["name"] = d.Name };
        if (d.Description is not null) obj["description"] = d.Description;
        obj["text_required"] = d.TextRequired;
        var arr = new JsonArray();
        foreach (var rd in d.OutRefs) arr.Add((JsonNode?)RefDescriptionToJson(rd));
        obj["out_refs"] = arr;
        return obj;
    }

    private static JsonObject RefDescriptionToJson(TypeRefDescription r)
    {
        var obj = new JsonObject { ["name"] = r.Name };
        if (r.Tree is not null) obj["tree"] = r.Tree;
        // Опускаем дефолты non-tree refs (cardinality=many, required=false): отсутствие
        // поля = дефолт. tree-refs приходят с Cardinality/Required=null (по контракту
        // TypeRefDescription) и сюда не попадают.
        if (r.Cardinality is { } c && c != Cardinality.Many) obj["cardinality"] = CardinalityToString(c);
        if (r.Required is true) obj["required"] = true;
        var targets = new JsonArray();
        foreach (var t in r.TargetTypes) targets.Add((JsonNode?)t);
        obj["target_types"] = targets;
        if (r.Description is not null) obj["description"] = r.Description;
        return obj;
    }

    private static string CardinalityToString(Cardinality c) => c switch
    {
        Cardinality.One => "one",
        Cardinality.Many => "many",
        _ => c.ToString(),
    };

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
