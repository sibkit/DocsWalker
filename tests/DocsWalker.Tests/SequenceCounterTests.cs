using DocsWalker.Core.Store;

namespace DocsWalker.Tests;

public class SequenceCounterTests
{
    [Fact]
    public void Read_OnMissingFile_InitializesWithZero()
    {
        var (path, dir) = NewSequenceFilePath();
        try
        {
            var counter = new SequenceCounter(path);
            Assert.Equal(0, counter.Read());
            Assert.True(File.Exists(path));
        }
        finally { CleanUp(dir); }
    }

    [Fact]
    public void Next_IncrementsByOne_AndPersists()
    {
        var (path, dir) = NewSequenceFilePath();
        try
        {
            var counter = new SequenceCounter(path);
            Assert.Equal(1, counter.Next());
            Assert.Equal(2, counter.Next());
            Assert.Equal(3, counter.Next());
            // Новый экземпляр читает то же значение.
            var counter2 = new SequenceCounter(path);
            Assert.Equal(3, counter2.Read());
        }
        finally { CleanUp(dir); }
    }

    [Fact]
    public void Read_OnInvalidContent_ThrowsSequenceCounterException()
    {
        var (path, dir) = NewSequenceFilePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "not a number");
            var counter = new SequenceCounter(path);
            var ex = Assert.Throws<SequenceCounterException>(() => counter.Read());
            Assert.Equal("sequence_invalid_value", ex.Code);
        }
        finally { CleanUp(dir); }
    }

    [Fact]
    public void Read_OnNegativeValue_ThrowsSequenceCounterException()
    {
        var (path, dir) = NewSequenceFilePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "-1");
            var counter = new SequenceCounter(path);
            var ex = Assert.Throws<SequenceCounterException>(() => counter.Read());
            Assert.Equal("sequence_invalid_value", ex.Code);
        }
        finally { CleanUp(dir); }
    }

    private static (string Path, string Dir) NewSequenceFilePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seqcounter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (Path.Combine(dir, "sub", "sequence.txt"), dir);
    }

    private static void CleanUp(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
