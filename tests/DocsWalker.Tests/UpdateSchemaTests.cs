using DocsWalker.Core.Api;
using DocsWalker.Core.Schema;

namespace DocsWalker.Tests;

/// <summary>
/// Поведение <see cref="WriteApi.UpdateSchema"/> — atomic-replacement Схемы проекта.
/// Тесты идут на изолированном клоне реального <c>docs/</c>
/// (<see cref="WriteTestEnvironment"/>); сначала проверяется серверная валидация
/// (meta-schema → graph-validator), потом — атомарная запись/откат файла.
/// </summary>
public class UpdateSchemaTests
{
    [Fact]
    public void UpdateSchema_SameYaml_AppliedTrueAndFileUnchanged()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromStoragePath(env.DocsRoot));

        var originalYaml = File.ReadAllText(env.SchemaPath);
        var result = api.UpdateSchema(originalYaml, dryRun: false);

        Assert.True(result.Applied);
        Assert.True(result.TypesCount > 0);
        Assert.True(result.TreesCount > 0);

        // Атомарная запись с тем же содержимым → файл побайтно совпадает с оригиналом.
        Assert.Equal(originalYaml, File.ReadAllText(env.SchemaPath));

        // Граф под новой Схемой грузится: types/trees совпадают с тем, что вернул result.
        var reloaded = SchemaLoader.LoadSchema(env.SchemaPath);
        Assert.Equal(result.TypesCount, reloaded.Types.Count);
        Assert.Equal(result.TreesCount, reloaded.Trees.Count);
    }

    [Fact]
    public void UpdateSchema_DryRun_NotAppliedAndFileUnchanged()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromStoragePath(env.DocsRoot));

        var originalYaml = File.ReadAllText(env.SchemaPath);
        // YAML отличается от оригинала маркером в description — dry-run не должен его записать.
        const string marker = "##DRY-RUN-CHECK-MARKER##";
        var modifiedYaml = originalYaml.Replace(
            "description:",
            "description: " + marker + " ",
            StringComparison.Ordinal);

        var result = api.UpdateSchema(modifiedYaml, dryRun: true);

        Assert.False(result.Applied);

        var afterText = File.ReadAllText(env.SchemaPath);
        Assert.Equal(originalYaml, afterText);
        Assert.DoesNotContain(marker, afterText);
    }

    [Fact]
    public void UpdateSchema_EmptyText_ThrowsInvalidYaml()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromStoragePath(env.DocsRoot));
        var originalYaml = File.ReadAllText(env.SchemaPath);

        var ex = Assert.Throws<WriteApiException>(() => api.UpdateSchema("   \n  \t", dryRun: false));
        Assert.Equal("invalid_yaml", ex.Code);

        Assert.Equal(originalYaml, File.ReadAllText(env.SchemaPath));
    }

    [Fact]
    public void UpdateSchema_BrokenYamlSyntax_ThrowsAndFileUnchanged()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromStoragePath(env.DocsRoot));
        var originalYaml = File.ReadAllText(env.SchemaPath);

        // Незакрытый flow-sequence — YAML-парсер должен бросить ошибку.
        const string brokenYaml = "description: broken\ntrees: [\ntypes: []\n";

        var ex = Assert.Throws<WriteApiException>(() => api.UpdateSchema(brokenYaml, dryRun: false));
        Assert.False(string.IsNullOrEmpty(ex.Code));

        Assert.Equal(originalYaml, File.ReadAllText(env.SchemaPath));
    }

    [Fact]
    public void UpdateSchema_MissingRequiredField_ThrowsAndFileUnchanged()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromStoragePath(env.DocsRoot));
        var originalYaml = File.ReadAllText(env.SchemaPath);

        // schema_root.description обязателен по meta-schema; YAML синтаксически валиден,
        // но SchemaLoader должен поймать отсутствие description.
        const string brokenSchema =
            "trees:\n" +
            "  - name: path\n" +
            "types: []\n";

        var ex = Assert.Throws<WriteApiException>(() => api.UpdateSchema(brokenSchema, dryRun: false));
        Assert.False(string.IsNullOrEmpty(ex.Code));

        Assert.Equal(originalYaml, File.ReadAllText(env.SchemaPath));
    }

    [Fact]
    public void UpdateSchema_SchemaBreaksGraph_ThrowsValidationAndFileUnchanged()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromStoragePath(env.DocsRoot));
        var originalYaml = File.ReadAllText(env.SchemaPath);

        // Schema формально валидна (meta-schema): есть description, trees, types.
        // Но types содержит только root — все существующие узлы folder/document/section/…
        // упадут в validator с unknown_type. WriteApi.UpdateSchema должен бросить
        // WriteValidationException и НЕ перезаписать файл.
        const string graphBreakingSchema =
            "description: Minimal schema for test\n" +
            "trees:\n" +
            "  - name: path\n" +
            "types:\n" +
            "  - name: root\n" +
            "    title_source: filename\n" +
            "    text_required: false\n";

        Assert.Throws<WriteValidationException>(() => api.UpdateSchema(graphBreakingSchema, dryRun: false));

        Assert.Equal(originalYaml, File.ReadAllText(env.SchemaPath));
    }

    [Fact]
    public void UpdateSchema_DryRunWithGraphBreakingSchema_StillThrows()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromStoragePath(env.DocsRoot));
        var originalYaml = File.ReadAllText(env.SchemaPath);

        // dry-run не пропускает валидацию: ломающая graph схема должна упасть и при dryRun=true.
        const string graphBreakingSchema =
            "description: Minimal schema for test\n" +
            "trees:\n" +
            "  - name: path\n" +
            "types:\n" +
            "  - name: root\n" +
            "    title_source: filename\n" +
            "    text_required: false\n";

        Assert.Throws<WriteValidationException>(() => api.UpdateSchema(graphBreakingSchema, dryRun: true));

        Assert.Equal(originalYaml, File.ReadAllText(env.SchemaPath));
    }
}
