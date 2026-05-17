using System.Text;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Yaml;

namespace DocsWalker.Tests;

public class EmitterTests
{
    /// <summary>
    /// Полный round-trip: парсим реальные docs/, эмитим каждый документ обратно в YAML,
    /// парсим эмитированное в новом каталоге и сравниваем граф с исходным по 5 полям
    /// refs-модели (id, type, title, text, out_refs) для каждого узла.
    /// </summary>
    [Fact]
    public void Emit_RealDocs_RoundTrip_PreservesGraph()
    {
        var schema = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var orig = DocumentLoader.Load(TestPaths.DocsRoot, schema).Graph;

        var tmpDir = Path.Combine(
            Path.GetTempPath(), "docswalker-emitter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            foreach (var doc in orig.Documents)
            {
                var yaml = Emitter.EmitDocument(orig, schema, doc);
                var path = Path.Combine(tmpDir, doc.Title + ".yml");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            var roundTripped = DocumentLoader.Load(tmpDir, schema).Graph;

            Assert.Equal(orig.NodeCount, roundTripped.NodeCount);
            Assert.Equal(orig.Documents.Count, roundTripped.Documents.Count);

            foreach (var origNode in orig.ById.Values)
            {
                if (origNode.Id == Node.RootId) continue;
                var rt = roundTripped.GetById(origNode.Id);
                Assert.NotNull(rt);
                AssertSameNode(origNode, rt!);
            }
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void Emit_ProducesYamlWithoutForbiddenConstructs()
    {
        var schema = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var graph = DocumentLoader.Load(TestPaths.DocsRoot, schema).Graph;

        foreach (var doc in graph.Documents)
        {
            var yaml = Emitter.EmitDocument(graph, schema, doc);
            // Запрещённые YAML-конструкции (см. docs/Стек.yml/«YAML-парсер»):
            // multiline-литералы '|'/'>', якоря '&', теги '!', директивы '%'.
            // Проверяем, что они не открыты как top-level YAML-индикаторы — в кавычках
            // эти символы как часть значения допустимы и не должны ломать тест.
            Assert.DoesNotContain(": |\n", yaml);
            Assert.DoesNotContain(": >\n", yaml);
            Assert.DoesNotContain(": &", yaml);
            Assert.DoesNotContain("\n%", yaml);
            Assert.DoesNotContain("\n!", yaml);
        }
    }

    private static void AssertSameNode(Node a, Node b)
    {
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.TypeName, b.TypeName);
        Assert.Equal(a.Title, b.Title);
        Assert.Equal(a.Text, b.Text);

        Assert.Equal(a.OutRefs.Count, b.OutRefs.Count);
        foreach (var (name, ids) in a.OutRefs)
        {
            Assert.True(b.OutRefs.TryGetValue(name, out var bIds), $"id={a.Id}: связь '{name}' пропала.");
            Assert.Equal(ids, bIds!);
        }
    }
}
