using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocsWalker.Cli.Cli.Kernel;

/// <summary>
/// Параметры клиента DocsWalker, прочитанные из <c>.dw/client.json</c>.
/// Файл ищется поиском вверх по родителям от cwd (как <c>.git/</c>);
/// первый найденный — он и есть клиентский config'.
/// <para>
/// Содержит:
/// </para>
/// <list type="bullet">
///   <item><see cref="KernelHost"/>, <see cref="KernelPort"/> — куда отправлять
///   JSON-RPC запросы.</item>
///   <item><see cref="Graph"/> — имя графа в URL <c>/db/&lt;name&gt;/rpc</c>.
///   Имена graph'ов определены kernel'ом в kernel-config'е; клиент должен
///   передать существующее имя.</item>
/// </list>
/// </summary>
public sealed record ClientConfig(
    string KernelHost,
    int KernelPort,
    string Graph)
{
    /// <summary>
    /// Стандартное имя папки клиентского config'а. Аналог <c>.git/</c> —
    /// проектная маркер-папка, не предназначенная к коммиту чужих секретов.
    /// </summary>
    public const string ConfigDirName = ".dw";

    /// <summary>
    /// Имя файла клиентского config'а внутри <c>.dw/</c>.
    /// </summary>
    public const string ConfigFileName = "client.json";

    /// <summary>
    /// Поиск client-config'а вверх по родителям от <paramref name="startDir"/>;
    /// при null — от <see cref="Directory.GetCurrentDirectory"/>. Возвращает
    /// абсолютный путь к найденному файлу или null.
    /// </summary>
    public static string? FindConfigPath(string? startDir = null)
    {
        var current = new DirectoryInfo(startDir ?? Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ConfigDirName, ConfigFileName);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// Читает client-config из заданного пути и валидирует. При отсутствии
    /// файла — <c>client_config_not_found</c>; при невалидном содержимом —
    /// <c>client_config_invalid</c>.
    /// </summary>
    public static ClientConfig Read(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            throw new ClientConfigException(
                "client_config_not_found",
                "Путь к client-config не задан.");
        if (!File.Exists(configPath))
            throw new ClientConfigException(
                "client_config_not_found",
                $"client-config '{configPath}' не найден.");

        string json;
        try
        {
            json = File.ReadAllText(configPath);
        }
        catch (Exception ex)
        {
            throw new ClientConfigException(
                "client_config_invalid",
                $"не удалось прочитать client-config '{configPath}': {ex.Message}", ex);
        }

        ClientConfigJson? raw;
        try
        {
            raw = JsonSerializer.Deserialize(json, ClientConfigJsonContext.Default.ClientConfigJson);
        }
        catch (JsonException ex)
        {
            throw new ClientConfigException(
                "client_config_invalid",
                $"client-config '{configPath}' — невалидный JSON: {ex.Message}", ex);
        }
        if (raw is null)
            throw new ClientConfigException(
                "client_config_invalid",
                $"client-config '{configPath}' пустой");

        return Validate(raw, configPath);
    }

    /// <summary>
    /// Сквозной resolver: ищет config поиском вверх от cwd, читает и валидирует.
    /// При отсутствии файла — <c>client_config_not_found</c>.
    /// </summary>
    public static ClientConfig Resolve(string? startDir = null)
    {
        var path = FindConfigPath(startDir);
        if (path is null)
            throw new ClientConfigException(
                "client_config_not_found",
                $"client-config '{ConfigDirName}/{ConfigFileName}' не найден ни в текущей " +
                $"директории, ни выше по дереву.");
        return Read(path);
    }

    private static ClientConfig Validate(ClientConfigJson raw, string configPath)
    {
        if (raw.Kernel is null)
            throw new ClientConfigException(
                "client_config_invalid",
                $"client-config '{configPath}': секция 'kernel' обязательна");

        var host = string.IsNullOrWhiteSpace(raw.Kernel.Host) ? "127.0.0.1" : raw.Kernel.Host!;
        var port = raw.Kernel.Port ?? 0;
        if (port <= 0 || port > 65535)
            throw new ClientConfigException(
                "client_config_invalid",
                $"client-config '{configPath}': kernel.port должен быть 1..65535, получен {port}");

        if (string.IsNullOrWhiteSpace(raw.Graph))
            throw new ClientConfigException(
                "client_config_invalid",
                $"client-config '{configPath}': graph обязательное поле");

        return new ClientConfig(host, port, raw.Graph!);
    }
}

/// <summary>
/// Ошибка чтения/валидации client-config'а или транспорта к kernel'у.
/// Поле <see cref="Code"/> — стабильный машинный код (<c>client_config_not_found</c>,
/// <c>client_config_invalid</c>, <c>kernel_unreachable</c>).
/// </summary>
public sealed class ClientConfigException : Exception
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

/// <summary>
/// Сырая JSON-структура client-config'а (DTO для System.Text.Json source-gen).
/// </summary>
public sealed record ClientConfigJson(
    [property: JsonPropertyName("kernel")] ClientConfigKernelJson? Kernel,
    [property: JsonPropertyName("graph")] string? Graph);

public sealed record ClientConfigKernelJson(
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("port")] int? Port);

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ClientConfigJson))]
public partial class ClientConfigJsonContext : JsonSerializerContext
{
}
