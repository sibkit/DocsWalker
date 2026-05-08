using DocsWalker.Core.Sessions;

namespace DocsWalker.Tests;

/// <summary>
/// Инфраструктура seen-state процесса: <see cref="SessionState"/>,
/// <see cref="SessionFile"/>, <see cref="DocsChecksum"/>.
/// Тесты не задействуют сетевые/IPC-каналы: чистая работа с RAM и temp-каталогом.
/// </summary>
public class SessionsInfrastructureTests
{
    private static readonly DateTime Now = new(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc);

    // ── SessionState ────────────────────────────────────────────────────────

    [Fact]
    public void EnsureSession_NewId_CreatesDirtySet()
    {
        var st = new SessionState();
        var id = Guid.NewGuid();

        var set = st.EnsureSession(id, Now);

        Assert.True(set.Dirty);
        Assert.Equal(Now, set.Created);
        Assert.Equal(Now, set.LastUsed);
        Assert.Empty(set.Ids);
        Assert.Same(set, st.Sessions[id]);
    }

    [Fact]
    public void MarkSeen_AddsIdsAndUpdatesLastUsed()
    {
        var st = new SessionState();
        var id = Guid.NewGuid();
        st.MarkSeen(id, new[] { 10, 20, 30 }, Now);
        var later = Now.AddMinutes(5);
        st.MarkSeen(id, new[] { 30, 40 }, later);

        var set = st.Sessions[id];
        Assert.Equal(new[] { 10, 20, 30, 40 }, set.Ids.OrderBy(x => x));
        Assert.Equal(later, set.LastUsed);
        Assert.True(set.Dirty);
    }

    [Fact]
    public void Filter_SplitsSeenAndUnseen()
    {
        var st = new SessionState();
        var id = Guid.NewGuid();
        st.MarkSeen(id, new[] { 1, 2, 3 }, Now);

        var (seen, unseen) = st.Filter(id, new[] { 1, 4, 2, 5 });

        Assert.Equal(new[] { 1, 2 }, seen.OrderBy(x => x));
        Assert.Equal(new[] { 4, 5 }, unseen.OrderBy(x => x));
    }

    [Fact]
    public void Filter_UnknownSession_AllUnseen()
    {
        var st = new SessionState();
        var (seen, unseen) = st.Filter(Guid.NewGuid(), new[] { 1, 2 });
        Assert.Empty(seen);
        Assert.Equal(new[] { 1, 2 }, unseen);
    }

    [Fact]
    public void RemoveFromAll_RemovesAcrossAllSessions()
    {
        var st = new SessionState();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        st.MarkSeen(a, new[] { 1, 2, 3 }, Now);
        st.MarkSeen(b, new[] { 2, 4 }, Now);
        // dirty флаги уже true; сбросим, чтобы поймать «отметили dirty по факту удаления»
        foreach (var (_, set) in st.Sessions) set.Dirty = false;

        st.RemoveFromAll(new[] { 2, 5 });

        Assert.Equal(new[] { 1, 3 }, st.Sessions[a].Ids.OrderBy(x => x));
        Assert.Equal(new[] { 4 }, st.Sessions[b].Ids);
        Assert.True(st.Sessions[a].Dirty);
        Assert.True(st.Sessions[b].Dirty);
    }

    [Fact]
    public void ResetSeen_ExistingSession_ClearsIds()
    {
        var st = new SessionState();
        var id = Guid.NewGuid();
        st.MarkSeen(id, new[] { 1, 2, 3 }, Now);
        var later = Now.AddMinutes(10);

        st.ResetSeen(id, later);

        Assert.Empty(st.Sessions[id].Ids);
        Assert.Equal(later, st.Sessions[id].LastUsed);
        Assert.True(st.Sessions[id].Dirty);
    }

    [Fact]
    public void EvictExpired_RemovesOnlyExpired()
    {
        var st = new SessionState();
        var fresh = Guid.NewGuid();
        var stale = Guid.NewGuid();
        st.MarkSeen(fresh, new[] { 1 }, Now);
        var ten_days_ago = Now.AddDays(-10);
        // прямой Restore чтобы заранее задать lastUsed
        st.RestoreSession(stale, new SeenSet(new[] { 9 }, ten_days_ago, ten_days_ago));

        var evicted = st.EvictExpired(SessionState.TimeToLive, Now);

        Assert.Equal(new[] { stale }, evicted);
        Assert.True(st.Sessions.ContainsKey(fresh));
        Assert.False(st.Sessions.ContainsKey(stale));
    }

    // ── SessionFile roundtrip ──────────────────────────────────────────────

    [Fact]
    public void SaveAll_Then_LoadAll_PreservesIdsAndTimestamps()
    {
        var dir = NewTempDir();
        try
        {
            var st = new SessionState();
            var id = Guid.NewGuid();
            var created = Now.AddHours(-2);
            var lastUsed = Now;
            st.RestoreSession(id, new SeenSet(new[] { 100, 200, 300 }, created, lastUsed));
            st.Sessions[id].Dirty = true;

            SessionFile.SaveAll(st, dir);

            // dirty снят после успешной записи
            Assert.False(st.Sessions[id].Dirty);
            Assert.True(File.Exists(Path.Combine(dir, id.ToString("D") + ".yml")));

            var loaded = SessionFile.LoadAll(dir);
            Assert.True(loaded.Sessions.ContainsKey(id));
            var set = loaded.Sessions[id];
            Assert.False(set.Dirty);
            Assert.Equal(new[] { 100, 200, 300 }, set.Ids.OrderBy(x => x));
            Assert.Equal(created, set.Created);
            Assert.Equal(lastUsed, set.LastUsed);
        }
        finally { CleanUp(dir); }
    }

    [Fact]
    public void SaveAll_NoDirty_DoesNotCreateDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dw-sessions-" + Guid.NewGuid().ToString("N"));
        try
        {
            var st = new SessionState();
            // empty state — никаких записей, папка не создаётся
            SessionFile.SaveAll(st, dir);
            Assert.False(Directory.Exists(dir));
        }
        finally { CleanUp(dir); }
    }

    [Fact]
    public void LoadAll_NonExistentDir_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dw-sessions-" + Guid.NewGuid().ToString("N"));
        var st = SessionFile.LoadAll(dir);
        Assert.Empty(st.Sessions);
    }

    [Fact]
    public void WipeAll_RemovesYmlFiles_KeepsOthers()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, Guid.NewGuid().ToString("D") + ".yml"), "x");
            File.WriteAllText(Path.Combine(dir, ".checksum"), "abc");

            SessionFile.WipeAll(dir);

            Assert.Empty(Directory.EnumerateFiles(dir, "*.yml"));
            Assert.True(File.Exists(Path.Combine(dir, ".checksum")));
        }
        finally { CleanUp(dir); }
    }

    // ── DocsChecksum ───────────────────────────────────────────────────────

    [Fact]
    public void ComputeForDocs_DeterministicForSameContent()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.yml"), "id: 1\n");
            File.WriteAllText(Path.Combine(dir, "b.yml"), "id: 2\n");

            var h1 = DocsChecksum.ComputeForDocs(dir, ".docswalker/sessions");
            var h2 = DocsChecksum.ComputeForDocs(dir, ".docswalker/sessions");

            Assert.Equal(h1, h2);
            Assert.Equal(64, h1.Length); // SHA-256 hex
        }
        finally { CleanUp(dir); }
    }

    [Fact]
    public void ComputeForDocs_FileEdited_HashChanges()
    {
        var dir = NewTempDir();
        try
        {
            var p = Path.Combine(dir, "a.yml");
            File.WriteAllText(p, "id: 1\n");
            var before = DocsChecksum.ComputeForDocs(dir, ".docswalker/sessions");
            File.WriteAllText(p, "id: 2\n");
            var after = DocsChecksum.ComputeForDocs(dir, ".docswalker/sessions");
            Assert.NotEqual(before, after);
        }
        finally { CleanUp(dir); }
    }

    [Fact]
    public void ComputeForDocs_ExcludesSessionsSubtree()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.yml"), "id: 1\n");
            var sessionsDir = Path.Combine(dir, ".docswalker", "sessions");
            Directory.CreateDirectory(sessionsDir);

            var before = DocsChecksum.ComputeForDocs(dir, ".docswalker/sessions");

            File.WriteAllText(Path.Combine(sessionsDir, Guid.NewGuid().ToString("D") + ".yml"), "seen: [1]\n");

            var after = DocsChecksum.ComputeForDocs(dir, ".docswalker/sessions");
            Assert.Equal(before, after);
        }
        finally { CleanUp(dir); }
    }

    [Fact]
    public void Stored_RoundtripsThroughDisk()
    {
        var dir = NewTempDir();
        try
        {
            var p = Path.Combine(dir, ".checksum");
            DocsChecksum.WriteStored(p, "deadbeef");
            Assert.Equal("deadbeef", DocsChecksum.ReadStored(p));
        }
        finally { CleanUp(dir); }
    }

    [Fact]
    public void Stored_MissingFile_ReturnsNull()
    {
        var dir = NewTempDir();
        try
        {
            var p = Path.Combine(dir, ".checksum");
            Assert.Null(DocsChecksum.ReadStored(p));
        }
        finally { CleanUp(dir); }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dw-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanUp(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
