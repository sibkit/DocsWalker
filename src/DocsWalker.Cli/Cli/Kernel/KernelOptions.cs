using System.Globalization;

namespace DocsWalker.Cli.Cli.Kernel;

/// <summary>
/// Параметры запуска ядра <c>docswalker kernel</c>.
/// <para>
/// <see cref="Bind"/> — IP-интерфейс прослушивания. По умолчанию <c>127.0.0.1</c>
/// (local-only, см. (#306) docs/DocsWalker.yml).
/// </para>
/// <para>
/// <see cref="Port"/> — TCP-порт. <c>0</c> = динамический (Kestrel выберет
/// свободный, фактический порт публикуется в <c>kernel.json</c> на step-03).
/// </para>
/// <para>
/// <see cref="RootIdleTimeout"/> — после этого периода без обращений к
/// конкретному root его entry в <see cref="RootRegistry"/> выгружается
/// (см. (#316) docs/DocsWalker.yml). Default 10 минут. Конфигурируется
/// через <c>--root-idle-timeout=&lt;duration&gt;</c>.
/// </para>
/// <para>
/// Format duration: <c>10m</c> | <c>30s</c> | <c>1h</c> | <c>500ms</c>.
/// </para>
/// </summary>
internal sealed record KernelOptions(
    string Bind,
    int Port,
    TimeSpan RootIdleTimeout)
{
    public static readonly TimeSpan DefaultRootIdleTimeout = TimeSpan.FromMinutes(10);
    public const string DefaultBind = "127.0.0.1";
    public const int DefaultPort = 0;

    public static KernelOptions Parse(IReadOnlyDictionary<string, string> args, out string? error)
    {
        error = null;

        var bind = args.TryGetValue("bind", out var b) && !string.IsNullOrWhiteSpace(b) ? b : DefaultBind;

        int port = DefaultPort;
        if (args.TryGetValue("port", out var rawPort))
        {
            if (!int.TryParse(rawPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
                || port < 0 || port > 65535)
            {
                error = $"--port: ожидается целое 0..65535, получено '{rawPort}'.";
                return new KernelOptions(bind, DefaultPort, DefaultRootIdleTimeout);
            }
        }

        var rootIdle = DefaultRootIdleTimeout;
        if (args.TryGetValue("root-idle-timeout", out var rawIdle))
        {
            if (!TryParseDuration(rawIdle, out rootIdle))
            {
                error = $"--root-idle-timeout: ожидается duration (Ns/Nm/Nh/Nms), получено '{rawIdle}'.";
                return new KernelOptions(bind, port, DefaultRootIdleTimeout);
            }
        }

        return new KernelOptions(bind, port, rootIdle);
    }

    /// <summary>
    /// Парс duration в формате <c>500ms</c> | <c>30s</c> | <c>10m</c> | <c>1h</c>.
    /// Регистр суффикса не важен. Целое неотрицательное число + один из суффиксов.
    /// </summary>
    internal static bool TryParseDuration(string raw, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrEmpty(raw)) return false;

        // Сначала пробуем самый длинный суффикс (ms), потом одиночные.
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

    public string Format() =>
        $"bind={Bind}, port={(Port == 0 ? "dynamic" : Port.ToString(CultureInfo.InvariantCulture))}, root_idle_timeout={FormatDuration(RootIdleTimeout)}";

    internal static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalMilliseconds < 1000) return $"{(long)ts.TotalMilliseconds}ms";
        if (ts.TotalSeconds < 60) return $"{(long)ts.TotalSeconds}s";
        if (ts.TotalMinutes < 60) return $"{(long)ts.TotalMinutes}m";
        return $"{(long)ts.TotalHours}h";
    }
}
