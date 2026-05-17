using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocsWalker.Mcp;

/// <summary>
/// Конфигурация stdio-bridge: где живёт kernel и какой граф проксируется.
/// Файл ищется как <c>.dw/client.json</c> поиском вверх от cwd
/// (cwd → root). Это позволяет одной командой запустить bridge из любой
/// подпапки репозитория.
///
/// <para>
/// Формат JSON:
/// <code>
/// {
///   "kernel": { "host": "127.0.0.1", "port": 18080 },
///   "graph": "docswalker"
/// }
/// </code>
/// </para>
/// </summary>
internal sealed record ClientConfig(string KernelHost, int KernelPort, string Graph)
{
    public const string ConfigDirName = ".dw";
    public const string ConfigFileName = "client.json";

    public static ClientConfig Resolve()
    {
        var startDir = new DirectoryInfo(Directory.GetCurrentDirectory());
        var found = FindConfig(startDir);
        if (found is null)
        {
            throw new ClientConfigException(
                "client_config_not_found",
                $"Не найден '{ConfigDirName}/{ConfigFileName}' начиная от cwd " +
                $"'{startDir.FullName}' и выше до корня диска.");
        }
        return Read(found);
    }

    public static ClientConfig Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new ClientConfigException(
                "client_config_not_found",
                $"client config не найден: '{path}'");
        }
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new ClientConfigException("client_config_read_failed", ex.Message, ex);
        }
        ClientConfigJson? raw;
        try
        {
            raw = JsonSerializer.Deserialize(json, ClientConfigJsonContext.Default.ClientConfigJson);
        }
        catch (JsonException ex)
        {
            throw new ClientConfigException("client_config_invalid_json", ex.Message, ex);
        }
        if (raw is null || raw.Kernel is null)
        {
            throw new ClientConfigException(
                "client_config_invalid",
                $"client config '{path}': отсутствует обязательный объект 'kernel'");
        }
        if (string.IsNullOrWhiteSpace(raw.Graph))
        {
            throw new ClientConfigException(
                "client_config_invalid",
                $"client config '{path}': отсутствует обязательное поле 'graph'");
        }
        var host = string.IsNullOrWhiteSpace(raw.Kernel.Host) ? "127.0.0.1" : raw.Kernel.Host!;
        var port = raw.Kernel.Port ?? 18080;
        if (port < 1 || port > 65535)
        {
            throw new ClientConfigException(
                "client_config_invalid",
                $"client config '{path}': kernel.port должен быть 1..65535, получен {port}");
        }
        return new ClientConfig(host, port, raw.Graph!);
    }

    private static string? FindConfig(DirectoryInfo? from)
    {
        for (var dir = from; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ConfigDirName, ConfigFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}

internal sealed class ClientConfigException : Exception
{
    public string Code { get; }

    public ClientConfigException(string code, string message) : base(message)
    {
        Code = code;
    }
    public ClientConfigException(string code, string message, Exception inner) : base(message, inner)
    {
        Code = code;
    }
}

internal sealed record ClientConfigJson(
    [property: JsonPropertyName("kernel")] ClientKernelJson? Kernel,
    [property: JsonPropertyName("graph")] string? Graph);

internal sealed record ClientKernelJson(
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("port")] int? Port);

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ClientConfigJson))]
internal partial class ClientConfigJsonContext : JsonSerializerContext
{
}
