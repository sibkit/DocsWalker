namespace DocsWalker.Kernel;

/// <summary>
/// Параметры командной строки <c>DocsWalker.Kernel.exe</c>. Сейчас единственный
/// аргумент — путь к kernel-config'у (<c>--config=&lt;path&gt;</c>); все
/// остальные параметры (bind/port/graphs/idle-timeout) живут внутри JSON-файла.
/// См. <see cref="KernelConfig"/>.
/// </summary>
internal sealed record KernelOptions(string ConfigPath)
{
    /// <summary>
    /// Парсит argv, ищет токен вида <c>--config=&lt;path&gt;</c>. При
    /// отсутствии или некорректной форме — выставляет <paramref name="error"/>.
    /// </summary>
    public static KernelOptions ParseArgv(string[] argv, out string? error)
    {
        error = null;
        string? configPath = null;
        foreach (var token in argv)
        {
            if (!token.StartsWith("--", StringComparison.Ordinal)) continue;
            var eq = token.IndexOf('=');
            if (eq < 0) continue;
            var key = token.Substring(2, eq - 2).Replace('_', '-');
            var value = token[(eq + 1)..];
            if (key == "config") configPath = value;
        }

        if (string.IsNullOrWhiteSpace(configPath))
        {
            error = "--config=<path> обязателен (путь к kernel-config JSON-файлу)";
            return new KernelOptions(string.Empty);
        }

        return new KernelOptions(configPath!);
    }
}
