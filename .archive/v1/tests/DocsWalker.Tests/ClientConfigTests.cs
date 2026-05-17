using DocsWalker.Cli.Cli.Kernel;

namespace DocsWalker.Tests;

/// <summary>
/// Чтение и валидация client-config'а (<c>.dw/client.json</c>) — stg-0010
/// step-03. Поиск вверх по родителям, error-codes
/// <c>client_config_not_found</c> и <c>client_config_invalid</c>.
/// </summary>
public class ClientConfigTests
{
    [Fact]
    public void Read_ValidConfig_ParsesAllFields()
    {
        using var f = new TempFile();
        f.WriteAllText(
            "{\n" +
            "  \"kernel\": { \"host\": \"127.0.0.1\", \"port\": 12345 },\n" +
            "  \"graph\": \"main\"\n" +
            "}");

        var cfg = ClientConfig.Read(f.Path);

        Assert.Equal("127.0.0.1", cfg.KernelHost);
        Assert.Equal(12345, cfg.KernelPort);
        Assert.Equal("main", cfg.Graph);
    }

    [Fact]
    public void Read_FileNotFound_ThrowsClientConfigNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), "no-such-client-config-" + Guid.NewGuid().ToString("N") + ".json");

        var ex = Assert.Throws<ClientConfigException>(() => ClientConfig.Read(path));
        Assert.Equal("client_config_not_found", ex.Code);
    }

    [Fact]
    public void Read_MalformedJson_ThrowsClientConfigInvalid()
    {
        using var f = new TempFile();
        f.WriteAllText("{ not json");

        var ex = Assert.Throws<ClientConfigException>(() => ClientConfig.Read(f.Path));
        Assert.Equal("client_config_invalid", ex.Code);
    }

    [Fact]
    public void Read_MissingKernel_ThrowsClientConfigInvalid()
    {
        using var f = new TempFile();
        f.WriteAllText("{ \"graph\": \"main\" }");

        var ex = Assert.Throws<ClientConfigException>(() => ClientConfig.Read(f.Path));
        Assert.Equal("client_config_invalid", ex.Code);
    }

    [Fact]
    public void Read_MissingGraph_ThrowsClientConfigInvalid()
    {
        using var f = new TempFile();
        f.WriteAllText("{ \"kernel\": { \"host\": \"127.0.0.1\", \"port\": 1234 } }");

        var ex = Assert.Throws<ClientConfigException>(() => ClientConfig.Read(f.Path));
        Assert.Equal("client_config_invalid", ex.Code);
    }

    [Fact]
    public void Read_InvalidPort_ThrowsClientConfigInvalid()
    {
        using var f = new TempFile();
        f.WriteAllText(
            "{\n" +
            "  \"kernel\": { \"host\": \"127.0.0.1\", \"port\": 99999 },\n" +
            "  \"graph\": \"main\"\n" +
            "}");

        var ex = Assert.Throws<ClientConfigException>(() => ClientConfig.Read(f.Path));
        Assert.Equal("client_config_invalid", ex.Code);
    }

    [Fact]
    public void Read_ReservedApiGraphName_ThrowsClientConfigInvalid()
    {
        using var f = new TempFile();
        f.WriteAllText(
            "{\n" +
            "  \"kernel\": { \"host\": \"127.0.0.1\", \"port\": 12345 },\n" +
            "  \"graph\": \"api\"\n" +
            "}");

        var ex = Assert.Throws<ClientConfigException>(() => ClientConfig.Read(f.Path));
        Assert.Equal("client_config_invalid", ex.Code);
        Assert.Contains("зарезервирован", ex.Message);
    }

    [Fact]
    public void FindConfigPath_LooksUpwardFromStartDir()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dw-client-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var deepDir = Path.Combine(temp, "a", "b", "c");
            Directory.CreateDirectory(deepDir);

            var dwDir = Path.Combine(temp, ClientConfig.ConfigDirName);
            Directory.CreateDirectory(dwDir);
            var configPath = Path.Combine(dwDir, ClientConfig.ConfigFileName);
            File.WriteAllText(configPath,
                "{\"kernel\":{\"host\":\"127.0.0.1\",\"port\":61532},\"graph\":\"main\"}");

            var found = ClientConfig.FindConfigPath(deepDir);
            Assert.NotNull(found);
            Assert.Equal(
                Path.GetFullPath(configPath).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(found!).TrimEnd(Path.DirectorySeparatorChar));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Resolve_ConfigNotFoundAnywhere_ThrowsClientConfigNotFound()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dw-client-no-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var ex = Assert.Throws<ClientConfigException>(() => ClientConfig.Resolve(temp));
            Assert.Equal("client_config_not_found", ex.Code);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; }
        public TempFile()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "docswalker-client-config-test-" + Guid.NewGuid().ToString("N") + ".json");
        }
        public void WriteAllText(string content) => File.WriteAllText(Path, content);
        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); } catch { }
        }
    }
}
