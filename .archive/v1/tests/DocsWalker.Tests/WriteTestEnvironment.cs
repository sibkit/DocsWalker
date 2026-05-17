namespace DocsWalker.Tests;

/// <summary>
/// Хелпер для write-тестов: создаёт изолированный временный корень, в котором лежит
/// полная копия реального docs/ (включая Схема.yml и .docswalker/). Тест работает с
/// этим клоном; после <see cref="Dispose"/> временный каталог удаляется.
/// </summary>
internal sealed class WriteTestEnvironment : IDisposable
{
    public string Root { get; }
    public string DocsRoot => Path.Combine(Root, "docs");
    public string SchemaPath => Path.Combine(DocsRoot, "Схема.yml");
    public string MetaSchemaPath => Path.Combine(DocsRoot, ".docswalker", "meta-schema.yml");
    public string SequencePath => Path.Combine(DocsRoot, ".docswalker", "sequence.txt");

    public WriteTestEnvironment()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "docswalker-writetest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        CopyDirectory(TestPaths.DocsRoot, DocsRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch { /* best-effort */ }
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
}
