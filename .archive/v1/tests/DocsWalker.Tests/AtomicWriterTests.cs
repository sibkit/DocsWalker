using DocsWalker.Core.Store;

namespace DocsWalker.Tests;

public class AtomicWriterTests
{
    [Fact]
    public void WriteAll_OneFile_WritesContent()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "a.yml");
            AtomicWriter.WriteAll(new[] { new AtomicWriteTarget(path, "hello\n") });
            Assert.Equal("hello\n", File.ReadAllText(path));
        }
        finally { CleanUp(dir); }
    }

    [Fact]
    public void WriteAll_OverwritesExistingFile()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "a.yml");
            File.WriteAllText(path, "old");
            AtomicWriter.WriteAll(new[] { new AtomicWriteTarget(path, "new") });
            Assert.Equal("new", File.ReadAllText(path));
        }
        finally { CleanUp(dir); }
    }

    [Fact]
    public void WriteAll_ManyFiles_AllAppearAtomically()
    {
        var dir = NewTempDir();
        try
        {
            var t1 = new AtomicWriteTarget(Path.Combine(dir, "x.yml"), "x\n");
            var t2 = new AtomicWriteTarget(Path.Combine(dir, "y.yml"), "y\n");
            var t3 = new AtomicWriteTarget(Path.Combine(dir, "sub", "z.yml"), "z\n");
            AtomicWriter.WriteAll(new[] { t1, t2, t3 });
            Assert.Equal("x\n", File.ReadAllText(t1.AbsolutePath));
            Assert.Equal("y\n", File.ReadAllText(t2.AbsolutePath));
            Assert.Equal("z\n", File.ReadAllText(t3.AbsolutePath));
            // Не остаётся ни одного .tmp-файла.
            Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp-*", SearchOption.AllDirectories));
        }
        finally { CleanUp(dir); }
    }

    [Fact]
    public void WriteAll_EmptyList_DoesNothing()
    {
        // Не падает, не пишет временных файлов.
        AtomicWriter.WriteAll(Array.Empty<AtomicWriteTarget>());
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atomicwriter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanUp(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
