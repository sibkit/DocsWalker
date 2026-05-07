using System.Diagnostics;

namespace DocsWalker.Core.Server;

/// <summary>
/// Проверяет, жив ли процесс с заданным pid и является ли он «нашим»
/// (сверяется с путём exe-файла из run.pid).
/// </summary>
public static class StalePidDetector
{
    /// <summary>
    /// Возвращает <c>true</c>, если процесс <paramref name="pid"/> существует
    /// и — при наличии <paramref name="exePath"/> — запущен из того же бинаря.
    /// Возвращает <c>false</c> при несуществующем pid или несовпадении exe.
    /// </summary>
    public static bool IsAlive(int pid, string? exePath)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            if (process.HasExited)
                return false;

            if (exePath is null)
                return true;

            var actualExe = GetExePath(process);
            if (actualExe is null)
                return true; // не удалось проверить — считаем живым (безопасная сторона)

            return string.Equals(
                actualExe, exePath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false; // pid не найден
        }
        catch (InvalidOperationException)
        {
            return false; // процесс завершился
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string? GetExePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
