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
/// Содержимое <c>kernel.json</c> — discovery-файла per-user ядра DocsWalker. Пишется
/// ядром на старте после биндинга порта Kestrel'ом (<see cref="Port"/> уже фактический,
/// не 0). Удаляется на graceful shutdown. <see cref="AuthToken"/> — null в local-only
/// режиме (bind=127.0.0.1); используется в будущем при remote-bind.
/// </summary>
public sealed record KernelInfo(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("auth_token")] string? AuthToken);

/// <summary>
/// Информация об одном графе в kernel'е. Используется в ответе
/// <c>GET /db</c>: имя, путь к storage-папке и метка последнего запроса.
/// </summary>
public sealed record GraphInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("storage_path")] string StoragePath,
    [property: JsonPropertyName("last_used")] DateTimeOffset LastUsed);

/// <summary>
/// Ответ <c>GET /db</c> — список графов, известных kernel'у из
/// kernel-config'а.
/// </summary>
public sealed record GraphsResponse(
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
[JsonSerializable(typeof(GraphsResponse))]
[JsonSerializable(typeof(KernelInfo))]
public partial class KernelJsonContext : JsonSerializerContext
{
}
