using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocsWalker.Kernel;

/// <summary>
/// Параметры запуска ядра DocsWalker, прочитанные из JSON-файла. Путь к
/// файлу передаётся в argv через <c>--config=&lt;path&gt;</c>; см.
/// <see cref="KernelOptions"/>.
/// <para>
/// Поля JSON:
/// </para>
/// <list type="bullet">
///   <item><c>bind</c> — IP-интерфейс, на котором слушает Kestrel (по
///   умолчанию <c>127.0.0.1</c>, local-only).</item>
///   <item><c>port</c> — TCP-порт. <c>0</c> = динамический.</item>
///   <item><c>graphs</c> — обязательный словарь <c>graph_name → storage_path</c>.
///   Каждый <c>graph_name</c> присутствует в URL <c>/db/&lt;name&gt;/rpc</c>;
///   <c>storage_path</c> указывает на папку <c>docs/</c> графа (не на
///   корень репозитория).</item>
///   <item><c>graph_idle_timeout</c> — duration; зарезервировано для
///   будущего per-graph cache-eviction (сейчас не используется).</item>
/// </list>
/// </summary>
internal sealed record KernelConfig(
    string Bind,
    int Port,
    IReadOnlyList<KernelGraphConfig> Graphs,
    TimeSpan GraphIdleTimeout)
{
    public const string DefaultBind = "127.0.0.1";
    public const int DefaultPort = 0;
    public static readonly TimeSpan DefaultGraphIdleTimeout = TimeSpan.FromMinutes(10);

    public static KernelConfig Read(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            throw new KernelConfigException("--config: путь не задан");
        if (!File.Exists(configPath))
            throw new KernelConfigException($"kernel config не найден: '{configPath}'");

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
            throw new KernelConfigException($"kernel config '{configPath}' пустой");

        return Validate(raw, configPath);
    }

    private static KernelConfig Validate(KernelConfigJson raw, string configPath)
    {
        var bind = string.IsNullOrWhiteSpace(raw.Bind) ? DefaultBind : raw.Bind!;

        var port = raw.Port ?? DefaultPort;
        if (port < 0 || port > 65535)
            throw new KernelConfigException(
                $"kernel config '{configPath}': port должен быть 0..65535, получен {port}");

        var graphIdle = DefaultGraphIdleTimeout;
        if (!string.IsNullOrEmpty(raw.GraphIdleTimeout))
        {
            if (!TryParseDuration(raw.GraphIdleTimeout!, out graphIdle))
                throw new KernelConfigException(
                    $"kernel config '{configPath}': graph_idle_timeout должен быть duration " +
                    $"(Ns/Nm/Nh/Nms), получено '{raw.GraphIdleTimeout}'");
        }

        if (raw.Graphs is null || raw.Graphs.Count == 0)
            throw new KernelConfigException(
                $"kernel config '{configPath}': graphs обязательное поле, минимум 1 граф");

        var graphs = new List<KernelGraphConfig>(raw.Graphs.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kvp in raw.Graphs)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key))
                throw new KernelConfigException(
                    $"kernel config '{configPath}': graph_name пустой");
            if (!seen.Add(kvp.Key))
                throw new KernelConfigException(
                    $"kernel config '{configPath}': graph_name '{kvp.Key}' встречается дважды");
            if (string.IsNullOrWhiteSpace(kvp.Value))
                throw new KernelConfigException(
                    $"kernel config '{configPath}': graphs['{kvp.Key}'] storage_path пустой");

            string storagePath;
            try
            {
                storagePath = Path.GetFullPath(kvp.Value);
            }
            catch (Exception ex)
            {
                throw new KernelConfigException(
                    $"kernel config '{configPath}': graphs['{kvp.Key}'] storage_path " +
                    $"невалиден: {ex.Message}", ex);
            }
            graphs.Add(new KernelGraphConfig(kvp.Key, storagePath));
        }

        return new KernelConfig(bind, port, graphs, graphIdle);
    }

    public string Format()
    {
        var graphsStr = string.Join(", ",
            Graphs.Select(g => $"{g.Name}→{g.StoragePath}"));
        var portStr = Port == 0 ? "dynamic" : Port.ToString(CultureInfo.InvariantCulture);
        return $"bind={Bind}, port={portStr}, graphs=[{graphsStr}], " +
               $"graph_idle_timeout={FormatDuration(GraphIdleTimeout)}";
    }

    internal static bool TryParseDuration(string raw, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrEmpty(raw)) return false;

        string suffix;
        long multiplierTicks;
        if (raw.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            suffix = raw[..^2];
            multiplierTicks = TimeSpan.TicksPerMillisecond;
        }
        else if (raw.EndsWith('s') || raw.EndsWith('S'))
        {
            suffix = raw[..^1];
            multiplierTicks = TimeSpan.TicksPerSecond;
        }
        else if (raw.EndsWith('m') || raw.EndsWith('M'))
        {
            suffix = raw[..^1];
            multiplierTicks = TimeSpan.TicksPerMinute;
        }
        else if (raw.EndsWith('h') || raw.EndsWith('H'))
        {
            suffix = raw[..^1];
            multiplierTicks = TimeSpan.TicksPerHour;
        }
        else
        {
            return false;
        }

        if (!long.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0)
            return false;

        try
        {
            value = TimeSpan.FromTicks(checked(n * multiplierTicks));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    internal static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalMilliseconds < 1000) return $"{(long)ts.TotalMilliseconds}ms";
        if (ts.TotalSeconds < 60) return $"{(long)ts.TotalSeconds}s";
        if (ts.TotalMinutes < 60) return $"{(long)ts.TotalMinutes}m";
        return $"{(long)ts.TotalHours}h";
    }
}

/// <summary>
/// Описание одного графа в kernel-config'е.
/// </summary>
internal sealed record KernelGraphConfig(string Name, string StoragePath);

/// <summary>
/// Ошибка чтения/валидации kernel-config'а. Поднимается из
/// <see cref="KernelConfig.Read"/>.
/// </summary>
internal sealed class KernelConfigException : Exception
{
    public KernelConfigException(string message) : base(message) { }
    public KernelConfigException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Сырая JSON-структура kernel-config'а (DTO для System.Text.Json source-gen).
/// </summary>
internal sealed record KernelConfigJson(
    [property: JsonPropertyName("bind")] string? Bind,
    [property: JsonPropertyName("port")] int? Port,
    [property: JsonPropertyName("graphs")] Dictionary<string, string>? Graphs,
    [property: JsonPropertyName("graph_idle_timeout")] string? GraphIdleTimeout);

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(KernelConfigJson))]
internal partial class KernelConfigJsonContext : JsonSerializerContext
{
}
