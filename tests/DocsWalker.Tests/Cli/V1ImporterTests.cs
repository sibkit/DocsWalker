using System.Text;
using DocsWalker.Cli.Migration;
using DocsWalker.Core.Api;

namespace DocsWalker.Tests.Cli;

public sealed class V1ImporterTests
{
    [Fact]
    public void ImportFile_RootAndChildren_BuildsCreateOps()
    {
        var folder = NewTempFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, "Sample.yml"),
                """
                id: 64
                text: "Тех-стек DocsWalker."
                sections:
                  - "(#65) Версия .NET":
                    - text: "Все компоненты — .NET 10."
                    - rules:
                      - "(#87) Целевой LTS":
                        - text: "LTS-версия."
                        - subject: [425]
                """, Encoding.UTF8);
            var importer = new V1Importer(TextWriter.Null);
            importer.ImportFolder(folder);

            Assert.Equal(1, importer.FilesProcessed);
            Assert.Equal(3, importer.NodesCreated);
            var paths = importer.CollectedOps.Select(o => o.Path).ToList();
            Assert.Contains("Sample", paths);
            Assert.Contains("Sample/Версия-.NET", paths);
            Assert.Contains("Sample/Версия-.NET/Целевой-LTS", paths);
            // V1 ref subject:[425] был учтён как «пропущенная ссылка».
            Assert.True(importer.RefsSkipped >= 1);

            var leaf = importer.CollectedOps.Single(o => o.Path.EndsWith("/Целевой-LTS"));
            Assert.Equal("LTS-версия.", leaf.Set.Content);
            Assert.Equal("rule", leaf.Set.MapBindings!["v1_kind"]);
            Assert.Equal("87", leaf.Set.MapBindings["v1_id"]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ImportFile_DuplicateSiblingTitles_AppendV1IdSuffix()
    {
        var folder = NewTempFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, "Dup.yml"),
                """
                id: 1
                text: "Root."
                sections:
                  - "(#2) Раздел":
                    - text: "A"
                  - "(#3) Раздел":
                    - text: "B"
                """, Encoding.UTF8);
            var importer = new V1Importer(TextWriter.Null);
            importer.ImportFolder(folder);

            var paths = importer.CollectedOps.Select(o => o.Path).ToList();
            Assert.Contains("Dup/Раздел", paths);
            // Дубль получает suffix _v1<id>.
            Assert.Contains("Dup/Раздел_v13", paths);
            Assert.True(importer.CollisionsResolved >= 1);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ImportFile_NonYamlContent_SkipsCleanly()
    {
        var folder = NewTempFolder();
        try
        {
            // Файл существует, но содержимое не mapping (просто строка) —
            // импортёр должен выдать warning и пропустить.
            File.WriteAllText(Path.Combine(folder, "Bad.yml"), "just a scalar\n");
            var stderr = new StringWriter();
            var importer = new V1Importer(stderr);
            importer.ImportFolder(folder);

            Assert.Equal(1, importer.FilesProcessed);
            Assert.Empty(importer.CollectedOps);
            Assert.Contains("корень не mapping", stderr.ToString());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string NewTempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "dw_v1import_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
