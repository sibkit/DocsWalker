using System.Text;

namespace DocsWalker.Cli.Cli.Repl;

/// <summary>
/// Минимальный построчный ввод поверх <see cref="Console.ReadKey"/>:
/// Enter — возврат строки; Backspace — удаление последнего символа;
/// Ctrl+C — отмена текущего ввода (возврат пустой строки, prompt
/// перерисовывается); Ctrl+D / Ctrl+Z (на пустой строке) — EOF (null).
/// История и стрелки в MVP не поддерживаются.
/// <para>
/// На время чтения переключает <see cref="Console.TreatControlCAsInput"/>
/// в true — иначе Ctrl+C триггернёт глобальный <see cref="SignalHandler"/>
/// и завершит сервер. Восстанавливает прежнее значение в finally.
/// </para>
/// <para>
/// Цикл блокирующий: в .NET нет API для отмены <see cref="Console.ReadKey"/>
/// извне. Используем poll <see cref="Console.KeyAvailable"/> с малой задержкой,
/// чтобы периодически проверять <paramref name="ct"/>.
/// </para>
/// </summary>
internal static class LineReader
{
    public static string? ReadLine(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var prevTreatCtrlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;

        try
        {
            while (true)
            {
                while (!Console.KeyAvailable)
                {
                    if (ct.IsCancellationRequested) return null;
                    Thread.Sleep(20);
                }

                var key = Console.ReadKey(intercept: true);

                if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                {
                    if (key.Key == ConsoleKey.C)
                    {
                        Console.WriteLine("^C");
                        return string.Empty;
                    }
                    if (key.Key == ConsoleKey.D || key.Key == ConsoleKey.Z)
                    {
                        if (sb.Length == 0)
                        {
                            Console.WriteLine();
                            return null;
                        }
                        // EOF в середине строки игнорируем — пользователь завершит
                        // ввод Enter'ом.
                        continue;
                    }
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return sb.ToString();
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0)
                    {
                        sb.Remove(sb.Length - 1, 1);
                        Console.Write("\b \b");
                    }
                    continue;
                }

                if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                }
            }
        }
        finally
        {
            Console.TreatControlCAsInput = prevTreatCtrlC;
        }
    }
}
