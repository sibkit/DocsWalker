using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocsWalker.Kernel;

internal static class TxJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static PendingTx Capture(string docsRoot, string graphName)
    {
        var journalRoot = GetJournalRoot(docsRoot, graphName);
        Directory.CreateDirectory(journalRoot);

        var txId = GenerateTxId();
        var txDir = Path.Combine(journalRoot, txId);
        var snapshotDir = Path.Combine(txDir, "snapshot");
        Directory.CreateDirectory(snapshotDir);

        try
        {
            CopyDirectory(GetFullDirectoryPath(docsRoot), snapshotDir);
            return new PendingTx(
                txId,
                txDir,
                Path.Combine(txDir, "tx.json"),
                graphName);
        }
        catch
        {
            TryDeleteDirectory(txDir);
            throw;
        }
    }

    public static JsonObject Rollback(string docsRoot, string graphName, string txId)
    {
        var journalRoot = GetJournalRoot(docsRoot, graphName);
        var txDir = Path.Combine(journalRoot, txId);
        if (!Directory.Exists(txDir))
            return BuildError("rollback_not_found", new JsonObject { ["tx_id"] = txId });

        var snapshotDir = Path.Combine(txDir, "snapshot");
        if (!Directory.Exists(snapshotDir))
            return BuildError("rollback_not_found", new JsonObject { ["tx_id"] = txId });

        try
        {
            RestoreSnapshot(snapshotDir, docsRoot);
            TryWriteJson(
                Path.Combine(txDir, "rolled_back.json"),
                new JsonObject
                {
                    ["tx_id"] = txId,
                    ["rolled_back_at_utc"] = DateTimeOffset.UtcNow.ToString("O"),
                });

            return new JsonObject
            {
                ["result"] = new JsonObject
                {
                    ["rolled_back"] = txId,
                },
            };
        }
        catch (Exception ex)
        {
            return BuildError(
                "rollback_failed",
                new JsonObject
                {
                    ["tx_id"] = txId,
                    ["error"] = ex.Message,
                });
        }
    }

    private static JsonObject BuildError(string code, JsonObject details) =>
        new()
        {
            ["code"] = code,
            ["details"] = details,
        };

    private static string GetJournalRoot(string docsRoot, string graphName)
    {
        var docsRootFull = GetFullDirectoryPath(docsRoot);
        ValidateSafeDocsRoot(docsRootFull);
        var parent = Directory.GetParent(docsRootFull)
            ?? throw new IOException("Docs root must have a parent directory.");
        return Path.Combine(parent.FullName, ".dw", "tx-journal", ToSafeName(graphName));
    }

    private static string GetFullDirectoryPath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void ValidateSafeDocsRoot(string docsRoot)
    {
        var root = Path.GetPathRoot(docsRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(docsRoot) ||
            string.Equals(docsRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Unsafe docs root for transaction rollback.");
        }
    }

    private static string GenerateTxId()
    {
        Span<byte> suffix = stackalloc byte[4];
        RandomNumberGenerator.Fill(suffix);
        return "tx_" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "_" + Convert.ToHexString(suffix);
    }

    private static string ToSafeName(string value)
    {
        var chars = value
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')
            .ToArray();
        var safe = new string(chars);
        return string.IsNullOrWhiteSpace(safe) ? "graph" : safe;
    }

    private static void RestoreSnapshot(string snapshotDir, string docsRoot)
    {
        var target = GetFullDirectoryPath(docsRoot);
        ValidateSafeDocsRoot(target);
        var parent = Directory.GetParent(target)
            ?? throw new IOException("Docs root must have a parent directory.");
        var restoreRoot = Path.Combine(parent.FullName, ".dw", "tx-journal", "_restore");
        Directory.CreateDirectory(restoreRoot);
        var suffix = Guid.NewGuid().ToString("N");
        var restoreTemp = Path.Combine(restoreRoot, "restore-" + suffix);
        var backup = Path.Combine(restoreRoot, "backup-" + suffix);

        CopyDirectory(snapshotDir, restoreTemp);

        try
        {
            if (Directory.Exists(target))
                Directory.Move(target, backup);

            Directory.Move(restoreTemp, target);
            TryDeleteDirectory(backup);
        }
        catch
        {
            if (!Directory.Exists(target) && Directory.Exists(backup))
                Directory.Move(backup, target);
            throw;
        }
        finally
        {
            TryDeleteDirectory(restoreTemp);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(destination, rel));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private static void TryWriteJson(string path, JsonObject value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value.ToJsonString(JsonOptions));
        }
        catch
        {
            // best effort
        }
    }

    internal sealed class PendingTx : IDisposable
    {
        private readonly string _txDir;
        private readonly string _metadataPath;
        private readonly string _graphName;
        private bool _committed;
        private bool _discarded;

        public PendingTx(
            string txId,
            string txDir,
            string metadataPath,
            string graphName)
        {
            TxId = txId;
            _txDir = txDir;
            _metadataPath = metadataPath;
            _graphName = graphName;
        }

        public string TxId { get; }

        public string Commit()
        {
            File.WriteAllText(
                _metadataPath,
                new JsonObject
                {
                    ["tx_id"] = TxId,
                    ["graph"] = _graphName,
                    ["committed_at_utc"] = DateTimeOffset.UtcNow.ToString("O"),
                }.ToJsonString(JsonOptions));
            _committed = true;
            return TxId;
        }

        public void Discard()
        {
            if (_committed || _discarded)
                return;

            TryDeleteDirectory(_txDir);
            _discarded = true;
        }

        public void Dispose() => Discard();
    }
}
