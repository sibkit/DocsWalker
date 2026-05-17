using System.Text.Json;

namespace DocsWalker.Core.Api;

/// <summary>
/// Полное содержимое колонки <c>tx_event.sections_json</c>: секции
/// <c>created</c>, <c>changed</c>, <c>deleted</c> успешной tx
/// (per api/hist-scope.md). Любая секция может быть null, если
/// соответствующих изменений в tx не было.
/// </summary>
public sealed record HistSections(
    CreatedSection? Created,
    ChangedSection? Changed,
    DeletedSection? Deleted)
{
    public static readonly HistSections Empty = new(null, null, null);
}

/// <summary>
/// Секция <c>created</c>: полный post-state каждого нового узла и
/// identity каждого нового link (per api/hist-scope.md, раздел
/// «Секция created»).
/// </summary>
public sealed record CreatedSection(
    IReadOnlyList<CreatedNode>? Nodes,
    IReadOnlyList<HistLink>? Links);

/// <summary>
/// Полный snapshot созданного data-узла. <c>MapBindings</c> хранится как
/// dict со string-значениями: при создании snapshot — null недопустим
/// (per api/tx.md, раздел <c>create.set</c>).
/// </summary>
public sealed record CreatedNode(
    string Id,
    string Path,
    string Title,
    string Content,
    IReadOnlyDictionary<string, string>? MapBindings);

/// <summary>
/// Секция <c>changed</c>: forward-only diff фактически изменённых
/// полей. Links не имеют <c>changed</c> — они либо есть, либо нет.
/// </summary>
public sealed record ChangedSection(IReadOnlyList<ChangedNode> Nodes);

/// <summary>
/// Изменение узла. В <c>Set</c> заполнены только фактически
/// изменённые поля. Для <c>MapBindings</c>: ключ → ветка (set), ключ →
/// null (tombstone «снять»). Пустого <c>Set</c> и пустого
/// <c>MapBindings={}</c> в журнале не бывает (per api/hist-scope.md).
/// </summary>
public sealed record ChangedNode(string Id, ChangedSet Set);

/// <summary>
/// Forward-only diff одного узла в секции <c>changed</c>.
/// </summary>
public sealed record ChangedSet(
    string? Title,
    string? Content,
    string? Path,
    IReadOnlyDictionary<string, string?>? MapBindings);

/// <summary>
/// Секция <c>deleted</c>: identity удалённых узлов / links.
/// </summary>
public sealed record DeletedSection(
    IReadOnlyList<DeletedNode>? Nodes,
    IReadOnlyList<HistLink>? Links);

public sealed record DeletedNode(string Id);

/// <summary>
/// Identity link в секциях <c>created.links</c> / <c>deleted.links</c>.
/// </summary>
public sealed record HistLink(string Name, string From, string To);

/// <summary>
/// JSON сериализация/десериализация секций. Формат — стабильный wire-формат
/// колонки <c>tx_event.sections_json</c>, на котором основаны replay и
/// rollback.
/// </summary>
public static class HistSectionsJson
{
    public static string Serialize(HistSections sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (sections.Created is { } created)
            {
                writer.WritePropertyName("created");
                WriteCreated(writer, created);
            }
            if (sections.Changed is { } changed)
            {
                writer.WritePropertyName("changed");
                WriteChanged(writer, changed);
            }
            if (sections.Deleted is { } deleted)
            {
                writer.WritePropertyName("deleted");
                WriteDeleted(writer, deleted);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static HistSections Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        using var doc = JsonDocument.Parse(json);
        return FromElement(doc.RootElement);
    }

    public static HistSections FromElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("sections_json root must be an object");
        }
        CreatedSection? created = null;
        ChangedSection? changed = null;
        DeletedSection? deleted = null;
        if (root.TryGetProperty("created", out var cE) && cE.ValueKind == JsonValueKind.Object)
        {
            created = ReadCreated(cE);
        }
        if (root.TryGetProperty("changed", out var hE) && hE.ValueKind == JsonValueKind.Object)
        {
            changed = ReadChanged(hE);
        }
        if (root.TryGetProperty("deleted", out var dE) && dE.ValueKind == JsonValueKind.Object)
        {
            deleted = ReadDeleted(dE);
        }
        return new HistSections(created, changed, deleted);
    }

    private static void WriteCreated(Utf8JsonWriter w, CreatedSection s)
    {
        w.WriteStartObject();
        if (s.Nodes is { Count: > 0 } nodes)
        {
            w.WritePropertyName("nodes");
            w.WriteStartArray();
            foreach (var n in nodes)
            {
                w.WriteStartObject();
                w.WriteString("id", n.Id);
                w.WriteString("path", n.Path);
                w.WriteString("title", n.Title);
                w.WriteString("content", n.Content);
                if (n.MapBindings is { Count: > 0 } mb)
                {
                    w.WritePropertyName("map_bindings");
                    w.WriteStartObject();
                    foreach (var kv in mb)
                    {
                        w.WriteString(kv.Key, kv.Value);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (s.Links is { Count: > 0 } links)
        {
            w.WritePropertyName("links");
            WriteLinkArray(w, links);
        }
        w.WriteEndObject();
    }

    private static void WriteChanged(Utf8JsonWriter w, ChangedSection s)
    {
        w.WriteStartObject();
        if (s.Nodes is { Count: > 0 } nodes)
        {
            w.WritePropertyName("nodes");
            w.WriteStartArray();
            foreach (var n in nodes)
            {
                w.WriteStartObject();
                w.WriteString("id", n.Id);
                w.WritePropertyName("set");
                w.WriteStartObject();
                if (n.Set.Title is not null) w.WriteString("title", n.Set.Title);
                if (n.Set.Content is not null) w.WriteString("content", n.Set.Content);
                if (n.Set.Path is not null) w.WriteString("path", n.Set.Path);
                if (n.Set.MapBindings is { Count: > 0 } mb)
                {
                    w.WritePropertyName("map_bindings");
                    w.WriteStartObject();
                    foreach (var kv in mb)
                    {
                        if (kv.Value is null) w.WriteNull(kv.Key);
                        else w.WriteString(kv.Key, kv.Value);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteDeleted(Utf8JsonWriter w, DeletedSection s)
    {
        w.WriteStartObject();
        if (s.Nodes is { Count: > 0 } nodes)
        {
            w.WritePropertyName("nodes");
            w.WriteStartArray();
            foreach (var n in nodes)
            {
                w.WriteStartObject();
                w.WriteString("id", n.Id);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (s.Links is { Count: > 0 } links)
        {
            w.WritePropertyName("links");
            WriteLinkArray(w, links);
        }
        w.WriteEndObject();
    }

    private static void WriteLinkArray(Utf8JsonWriter w, IReadOnlyList<HistLink> links)
    {
        w.WriteStartArray();
        foreach (var l in links)
        {
            w.WriteStartObject();
            w.WriteString("name", l.Name);
            w.WriteString("from", l.From);
            w.WriteString("to", l.To);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static CreatedSection ReadCreated(JsonElement e)
    {
        List<CreatedNode>? nodes = null;
        List<HistLink>? links = null;
        if (e.TryGetProperty("nodes", out var nA) && nA.ValueKind == JsonValueKind.Array)
        {
            nodes = [];
            foreach (var n in nA.EnumerateArray())
            {
                IReadOnlyDictionary<string, string>? mb = null;
                if (n.TryGetProperty("map_bindings", out var mbE) && mbE.ValueKind == JsonValueKind.Object)
                {
                    var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var p in mbE.EnumerateObject())
                    {
                        dict[p.Name] = p.Value.GetString() ?? string.Empty;
                    }
                    mb = dict;
                }
                nodes.Add(new CreatedNode(
                    n.GetProperty("id").GetString()!,
                    n.GetProperty("path").GetString()!,
                    n.GetProperty("title").GetString()!,
                    n.TryGetProperty("content", out var cE) ? (cE.GetString() ?? string.Empty) : string.Empty,
                    mb));
            }
        }
        if (e.TryGetProperty("links", out var lA) && lA.ValueKind == JsonValueKind.Array)
        {
            links = ReadLinkArray(lA);
        }
        return new CreatedSection(nodes, links);
    }

    private static ChangedSection ReadChanged(JsonElement e)
    {
        var nodes = new List<ChangedNode>();
        if (e.TryGetProperty("nodes", out var nA) && nA.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in nA.EnumerateArray())
            {
                var setE = n.GetProperty("set");
                string? title = setE.TryGetProperty("title", out var t) ? t.GetString() : null;
                string? content = setE.TryGetProperty("content", out var c) ? c.GetString() : null;
                string? path = setE.TryGetProperty("path", out var p) ? p.GetString() : null;
                IReadOnlyDictionary<string, string?>? mb = null;
                if (setE.TryGetProperty("map_bindings", out var mbE) && mbE.ValueKind == JsonValueKind.Object)
                {
                    var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
                    foreach (var kv in mbE.EnumerateObject())
                    {
                        dict[kv.Name] = kv.Value.ValueKind == JsonValueKind.Null ? null : kv.Value.GetString();
                    }
                    mb = dict;
                }
                nodes.Add(new ChangedNode(n.GetProperty("id").GetString()!, new ChangedSet(title, content, path, mb)));
            }
        }
        return new ChangedSection(nodes);
    }

    private static DeletedSection ReadDeleted(JsonElement e)
    {
        List<DeletedNode>? nodes = null;
        List<HistLink>? links = null;
        if (e.TryGetProperty("nodes", out var nA) && nA.ValueKind == JsonValueKind.Array)
        {
            nodes = [];
            foreach (var n in nA.EnumerateArray())
            {
                nodes.Add(new DeletedNode(n.GetProperty("id").GetString()!));
            }
        }
        if (e.TryGetProperty("links", out var lA) && lA.ValueKind == JsonValueKind.Array)
        {
            links = ReadLinkArray(lA);
        }
        return new DeletedSection(nodes, links);
    }

    private static List<HistLink> ReadLinkArray(JsonElement arr)
    {
        var list = new List<HistLink>();
        foreach (var l in arr.EnumerateArray())
        {
            list.Add(new HistLink(
                l.GetProperty("name").GetString()!,
                l.GetProperty("from").GetString()!,
                l.GetProperty("to").GetString()!));
        }
        return list;
    }
}
