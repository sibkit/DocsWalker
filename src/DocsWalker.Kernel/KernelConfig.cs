using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocsWalker.Kernel;

/// <summary>
/// Параметры запуска kernel-а, прочитанные из JSON-файла. Путь к файлу
/// передаётся через <c>--config=&lt;path&gt;</c>.
///
/// <para>
/// Формат JSON (V2):
/// <code>
/// {
///   "bind": "127.0.0.1",
///   "port": 18080,
///   "db_path": "D:/.../docswalker.sqlite",
///   "graphs": ["docswalker"]
/// }
/// </code>
/// </para>
///
/// <para>
/// Поля:
/// <list type="bullet">
///   <item><c>bind</c> — IP-интерфейс Kestrel (default <c>127.0.0.1</c>).</item>
///   <item><c>port</c> — TCP-порт (0 = динамический; default 18080).</item>
///   <item><c>db_path</c> — обязательный, абсолютный путь к
///     SQLite-файлу. Один файл на kernel, все графы внутри него
///     (per database-model/README.md).</item>
///   <item><c>graphs</c> — обязательный массив имён графов. Имена
///     попадают первым сегментом URL <c>/{graph}</c>.</item>
/// </list>
/// </para>
/// </summary>
internal sealed record KernelConfig(
    string Bind,
    int Port,
    string DbPath,
    IReadOnlyList<string> Graphs)
{
    public const string DefaultBind = "127.0.0.1";
    public const int DefaultPort = 18080;
    public const string ReservedHealthName = "health";

    public static KernelConfig Read(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            throw new KernelConfigException("--config: путь не задан");
        }
        if (!File.Exists(configPath))
        {
            throw new KernelConfigException($"kernel config не найден: '{configPath}'");
        }
        string json;
        try
        {
            json = File.ReadAllText(configPath);
        }
        catch (Exception ex)
        {
            throw new KernelConfigException(
                $"не удалось прочитать kernel config '{configPath}': {ex.Message}", ex);
        }
        KernelConfigJson? raw;
        try
        {
            raw = JsonSerializer.Deserialize(json, KernelConfigJsonContext.Default.KernelConfigJson);
        }
        catch (JsonException ex)
        {
            throw new KernelConfigException(
                $"kernel config '{configPath}' — невалидный JSON: {ex.Message}", ex);
        }
        if (raw is null)
        {
            throw new KernelConfigException($"kernel config '{configPath}' пустой");
        }
        return Validate(raw, configPath);
    }

    private static KernelConfig Validate(KernelConfigJson raw, string configPath)
    {
        var bind = string.IsNullOrWhiteSpace(raw.Bind) ? DefaultBind : raw.Bind!;
        var port = raw.Port ?? DefaultPort;
        if (port < 0 || port > 65535)
        {
            throw new KernelConfigException(
                $"kernel config '{configPath}': port должен быть 0..65535, получен {port}");
        }
        if (string.IsNullOrWhiteSpace(raw.DbPath))
        {
            throw new KernelConfigException(
                $"kernel config '{configPath}': db_path обязательное поле");
        }
        string dbPath;
        try
        {
            dbPath = Path.GetFullPath(raw.DbPath!);
        }
        catch (Exception ex)
        {
            throw new KernelConfigException(
                $"kernel config '{configPath}': db_path невалиден: {ex.Message}", ex);
        }
        if (raw.Graphs is null || raw.Graphs.Count == 0)
        {
            throw new KernelConfigException(
                $"kernel config '{configPath}': graphs обязателен, минимум 1 граф");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var graphs = new List<string>(raw.Graphs.Count);
        foreach (var name in raw.Graphs)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new KernelConfigException(
                    $"kernel config '{configPath}': graph name пустой");
            }
            if (string.Equals(name, ReservedHealthName, StringComparison.Ordinal))
            {
                throw new KernelConfigException(
                    $"kernel config '{configPath}': graph name '{ReservedHealthName}' " +
                    "зарезервирован под /health");
            }
            if (!seen.Add(name))
            {
                throw new KernelConfigException(
                    $"kernel config '{configPath}': graph '{name}' встречается дважды");
            }
            graphs.Add(name);
        }
        return new KernelConfig(bind, port, dbPath, graphs);
    }

    public string Format()
    {
        var portStr = Port == 0 ? "dynamic" : Port.ToString(CultureInfo.InvariantCulture);
        return $"bind={Bind}, port={portStr}, db_path={DbPath}, graphs=[{string.Join(", ", Graphs)}]";
    }
}

internal sealed class KernelConfigException : Exception
{
    public KernelConfigException(string message) : base(message) { }
    public KernelConfigException(string message, Exception inner) : base(message, inner) { }
}

internal sealed record KernelConfigJson(
    [property: JsonPropertyName("bind")] string? Bind,
    [property: JsonPropertyName("port")] int? Port,
    [property: JsonPropertyName("db_path")] string? DbPath,
    [property: JsonPropertyName("graphs")] List<string>? Graphs);

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(KernelConfigJson))]
internal partial class KernelConfigJsonContext : JsonSerializerContext
{
}
