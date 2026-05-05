namespace DocsWalker.Cli.Cli;

internal sealed record ParsedArgs(
    string CommandKebab,
    IReadOnlyDictionary<string, string> Params);

internal sealed record ArgParseError(string Code, string Message);

internal static class ArgParser
{
    public static (ParsedArgs? Args, ArgParseError? Error) Parse(string[] argv)
    {
        if (argv.Length == 0)
        {
            return (null, new ArgParseError(
                "no_command",
                "Не задано имя команды. Использование: docswalker <command> [--key=value ...]."));
        }

        var first = argv[0];
        if (first.StartsWith("--", StringComparison.Ordinal))
        {
            return (null, new ArgParseError(
                "no_command",
                "Первым аргументом должно быть имя команды, а не параметр."));
        }

        var commandKebab = first.Replace('_', '-');
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 1; i < argv.Length; i++)
        {
            var token = argv[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                return (null, new ArgParseError(
                    "invalid_argument",
                    $"Неименованный аргумент '{token}'. Ожидается '--key=value'."));
            }

            var eq = token.IndexOf('=');
            if (eq < 0)
            {
                return (null, new ArgParseError(
                    "invalid_argument",
                    $"Аргумент '{token}' без значения. Используйте '--key=value'."));
            }

            var key = token.Substring(2, eq - 2);
            var value = token[(eq + 1)..];

            if (key.Length == 0)
            {
                return (null, new ArgParseError(
                    "invalid_argument",
                    $"Пустое имя параметра в '{token}'."));
            }

            // Нормализуем имя параметра в kebab-case (на всякий случай, если LLM прислал snake_case).
            key = key.Replace('_', '-');

            if (!dict.TryAdd(key, value))
            {
                return (null, new ArgParseError(
                    "duplicate_parameter",
                    $"Параметр '--{key}' указан более одного раза."));
            }
        }

        return (new ParsedArgs(commandKebab, dict), null);
    }
}
