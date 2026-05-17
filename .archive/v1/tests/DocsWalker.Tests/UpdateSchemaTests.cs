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
        // YAML отличается от оригинала маркером (в виде trailing YAML-комментария,
        // безопасного для любой структуры Схемы) — dry-run не должен его записать.
        const string marker = "##DRY-RUN-CHECK-MARKER##";
        var modifiedYaml = originalYaml + "\n# " + marker + "\n";

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

    [Fact]
    public void UpdateSchema_ForceWithGraphBreakingSchema_AppliedDespiteBrokenGraph()
    {
        // force=true — admin-knob для миграций: пропускает Validator (но не meta-schema).
        // Та же ломающая схема, что и в UpdateSchema_SchemaBreaksGraph_*, под force должна
        // успешно записаться. Граф после этого невалиден, но это ответственность вызывающего —
        // следующая операция чинит граф (см. stg-0011/migrate-classifiers-data).
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromStoragePath(env.DocsRoot));

        const string graphBreakingSchema =
            "description: Minimal schema for test\n" +
            "trees:\n" +
            "  - name: path\n" +
            "types:\n" +
            "  - name: root\n" +
            "    title_source: filename\n" +
            "    text_required: false\n";

        var result = api.UpdateSchema(graphBreakingSchema, dryRun: false, force: true);

        Assert.True(result.Applied);
        Assert.Equal(1, result.TypesCount);
        Assert.Equal(1, result.TreesCount);

        // Файл перезаписан: новый текст на диске.
        Assert.Equal(graphBreakingSchema, File.ReadAllText(env.SchemaPath));
    }

    [Fact]
    public void UpdateSchema_ForceDoesNotBypassMetaSchema()
    {
        // force обходит только Validator на графе. Meta-schema проверяется всегда:
        // YAML без обязательного поля description должен упасть даже с force=true.
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromStoragePath(env.DocsRoot));
        var originalYaml = File.ReadAllText(env.SchemaPath);

        const string missingDescription =
            "trees:\n" +
            "  - name: path\n" +
            "types: []\n";

        var ex = Assert.Throws<WriteApiException>(
            () => api.UpdateSchema(missingDescription, dryRun: false, force: true));
        Assert.False(string.IsNullOrEmpty(ex.Code));

        // Файл не изменился — meta-schema check сработал до записи.
        Assert.Equal(originalYaml, File.ReadAllText(env.SchemaPath));
    }

    [Fact]
    public void UpdateSchema_ForceWithDryRun_NotAppliedAndFileUnchanged()
    {
        // force + dryRun: валидатор пропущен, но FS-фаза тоже пропущена — файл не меняется.
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromStoragePath(env.DocsRoot));
        var originalYaml = File.ReadAllText(env.SchemaPath);

        const string graphBreakingSchema =
            "description: Minimal schema for test\n" +
            "trees:\n" +
            "  - name: path\n" +
            "types:\n" +
            "  - name: root\n" +
            "    title_source: filename\n" +
            "    text_required: false\n";

        var result = api.UpdateSchema(graphBreakingSchema, dryRun: true, force: true);

        Assert.False(result.Applied);
        Assert.Equal(originalYaml, File.ReadAllText(env.SchemaPath));
    }
}
