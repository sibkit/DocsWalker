using System.Text.Json;

namespace DocsWalker.Core.Mcp;

/// <summary>
/// Маршалинг MCP <c>tools/call.arguments</c>-объекта в CLI-argv: первый элемент —
/// имя tool'а, далее <c>--key=value</c> для каждого ключа. Используется
/// <see cref="DocsWalker.Kernel.RpcDispatcher"/> и MCP-wrapper'ом.
/// <para>
/// Логика разделения «массив-id-list (CSV)» vs «массив-объектов (raw JSON со
/// скобками)» опирается на <see cref="McpToolParam"/>: <c>JsonType="array",
/// ItemsJsonType="object"</c> → передаём raw JSON. Иначе — CSV-склейка скаляров.
/// Без <see cref="McpToolParam"/>-контекста массивы всегда собираются как CSV
/// (backward-compat для unit-тестов).
/// </para>
/// <para>
/// Filter <see cref="FilteredKeys"/>: только <c>storage_path</c> в
/// user-input игнорируется. Storage-path задаётся kernel'ом из
/// kernel-config'а (по имени графа из URL <c>/db/&lt;name&gt;/rpc</c>) и
/// инжектится в argv в <see cref="DocsWalker.Kernel.RpcDispatcher"/> уже
/// после этого билдера; clientside-инжекция перебила бы это, поэтому
/// ключ режется тихо. <c>root</c> убран в stg-0010 step-06 — если LLM
/// передаст <c>arguments.root=...</c>, оно попадёт в argv и Dispatcher
/// ответит <c>unknown_parameter</c> (loud failure).
/// </para>
/// </summary>
public static class McpArgvBuilder
{
    /// <summary>
    /// Ключи, которые игнорируются при сборке argv. <c>storage-path</c>
    /// инжектится kernel'ом из kernel-config'а — в user-input не должен
    /// встречаться, иначе перебил бы инжекцию. <c>root</c> здесь больше
    /// не фильтруется (см. xmldoc типа): если LLM его передаст —
    /// kernel-валидатор отвергнет с <c>unknown_parameter</c>.
    /// </summary>
    private static readonly HashSet<string> FilteredKeys = new(StringComparer.Ordinal)
    {
        "storage-path",
    };

    public static string[] BuildArgvFromArguments(
        string toolName,
        JsonElement? arguments,
        IReadOnlyDictionary<string, McpToolParam>? paramByName = null)
    {
        var argv = new List<string>(8) { toolName };
        if (!arguments.HasValue) return argv.ToArray();
        var args = arguments.Value;
        if (args.ValueKind == JsonValueKind.Null || args.ValueKind == JsonValueKind.Undefined)
            return argv.ToArray();
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("arguments must be an object");

        foreach (var prop in args.EnumerateObject())
        {
            var key = prop.Name.Replace('_', '-');
            if (FilteredKeys.Contains(key)) continue;

            McpToolParam? paramSpec = null;
            paramByName?.TryGetValue(key, out paramSpec);
            var value = JsonValueToCliString(prop.Value, paramSpec);
            argv.Add($"--{key}={value}");
        }
        return argv.ToArray();
    }

    private static string JsonValueToCliString(JsonElement value, McpToolParam? param) => value.ValueKind switch
    {
        JsonValueKind.String  => value.GetString() ?? string.Empty,
        JsonValueKind.Number  => value.GetRawText(),
        JsonValueKind.True    => "true",
        JsonValueKind.False   => "false",
        JsonValueKind.Null    => string.Empty,
        // Массив: array-of-object (например, transaction.operations) — отдаём raw JSON
        // со скобками; CLI-парсер ждёт валидный JSON-массив. Иначе — CSV-id-list:
        // 1,2,3 (формат совпадает с CLI-сепаратором IdList).
        JsonValueKind.Array   => IsObjectArray(param)
            ? value.GetRawText()
            : string.Join(",", value.EnumerateArray().Select(x => JsonValueToCliString(x, null))),
        // Объект — Json-параметр: сырой JSON-текст.
        JsonValueKind.Object  => value.GetRawText(),
        _ => throw new ArgumentException($"unsupported argument value kind: {value.ValueKind}")
    };

    private static bool IsObjectArray(McpToolParam? param) =>
        param is { JsonType: "array", ItemsJsonType: "object" };
}
