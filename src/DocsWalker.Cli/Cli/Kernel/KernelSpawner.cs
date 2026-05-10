using System.Diagnostics;
using System.Globalization;

namespace DocsWalker.Cli.Cli.Kernel;

/// <summary>
/// Detached spawn ядра <c>DocsWalker.Kernel.exe</c> клиентом. После успешного запуска
/// родительский процесс CLI может exit'нуть — ядро живёт автономно.
/// <para>
/// На Windows ядро — отдельный exe с <c>OutputType=WinExe</c> (windows subsystem):
/// child-процесс не имеет console и не пытается унаследовать handles parent'а.
/// Это снимает класс ошибок «kernel падает на Console.Error.WriteLine после exit
/// CLI» — не нужны ни PowerShell-wrapper, ни <c>cmd /c start /B</c>.
/// </para>
/// <para>
/// На POSIX subsystem-понятия нет: spawn'им через <c>setsid</c> (fallback <c>nohup</c>),
/// перенаправляя stdio в /dev/null — child отвязывается от controlling terminal.
/// </para>
/// </summary>
internal static class KernelSpawner
{
    /// <summary>
    /// Спавнит детачд-ядро. Возвращает pid дочернего процесса (для логирования).
    /// </summary>
    /// <exception cref="KernelSpawnException">
    /// exe не найден, нет прав, sh/setsid недоступны на POSIX.
    /// </exception>
    public static int SpawnDetached(string kernelExePath)
    {
        if (string.IsNullOrEmpty(kernelExePath))
            throw new KernelSpawnException("invalid_exe_path", "kernelExePath is empty");
        if (!File.Exists(kernelExePath))
            throw new KernelSpawnException("kernel_exe_not_found",
                $"DocsWalker.Kernel.exe не найден по пути '{kernelExePath}'.");

        if (OperatingSystem.IsWindows())
            return SpawnWindows(kernelExePath);
        return SpawnPosix(kernelExePath);
    }

    private static int SpawnWindows(string exePath)
    {
        // DocsWalker.Kernel.exe — Windows subsystem (OutputType=WinExe). Без console.
        // Process.Start с UseShellExecute=false — простейший детач: child не наследует
        // console handles parent'а (потому что у него их нет — он WinExe).
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex)
        {
            throw new KernelSpawnException("spawn_failed", ex.Message, ex);
        }
        if (p is null)
            throw new KernelSpawnException("spawn_failed", "Process.Start returned null");

        var pid = p.Id;
        p.Dispose();
        return pid;
    }

    private static int SpawnPosix(string exePath)
    {
        var exeQuoted = EscapeShellArg(exePath);
        var shellScript =
            $"if command -v setsid >/dev/null 2>&1; then " +
            $"  setsid {exeQuoted} </dev/null >/dev/null 2>&1 & " +
            $"elif command -v nohup >/dev/null 2>&1; then " +
            $"  nohup {exeQuoted} </dev/null >/dev/null 2>&1 & " +
            $"else " +
            $"  echo 'spawn_helper_missing: setsid и nohup недоступны' 1>&2; exit 64; " +
            $"fi; echo $!";

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

    private static string EscapeShellArg(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Резолв пути к <c>DocsWalker.Kernel.exe</c>: ожидаем рядом с <paramref name="cliExePath"/>
    /// (стандартный publish-layout). Имя бинаря per-OS: <c>.exe</c> на Windows,
    /// без расширения на POSIX.
    /// </summary>
    public static string ResolveKernelExePath(string cliExePath)
    {
        var dir = Path.GetDirectoryName(cliExePath);
        if (string.IsNullOrEmpty(dir))
            throw new KernelSpawnException("kernel_exe_not_found",
                $"Не удалось определить каталог CLI exe: '{cliExePath}'");
        var name = OperatingSystem.IsWindows() ? "DocsWalker.Kernel.exe" : "DocsWalker.Kernel";
        return Path.Combine(dir, name);
    }
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
