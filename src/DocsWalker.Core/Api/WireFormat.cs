using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DocsWalker.Core.Api;

/// <summary>
/// Сериализация ответов <c>read</c>/<c>tx</c> и ошибок в JSON-envelope из
/// api/surface.md. Используется и kernel-транспортом (HTTP/MCP), и CLI
/// (exec/repl) — единый wire-формат на оба канала, чтобы не было «двух
/// диалектов» одного API.
///
/// <para>
/// Envelope успеха <c>read</c>:
/// <list type="bullet">
///   <item>1 op → <c>{"result": &lt;op-data&gt;}</c>.</item>
///   <item>N ops → <c>{"result": [&lt;op1&gt;, &lt;op2&gt;, ...]}</c>.</item>
/// </list>
/// Envelope успеха <c>tx</c>:
/// <c>{"result": {"id": "&lt;tx_id&gt;", "ops": [&lt;per-op&gt;, ...]}}</c>.
/// Envelope ошибки: <c>{"code": "...", "details": {...}}</c>.
/// </para>
/// </summary>
public static class WireFormat
{
    // Без UnsafeRelaxedJsonEscaping все non-ASCII символы (включая
    // кириллицу) сериализуются как `\uXXXX`. Это синтаксически валидный
    // JSON, но раздувает payload и затрудняет чтение в дев-консолях.
    // Кодировка остаётся UTF-8 для самих байтов; «relaxed» означает
    // лишь, что не-ASCII не escape-ится. Никакого XSS-вектора нет —
    // ответ потребляется LLM-клиентом, не браузером.
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string SerializeRead(ReadResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WritePropertyName("result");
            if (response.Ops.Count == 1)
            {
                WriteReadOp(w, response.Ops[0]);
            }
            else
            {
                w.WriteStartArray();
                foreach (var op in response.Ops)
                {
                    WriteReadOp(w, op);
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string SerializeTx(TxResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WritePropertyName("result");
            w.WriteStartObject();
            w.WriteString("id", response.Id);
            w.WritePropertyName("ops");
            w.WriteStartArray();
            foreach (var op in response.Ops)
            {
                WriteTxOp(w, op);
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string SerializeError(ApiException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return SerializeError(ex.ToError());
    }

    public static string SerializeError(ApiError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("code", error.Code);
            w.WritePropertyName("details");
            w.WriteStartObject();
            if (error.Details.Path is { Length: > 0 } path)
            {
                w.WriteString("path", path);
            }
            if (error.Details.Extras is { Count: > 0 } extras)
            {
                foreach (var kv in extras)
                {
                    w.WritePropertyName(kv.Key);
                    WriteJsonValue(w, kv.Value);
                }
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ---- read ops ---------------------------------------------------------

    private static void WriteReadOp(Utf8JsonWriter w, ReadOpResponse op)
    {
        switch (op)
        {
            case SelectNodesResponse n:
                WriteNodesResponse(w, n);
                break;
            case SelectEventsResponse e:
                WriteEventsResponse(w, e);
                break;
            case SelectMetaResponse m:
                WriteMetaResponse(w, m);
                break;
            default:
                throw new InvalidOperationException($"Unknown ReadOpResponse {op.GetType().Name}");
        }
    }

    private static void WriteNodesResponse(Utf8JsonWriter w, SelectNodesResponse r)
    {
        w.WriteStartObject();
        w.WriteNumber("count", r.Count);
        if (r.Truncated)
        {
            w.WriteBoolean("truncated", true);
            w.WriteNumber("omitted_count", r.OmittedCount);
            if (r.StoppedAt is { Length: > 0 } stop)
            {
                w.WriteString("stopped_at", stop);
            }
        }
        w.WritePropertyName("items");
        w.WriteStartArray();
        foreach (var n in r.Items)
        {
            WriteNodeView(w, n);
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteNodeView(Utf8JsonWriter w, NodeView n)
    {
        w.WriteStartObject();
        w.WriteString("id", n.Id);
        if (n.Scope is { Length: > 0 } scope)
        {
            w.WriteString("scope", scope);
        }
        w.WriteString("path", n.Path);
        w.WriteString("title", n.Title);
        if (n.MapBindings.Count > 0)
        {
            w.WritePropertyName("map_bindings");
            w.WriteStartObject();
            foreach (var kv in n.MapBindings)
            {
                w.WriteString(kv.Key, kv.Value);
            }
            w.WriteEndObject();
        }
        if (n.Content is not null)
        {
            w.WriteString("content", n.Content);
        }
        if (n.Links is { } links)
        {
            w.WritePropertyName("links");
            w.WriteStartArray();
            foreach (var l in links)
            {
                w.WriteStartObject();
                w.WriteString("name", l.Name);
                if (l.To is { } to)
                {
                    w.WritePropertyName("to");
                    WriteEndpointView(w, to);
                }
                if (l.From is { } from)
                {
                    w.WritePropertyName("from");
                    WriteEndpointView(w, from);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteNumber("tokens", n.Tokens);
        if (n.Version is { } v)
        {
            w.WriteNumber("version", v);
        }
        w.WriteEndObject();
    }

    private static void WriteEndpointView(Utf8JsonWriter w, LinkEndpointView ep)
    {
        w.WriteStartObject();
        w.WriteString("id", ep.Id);
        if (ep.Path is { Length: > 0 } p)
        {
            w.WriteString("path", p);
        }
        w.WriteEndObject();
    }

    private static void WriteEventsResponse(Utf8JsonWriter w, SelectEventsResponse r)
    {
        w.WriteStartObject();
        w.WriteNumber("count", r.Count);
        if (r.Truncated)
        {
            w.WriteBoolean("truncated", true);
            w.WriteNumber("omitted_count", r.OmittedCount);
            if (r.StoppedAt is { Length: > 0 } stop)
            {
                w.WriteString("stopped_at", stop);
            }
        }
        w.WritePropertyName("items");
        w.WriteStartArray();
        foreach (var e in r.Items)
        {
            WriteEventView(w, e);
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteEventView(Utf8JsonWriter w, EventView e)
    {
        w.WriteStartObject();
        w.WriteString("id", e.Id);
        w.WriteString("title", e.Title);
        w.WriteString("date", e.Date);
        if (e.RollbackOf is { Length: > 0 } ro)
        {
            w.WriteString("rollback_of", ro);
        }
        if (e.Description is { } desc)
        {
            w.WriteString("description", desc);
        }
        if (e.Counts is { } counts)
        {
            w.WritePropertyName("counts");
            WriteEventCounts(w, counts);
        }
        if (e.Created is { } created)
        {
            w.WritePropertyName("created");
            WriteCreatedSection(w, created);
        }
        if (e.Changed is { } changed)
        {
            w.WritePropertyName("changed");
            WriteChangedSection(w, changed);
        }
        if (e.Deleted is { } deleted)
        {
            w.WritePropertyName("deleted");
            WriteDeletedSection(w, deleted);
        }
        w.WriteNumber("tokens", e.Tokens);
        w.WriteEndObject();
    }

    private static void WriteEventCounts(Utf8JsonWriter w, EventCountsView c)
    {
        w.WriteStartObject();
        if (c.Created is { } cs) WriteSectionCount(w, "created", cs);
        if (c.Changed is { } chs) WriteSectionCount(w, "changed", chs);
        if (c.Deleted is { } ds) WriteSectionCount(w, "deleted", ds);
        w.WriteEndObject();
    }

    private static void WriteSectionCount(Utf8JsonWriter w, string name, SectionCountView c)
    {
        w.WritePropertyName(name);
        w.WriteStartObject();
        if (c.Nodes is { } n) w.WriteNumber("nodes", n);
        if (c.Links is { } l) w.WriteNumber("links", l);
        w.WriteEndObject();
    }

    private static void WriteCreatedSection(Utf8JsonWriter w, CreatedSection s)
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
            WriteHistLinks(w, links);
        }
        w.WriteEndObject();
    }

    private static void WriteChangedSection(Utf8JsonWriter w, ChangedSection s)
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

    private static void WriteDeletedSection(Utf8JsonWriter w, DeletedSection s)
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
            WriteHistLinks(w, links);
        }
        w.WriteEndObject();
    }

    private static void WriteHistLinks(Utf8JsonWriter w, IReadOnlyList<HistLink> links)
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

    private static void WriteMetaResponse(Utf8JsonWriter w, SelectMetaResponse m)
    {
        w.WriteStartObject();
        w.WritePropertyName("meta");
        w.WriteStartObject();
        foreach (var kv in m.Meta)
        {
            w.WritePropertyName(kv.Key);
            WriteJsonValue(w, kv.Value);
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }

    // ---- tx ops -----------------------------------------------------------

    private static void WriteTxOp(Utf8JsonWriter w, TxOpResponse op)
    {
        switch (op)
        {
            case CreateOpResponse c:
                w.WriteStartObject();
                w.WriteString("id", c.Id);
                w.WriteEndObject();
                break;
            case EmptyTxOpResponse:
                w.WriteStartObject();
                w.WriteEndObject();
                break;
            default:
                throw new InvalidOperationException($"Unknown TxOpResponse {op.GetType().Name}");
        }
    }

    // ---- generic value writer (для ApiErrorDetails.Extras / meta stub) ---

    private static void WriteJsonValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteNullValue();
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case int i:
                w.WriteNumberValue(i);
                break;
            case long l:
                w.WriteNumberValue(l);
                break;
            case double d:
                w.WriteNumberValue(d);
                break;
            case IReadOnlyDictionary<string, object?> dict:
                w.WriteStartObject();
                foreach (var kv in dict)
                {
                    w.WritePropertyName(kv.Key);
                    WriteJsonValue(w, kv.Value);
                }
                w.WriteEndObject();
                break;
            case IEnumerable<object?> arr:
                w.WriteStartArray();
                foreach (var item in arr)
                {
                    WriteJsonValue(w, item);
                }
                w.WriteEndArray();
                break;
            default:
                w.WriteStringValue(value.ToString() ?? string.Empty);
                break;
        }
    }
}
