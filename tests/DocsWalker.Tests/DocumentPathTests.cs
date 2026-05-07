using DocsWalker.Cli.Cli;

namespace DocsWalker.Tests;

/// <summary>
/// Проверяем, что сырые FS-пути ядра приходят к LLM как title документа без расширения
/// и FS-префикса (правило #277). Тесты крутятся вокруг forward-compat: формат может
/// слегка плыть, но «никаких .yml и docs/» — жёсткий инвариант.
/// </summary>
public class DocumentPathTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("DocsWalker.yml", "DocsWalker")]
    [InlineData("docs/DocsWalker.yml", "DocsWalker")]
    [InlineData("docs/Схема.yml", "Схема")]
    [InlineData("docs/sub/Подраздел.yml", "sub/Подраздел")]
    [InlineData("D:/repo/docs/DocsWalker.yml", "DocsWalker")]
    [InlineData(@"D:\repo\docs\DocsWalker.yml", "DocsWalker")]
    [InlineData("/home/user/repo/docs/Схема.yml", "Схема")]
    public void NormalizeForLlm_StripsExtensionAndDocsPrefix(string? input, string? expected)
    {
        Assert.Equal(expected, DocumentPath.NormalizeForLlm(input));
    }

    [Theory]
    [InlineData("docs/.docswalker/meta-schema.yml")]
    [InlineData("docs/.docswalker/folders.yml")]
    [InlineData("docs/.docswalker/sequence.txt")]
    [InlineData(@"D:\repo\docs\.docswalker\meta-schema.yml")]
    public void NormalizeForLlm_HidesInternalDocswalkerFiles(string input)
    {
        // Служебные файлы LLM не знает; путь должен стать null, чтобы поле в JSON выпало.
        Assert.Null(DocumentPath.NormalizeForLlm(input));
    }

    [Theory]
    [InlineData("docs/Doc.yaml", "Doc")]
    [InlineData("docs/Doc.YML", "Doc")]
    public void NormalizeForLlm_StripsAlternativeExtensions(string input, string expected)
    {
        Assert.Equal(expected, DocumentPath.NormalizeForLlm(input));
    }
}
