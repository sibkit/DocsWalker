namespace DocsWalker.Tests;

/// <summary>
/// Поиск корня репозитория (где лежит DocsWalker.slnx) от каталога сборки тестов
/// вверх по дереву. Тестам нужны реальные docs/.
/// </summary>
internal static class TestPaths
{
    private static readonly Lazy<string> _repoRoot = new(FindRepoRoot);

    public static string RepoRoot => _repoRoot.Value;

    public static string DocsRoot => Path.Combine(RepoRoot, "docs");

    public static string MetaSchemaPath =>
        Path.Combine(DocsRoot, ".docswalker", "meta-schema.yml");

    public static string SchemaPath => Path.Combine(DocsRoot, "Схема.yml");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DocsWalker.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Не удалось найти корень репозитория (DocsWalker.slnx) от '{AppContext.BaseDirectory}'.");
    }
}
