using DocsWalker.Core.Server;

namespace DocsWalker.Cli.Cli;

internal static class PidFileReader
{
    internal static bool TryReadLivePid(string rootPath, out int pid)
    {
        pid = 0;
        var pidPath = Path.Combine(
            Path.GetFullPath(rootPath), "docs", ".docswalker", "run.pid");
        try
        {
            var text = File.ReadAllText(pidPath, System.Text.Encoding.UTF8);
            var nl = text.IndexOf('\n');
            var line0 = nl >= 0 ? text[..nl].Trim() : text.Trim();
            var line1 = nl >= 0 && nl + 1 < text.Length ? text[(nl + 1)..].Trim() : null;
            if (!int.TryParse(line0, out pid)) return false;
            return StalePidDetector.IsAlive(pid, string.IsNullOrEmpty(line1) ? null : line1);
        }
        catch
        {
            return false;
        }
    }
}
