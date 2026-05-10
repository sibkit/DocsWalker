using System.Diagnostics;
using System.Globalization;

namespace DocsWalker.Cli.Cli.Kernel;

/// <summary>
/// Detached spawn ядра DocsWalker клиентом. После успешного запуска родительский
/// процесс может exit'нуть — ядро живёт автономно.
/// <para>
/// <b>Windows:</b> <see cref="Process.Start(ProcessStartInfo)"/> с
/// <c>CreateNoWindow=true</c> и <c>UseShellExecute=false</c>. Без окна ядро теряет
/// связь с родительской console group, Ctrl-C в терминале клиента не передаётся.
/// </para>
/// <para>
/// <b>POSIX:</b> wrap через <c>/bin/sh -c "setsid &lt;exe&gt; ... &lt;/dev/null
/// &gt;/dev/null 2&gt;&amp;1 &amp;"</c>. <c>setsid</c> создаёт новую сессию;
/// fallback — <c>nohup</c>; редирект stdio в /dev/null отвязывает от controlling
/// terminal.
/// </para>
/// <para>
/// Spawn — strategy.md «Принятые решения» #8.
/// </para>
/// </summary>
internal static class KernelSpawner
{
    /// <summary>
    /// Спавнит детачд-ядро. Возвращает pid дочернего процесса (для логирования);
    /// сам процесс не отслеживается. <paramref name="kernelArgs"/> — argv для CLI
    /// после имени exe (например, <c>["kernel", "--port=0"]</c>).
    /// </summary>
    /// <exception cref="KernelSpawnException">
    /// Не удалось запустить процесс (exe не найден, нет прав, sh/setsid недоступны
    /// на POSIX).
    /// </exception>
    public static int SpawnDetached(string exePath, IEnumerable<string> kernelArgs)
    {
        if (string.IsNullOrEmpty(exePath))
            throw new KernelSpawnException("invalid_exe_path", "exePath is empty");

        if (OperatingSystem.IsWindows())
            return SpawnWindows(exePath, kernelArgs);
        return SpawnPosix(exePath, kernelArgs);
    }

    private static int SpawnWindows(string exePath, IEnumerable<string> kernelArgs)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Redirect — чтобы child получил отдельные pipe-handles вместо унаследования
            // консольных handle'ов родителя; мы их сразу закроем после Start.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in kernelArgs) psi.ArgumentList.Add(a);

        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex)
        {
            throw new KernelSpawnException("spawn_failed", ex.Message, ex);
        }
        if (p is null)
            throw new KernelSpawnException("spawn_failed", "Process.Start returned null");

        // Закрываем pipe-handles в parent'е — child уже получил свои.
        try { p.StandardInput.Close();  } catch { }
        try { p.StandardOutput.Close(); } catch { }
        try { p.StandardError.Close();  } catch { }

        var pid = p.Id;
        // НЕ диспозим: dispose Process при HasExited=false работает, но мы уже
        // отвязались; pid сохранён.
        p.Dispose();
        return pid;
    }

    private static int SpawnPosix(string exePath, IEnumerable<string> kernelArgs)
    {
        // Собираем shell-команду: setsid <exe> <args...> </dev/null >/dev/null 2>&1 &
        // Если setsid недоступен — пробуем nohup.
        var argsJoined = string.Join(' ', kernelArgs.Select(EscapeShellArg));
        var exeQuoted = EscapeShellArg(exePath);

        // setsid сначала, fallback — nohup.
        // command -v <prog> возвращает 0 если найдено.
        var shellScript =
            $"if command -v setsid >/dev/null 2>&1; then " +
            $"  setsid {exeQuoted} {argsJoined} </dev/null >/dev/null 2>&1 & " +
            $"elif command -v nohup >/dev/null 2>&1; then " +
            $"  nohup {exeQuoted} {argsJoined} </dev/null >/dev/null 2>&1 & " +
            $"else " +
            $"  echo 'spawn_helper_missing: setsid и nohup недоступны' 1>&2; exit 64; " +
            $"fi; " +
            // Печатаем pid дочернего процесса в stdout — родитель прочтёт.
            $"echo $!";

        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(shellScript);

        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex)
        {
            throw new KernelSpawnException("spawn_failed", ex.Message, ex);
        }
        if (p is null)
            throw new KernelSpawnException("spawn_failed", "Process.Start returned null");

        try
        {
            p.WaitForExit(5000);
            if (p.ExitCode == 64)
                throw new KernelSpawnException("spawn_helper_missing",
                    "setsid и nohup недоступны на этой системе");
            if (p.ExitCode != 0)
            {
                var err = p.StandardError.ReadToEnd().Trim();
                throw new KernelSpawnException("spawn_failed", $"sh exited {p.ExitCode}: {err}");
            }
            var pidStr = p.StandardOutput.ReadToEnd().Trim();
            if (!int.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                throw new KernelSpawnException("spawn_failed", $"sh не вернул pid: '{pidStr}'");
            return pid;
        }
        finally
        {
            p.Dispose();
        }
    }

    /// <summary>
    /// Минимальный shell-escape: оборачиваем в single-quotes, экранируем внутренние
    /// quotes как <c>'\''</c>. Достаточно для путей и наших argv-значений.
    /// </summary>
    private static string EscapeShellArg(string s) => "'" + s.Replace("'", "'\\''") + "'";
}

/// <summary>
/// Не удалось спавнить ядро: бинарь не найден, нет прав на exec, на POSIX отсутствуют
/// <c>setsid</c>/<c>nohup</c>.
/// </summary>
internal sealed class KernelSpawnException : Exception
{
    public string Code { get; }
    public KernelSpawnException(string code, string message, Exception? inner = null)
        : base(message, inner) { Code = code; }
}
