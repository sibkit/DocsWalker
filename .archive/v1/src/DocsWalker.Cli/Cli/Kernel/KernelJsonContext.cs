using System.Text.Json.Serialization;

namespace DocsWalker.Cli.Cli.Kernel;

/// <summary>
/// Liveness-ответ <c>GET /health</c>. <see cref="Ok"/> всегда true (если ядро отвечает —
/// значит живо), но клиент должен явно проверить <c>200 OK</c> + <c>ok==true</c>:
/// будущие версии могут возвращать <c>200 OK</c> с <c>ok==false</c> на degraded-mode.
/// </summary>
public sealed record HealthResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt);

/// <summary>
/// Информация об одном графе в kernel'е. Используется в ответах API/control
/// namespace: имя, путь к storage-папке и метка последнего запроса.
/// </summary>
public sealed record GraphInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("storage_path")] string StoragePath,
    [property: JsonPropertyName("last_used")] DateTimeOffset LastUsed);

/// <summary>
/// Ответ <c>GET /api/v0.4</c>: kernel/control plane DocsWalker, отдельный от
/// graph plane <c>POST /&lt;graph&gt;</c>.
/// </summary>
public sealed record KernelApiResponse(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("graph_endpoint")] string GraphEndpoint,
    [property: JsonPropertyName("reserved_graph_names")] IReadOnlyList<string> ReservedGraphNames,
    [property: JsonPropertyName("graphs")] IReadOnlyList<GraphInfo> Graphs);

/// <summary>
/// Source-gen JsonSerializerContext для kernel-специфичных типов. AOT-совместим:
/// никаких рефлексий. <see cref="JsonSourceGenerationOptions"/> совпадают с
/// <c>McpJsonContext</c> для единообразия (no indented, ignore null).
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(GraphInfo))]
[JsonSerializable(typeof(KernelApiResponse))]
public partial class KernelJsonContext : JsonSerializerContext
{
}
