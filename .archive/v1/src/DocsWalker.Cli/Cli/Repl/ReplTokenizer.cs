using System.Text;

namespace DocsWalker.Cli.Cli.Repl;

/// <summary>
/// Минимальный shell-like разбор строки REPL в argv.
/// Поддерживает разделение по whitespace и две формы кавычек:
/// двойные "..." и одинарные '...'. Внутри кавычек whitespace не разделяет.
/// Кавычки сами в результат не попадают. Backslash-escape не поддерживается:
/// для значений с обоими видами кавычек используются разные внешние/внутренние
/// (например, --operations='[{"op":"foo"}]').
/// </summary>
internal static class ReplTokenizer
{
    public static string[] Tokenize(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        char? quote = null;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (quote is not null)
            {
                if (c == quote.Value) { quote = null; continue; }
                sb.Append(c);
                continue;
            }

            if (c == '"' || c == '\'') { quote = c; continue; }

            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0) result.Add(sb.ToString());
        return result.ToArray();
    }
}
