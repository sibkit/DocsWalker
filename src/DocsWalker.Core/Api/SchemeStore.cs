using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DocsWalker.Core.Api;

/// <summary>
/// Описание одной map в scheme scope (per api/scheme-scope.md). Branches
/// — рекурсивный treе: ключ — имя ветки, значение — поддерево.
/// Required (true) означает, что каждый узел <see cref="OwnerScope"/>
/// должен иметь привязку к этой map.
/// </summary>
internal sealed class SchemeMapDef
{
    public string Name { get; }
    public string OwnerScope { get; }
    public BranchTree Branches { get; }
    public bool Required { get; }

    public SchemeMapDef(string name, string ownerScope, BranchTree branches, bool required)
    {
        Name = name;
        OwnerScope = ownerScope;
        Branches = branches;
        Required = required;
    }

    /// <summary>
    /// Проверка, что путь ветки <paramref name="branchPath"/> (например
    /// "documents/spec") представим в <see cref="Branches"/>. Пустая
    /// строка не валидна (нет «корневой» ветки).
    /// </summary>
    public bool IsBranchKnown(string branchPath)
    {
        if (string.IsNullOrEmpty(branchPath)) return false;
        var segments = branchPath.Split('/');
        var node = Branches;
        foreach (var seg in segments)
        {
            if (!node.Children.TryGetValue(seg, out var child)) return false;
            node = child;
        }
        return true;
    }
}

/// <summary>
/// Узел дерева веток <see cref="SchemeMapDef.Branches"/>. Пустые
/// поддеревья (листья) — пустой <see cref="Children"/>.
/// </summary>
internal sealed class BranchTree
{
    public Dictionary<string, BranchTree> Children { get; } =
        new(StringComparer.Ordinal);
}

/// <summary>
/// Описание одного link в scheme (per api/scheme-scope.md). Constraints
/// определяют, какие узлы могут быть from/to через
/// <see cref="EndpointConstraint.MapBindings"/>. Required_for определяет,
/// какие endpoint обязаны быть указаны для каждого подходящего узла.
/// </summary>
internal sealed class SchemeLinkDef
{
    public string Name { get; }
    public string OwnerScope { get; }
    public EndpointConstraint From { get; }
    public EndpointConstraint To { get; }
    public IReadOnlyList<string> RequiredFor { get; }

    public SchemeLinkDef(string name, string ownerScope,
        EndpointConstraint from, EndpointConstraint to,
        IReadOnlyList<string> requiredFor)
    {
        Name = name;
        OwnerScope = ownerScope;
        From = from;
        To = to;
        RequiredFor = requiredFor;
    }
}

/// <summary>
/// Ограничение endpoint в link (per api/scheme-scope.md, поле
/// <c>from</c>/<c>to</c> в content link-узла).
/// <see cref="MapBindings"/> — словарь <c>map_name → branch_pattern</c>;
/// узел подходит, если у него есть привязка к каждой указанной map с
/// branch_path, удовлетворяющим pattern (`documents/**` — prefix-match).
/// </summary>
internal sealed class EndpointConstraint
{
    public IReadOnlyDictionary<string, string> MapBindings { get; }

    public EndpointConstraint(IReadOnlyDictionary<string, string> mapBindings)
    {
        MapBindings = mapBindings;
    }

    /// <summary>
    /// Узел подходит, если каждый констрейнт удовлетворён. Если
    /// <see cref="MapBindings"/> пуст — подходит любой узел.
    /// </summary>
    public bool Matches(IReadOnlyDictionary<string, string> nodeBindings)
    {
        foreach (var kv in MapBindings)
        {
            if (!nodeBindings.TryGetValue(kv.Key, out var actual)) return false;
            if (!BranchPatternMatches(actual, kv.Value)) return false;
        }
        return true;
    }

    private static bool BranchPatternMatches(string actual, string pattern)
    {
        if (pattern.EndsWith("/**", StringComparison.Ordinal))
        {
            var prefix = pattern[..^3];
            return string.Equals(actual, prefix, StringComparison.Ordinal)
                || actual.StartsWith(prefix + "/", StringComparison.Ordinal);
        }
        if (pattern.EndsWith("/*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^2];
            if (!actual.StartsWith(prefix + "/", StringComparison.Ordinal)) return false;
            return actual.AsSpan(prefix.Length + 1).IndexOf('/') < 0;
        }
        return string.Equals(actual, pattern, StringComparison.Ordinal);
    }
}

/// <summary>
/// Закэшированное состояние схемы для одного scope (owner_scope ∈
/// {main, usage}). Содержит словари maps и links по имени. Считывается
/// <see cref="SchemeLoader"/> непосредственно перед валидацией.
/// </summary>
internal sealed class SchemeStore
{
    public Dictionary<string, SchemeMapDef> MainMaps { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, SchemeMapDef> UsageMaps { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, SchemeLinkDef> MainLinks { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, SchemeLinkDef> UsageLinks { get; } = new(StringComparer.Ordinal);

    public bool IsEmpty => MainMaps.Count == 0 && UsageMaps.Count == 0
        && MainLinks.Count == 0 && UsageLinks.Count == 0;

    public IReadOnlyDictionary<string, SchemeMapDef> MapsFor(string ownerScope) =>
        ownerScope switch
        {
            ScopeNames.Main => MainMaps,
            ScopeNames.Usage => UsageMaps,
            _ => new Dictionary<string, SchemeMapDef>(StringComparer.Ordinal),
        };

    public IReadOnlyDictionary<string, SchemeLinkDef> LinksFor(string ownerScope) =>
        ownerScope switch
        {
            ScopeNames.Main => MainLinks,
            ScopeNames.Usage => UsageLinks,
            _ => new Dictionary<string, SchemeLinkDef>(StringComparer.Ordinal),
        };
}

/// <summary>
/// Читает все scheme-узлы для графа и парсит их content в
/// <see cref="SchemeStore"/>. Узлы с неполным или невалидным content
/// (нет category / owner_scope / map|link_name; content не JSON)
/// пропускаются молча — breaking-check над сломанной схемой бесполезен.
/// </summary>
internal static class SchemeLoader
{
    public static SchemeStore Load(SqliteConnection conn, SqliteTransaction tx, string graphName)
    {
        var store = new SchemeStore();
        var rows = new List<(string Id, string Path, string Content, Dictionary<string, string> Mb)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id, path, content FROM node WHERE graph_name = @g AND scope = 'scheme'";
            cmd.Parameters.AddWithValue("@g", graphName);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                rows.Add((rdr.GetString(0), rdr.GetString(1), rdr.GetString(2),
                    new Dictionary<string, string>(StringComparer.Ordinal)));
            }
        }
        if (rows.Count == 0) return store;

        // Подтянуть map_bindings одним запросом.
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "SELECT b.node_id, b.map_name, b.branch_path FROM node_map_binding b " +
                "JOIN node n ON n.graph_name = b.graph_name AND n.id = b.node_id " +
                "WHERE n.graph_name = @g AND n.scope = 'scheme'";
            cmd.Parameters.AddWithValue("@g", graphName);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var id = rdr.GetString(0);
                var name = rdr.GetString(1);
                var value = rdr.GetString(2);
                foreach (var r in rows)
                {
                    if (string.Equals(r.Id, id, StringComparison.Ordinal))
                    {
                        r.Mb[name] = value;
                    }
                }
            }
        }

        foreach (var r in rows)
        {
            if (!r.Mb.TryGetValue("category", out var category)) continue;
            if (!r.Mb.TryGetValue("owner_scope", out var ownerScope)) continue;
            if (ownerScope is not (ScopeNames.Main or ScopeNames.Usage)) continue;

            JsonDocument? doc;
            try { doc = JsonDocument.Parse(r.Content); }
            catch (JsonException) { continue; }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                switch (category)
                {
                    case "map":
                        if (!r.Mb.TryGetValue("map", out var mapName)) break;
                        var mapDef = ParseMap(mapName, ownerScope, doc.RootElement);
                        if (mapDef is null) break;
                        if (ownerScope == ScopeNames.Main) store.MainMaps[mapName] = mapDef;
                        else store.UsageMaps[mapName] = mapDef;
                        break;
                    case "link":
                        if (!r.Mb.TryGetValue("link_name", out var linkName)) break;
                        var linkDef = ParseLink(linkName, ownerScope, doc.RootElement);
                        if (linkDef is null) break;
                        if (ownerScope == ScopeNames.Main) store.MainLinks[linkName] = linkDef;
                        else store.UsageLinks[linkName] = linkDef;
                        break;
                }
            }
        }
        return store;
    }

    private static SchemeMapDef? ParseMap(string name, string ownerScope, JsonElement root)
    {
        if (!root.TryGetProperty("branches", out var branchesE)) return null;
        if (branchesE.ValueKind != JsonValueKind.Object) return null;
        var tree = new BranchTree();
        BuildBranchTree(tree, branchesE);
        var required = root.TryGetProperty("required", out var rE)
            && rE.ValueKind == JsonValueKind.True;
        return new SchemeMapDef(name, ownerScope, tree, required);
    }

    private static void BuildBranchTree(BranchTree node, JsonElement obj)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            var child = new BranchTree();
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                BuildBranchTree(child, prop.Value);
            }
            node.Children[prop.Name] = child;
        }
    }

    private static SchemeLinkDef? ParseLink(string name, string ownerScope, JsonElement root)
    {
        var from = ParseEndpointConstraint(root, "from");
        var to = ParseEndpointConstraint(root, "to");
        var requiredFor = ParseRequiredFor(root);
        return new SchemeLinkDef(name, ownerScope, from, to, requiredFor);
    }

    private static EndpointConstraint ParseEndpointConstraint(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var ep) || ep.ValueKind != JsonValueKind.Object)
        {
            return new EndpointConstraint(
                new Dictionary<string, string>(StringComparer.Ordinal));
        }
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ep.TryGetProperty("map_bindings", out var mb) && mb.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in mb.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    dict[prop.Name] = prop.Value.GetString()!;
                }
            }
        }
        return new EndpointConstraint(dict);
    }

    private static IReadOnlyList<string> ParseRequiredFor(JsonElement root)
    {
        if (!root.TryGetProperty("required_for", out var rf)) return Array.Empty<string>();
        if (rf.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in rf.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
        }
        return list;
    }
}
