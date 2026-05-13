using DocsWalker.Kernel;

namespace DocsWalker.Tests;

/// <summary>
/// Чтение и валидация kernel-config'а (<c>--config=&lt;path&gt;</c> для
/// <c>DocsWalker.Kernel.exe</c>) — stg-0010 step-03.
/// </summary>
public class KernelConfigTests
{
    [Fact]
    public void Read_ValidConfig_ParsesAllFields()
    {
        using var f = new TempFile();
        var docsPath = TestPaths.DocsRoot.Replace("\\", "\\\\");
        f.WriteAllText(
            "{\n" +
            "  \"bind\": \"127.0.0.1\",\n" +
            "  \"port\": 12345,\n" +
            $"  \"graphs\": {{ \"main\": \"{docsPath}\" }},\n" +
            "  \"graph_idle_timeout\": \"5m\"\n" +
            "}");

        var cfg = KernelConfig.Read(f.Path);

        Assert.Equal("127.0.0.1", cfg.Bind);
        Assert.Equal(12345, cfg.Port);
        Assert.Single(cfg.Graphs);
        Assert.Equal("main", cfg.Graphs[0].Name);
        Assert.Equal(Path.GetFullPath(TestPaths.DocsRoot), cfg.Graphs[0].StoragePath);
        Assert.Equal(TimeSpan.FromMinutes(5), cfg.GraphIdleTimeout);
    }

    [Fact]
    public void Read_NoGraphs_ThrowsKernelConfigException()
    {
        using var f = new TempFile();
        f.WriteAllText("{ \"bind\": \"127.0.0.1\", \"port\": 0, \"graphs\": {} }");

        var ex = Assert.Throws<KernelConfigException>(() => KernelConfig.Read(f.Path));
        Assert.Contains("graphs", ex.Message);
    }

    [Fact]
    public void Read_InvalidPort_ThrowsKernelConfigException()
    {
        using var f = new TempFile();
        var docsPath = TestPaths.DocsRoot.Replace("\\", "\\\\");
        f.WriteAllText(
            "{\n" +
            "  \"port\": 99999,\n" +
            $"  \"graphs\": {{ \"main\": \"{docsPath}\" }}\n" +
            "}");

        var ex = Assert.Throws<KernelConfigException>(() => KernelConfig.Read(f.Path));
        Assert.Contains("port", ex.Message);
    }

    [Fact]
    public void Read_InvalidDuration_ThrowsKernelConfigException()
    {
        using var f = new TempFile();
        var docsPath = TestPaths.DocsRoot.Replace("\\", "\\\\");
        f.WriteAllText(
            "{\n" +
            $"  \"graphs\": {{ \"main\": \"{docsPath}\" }},\n" +
            "  \"graph_idle_timeout\": \"forever\"\n" +
            "}");

        var ex = Assert.Throws<KernelConfigException>(() => KernelConfig.Read(f.Path));
        Assert.Contains("graph_idle_timeout", ex.Message);
    }

    [Fact]
    public void Read_ReservedApiGraphName_ThrowsKernelConfigException()
    {
        using var f = new TempFile();
        var docsPath = TestPaths.DocsRoot.Replace("\\", "\\\\");
        f.WriteAllText(
            "{\n" +
            $"  \"graphs\": {{ \"api\": \"{docsPath}\" }}\n" +
            "}");

        var ex = Assert.Throws<KernelConfigException>(() => KernelConfig.Read(f.Path));
        Assert.Contains("api", ex.Message);
        Assert.Contains("зарезервирован", ex.Message);
    }

    [Fact]
    public void Read_FileNotFound_ThrowsKernelConfigException()
    {
        var path = Path.Combine(Path.GetTempPath(), "no-such-kernel-config-" + Guid.NewGuid().ToString("N") + ".json");

        var ex = Assert.Throws<KernelConfigException>(() => KernelConfig.Read(path));
        Assert.Contains("не найден", ex.Message);
    }

    [Fact]
    public void Read_DefaultBindAndPortWhenOmitted()
    {
        using var f = new TempFile();
        var docsPath = TestPaths.DocsRoot.Replace("\\", "\\\\");
        f.WriteAllText("{ \"graphs\": { \"main\": \"" + docsPath + "\" } }");

        var cfg = KernelConfig.Read(f.Path);

        Assert.Equal("127.0.0.1", cfg.Bind);
        Assert.Equal(0, cfg.Port);
        Assert.Equal(TimeSpan.FromMinutes(10), cfg.GraphIdleTimeout);
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; }
        public TempFile()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "docswalker-kernel-config-test-" + Guid.NewGuid().ToString("N") + ".json");
        }
        public void WriteAllText(string content) => File.WriteAllText(Path, content);
        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); } catch { }
        }
    }
}
