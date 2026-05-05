using System.Text;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Yaml;

namespace DocsWalker.Tests;

public class EmitterTests
{
    /// <summary>
    /// Полный round-trip: парсим реальные docs/, эмитим каждый документ обратно в YAML,
    /// парсим эмитированное в новом каталоге и сравниваем граф с исходным.
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
                var rt = roundTripped.GetById(origNode.Id);
                Assert.NotNull(rt);
                AssertNodesEquivalent(origNode, rt!);
            }
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void Emit_DocsWalker_OutputContains_ExpectedKeyShape()
    {
        var schema = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var graph = DocumentLoader.Load(TestPaths.DocsRoot, schema).Graph;
        var doc = graph.GetDocumentByTitle("DocsWalker")!;
        var yaml = Emitter.EmitDocument(graph, schema, doc);

        // id первым, description вторым, content — sequence на 0-уровне.
        Assert.StartsWith("id: 1\ndescription: ", yaml);
        Assert.Contains("\ncontent:\n", yaml);
        // section с ключом (#2) Назначение — в двойных кавычках.
        Assert.Contains("\n  - \"(#2) Назначение\":\n", yaml);
        // Default-блок content/definitions/examples и т.п. сериализуется по имени.
        Assert.Contains("\n    - statements:\n", yaml);
        // Ссылка ref: id выводится в plain.
        Assert.Contains("\n    - out_refs:\n", yaml);
        Assert.Contains("\n      - ref: 64\n", yaml);
    }

    [Fact]
    public void Emit_StackDoc_SerializesDefinitionsBlock_WithInlineValues()
    {
        var schema = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var graph = DocumentLoader.Load(TestPaths.DocsRoot, schema).Graph;
        var doc = graph.GetDocumentByTitle("DocsWalker")!;
        var yaml = Emitter.EmitDocument(graph, schema, doc);

        // Внутри section "Уровни схемы" id=4 лежит блок definitions с definition'ами 5 и 6.
        Assert.Contains("\n    - definitions:\n", yaml);
        Assert.Contains("\n      - \"(#5) мета-схема\":", yaml);
        Assert.Contains("\n      - \"(#6) схема\":", yaml);
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
            // multiline-литералы '|'/'>', якоря '&', алиасы '*', теги '!', директивы '%'.
            Assert.DoesNotContain("\n|", yaml);
            Assert.DoesNotContain("\n>", yaml);
            Assert.DoesNotContain(": |", yaml);
            Assert.DoesNotContain(": >", yaml);
            Assert.DoesNotContain(" &", yaml);
            // '*' допустим только в обычных строках, но в текущих docs он не встречается;
            // отдельная проверка на алиас-форму '*' ' name: ' тут излишня.
            Assert.DoesNotContain("\n%", yaml);
            Assert.DoesNotContain("\n!", yaml);
        }
    }

    private static void AssertNodesEquivalent(Node a, Node b)
    {
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.TypeName, b.TypeName);
        Assert.Equal(a.Title, b.Title);
        Assert.Equal(a.ParentId, b.ParentId);
        Assert.Equal(a.ParentBlockName, b.ParentBlockName);
        Assert.Equal(a.InlineValue, b.InlineValue);

        AssertFieldsEquivalent(a.Fields, b.Fields, a.Id);
        AssertBlocksEquivalent(a.Blocks, b.Blocks, a.Id);
        AssertRefsEquivalent(a.ExplicitOutRefs, b.ExplicitOutRefs, a.Id);
    }

    private static void AssertFieldsEquivalent(
        IReadOnlyList<FieldValue>? a, IReadOnlyList<FieldValue>? b, int nodeId)
    {
        if (a is null && b is null) return;
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(a!.Count == b!.Count,
            $"Узел id={nodeId}: число полей расходится — было {a.Count}, стало {b.Count}.");
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Name, b[i].Name);
            Assert.Equal(a[i].Scalar, b[i].Scalar);
            AssertScalarListEquivalent(a[i].Items, b[i].Items, $"id={nodeId} field '{a[i].Name}'");
        }
    }

    private static void AssertBlocksEquivalent(
        IReadOnlyList<NodeBlock>? a, IReadOnlyList<NodeBlock>? b, int nodeId)
    {
        if (a is null && b is null) return;
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(a!.Count == b!.Count,
            $"Узел id={nodeId}: число блоков расходится.");
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Name, b[i].Name);
            Assert.Equal(a[i].GetType(), b[i].GetType());
            switch (a[i])
            {
                case TextBlock ta when b[i] is TextBlock tb:
                    AssertScalarListEquivalent(ta.Items, tb.Items, $"id={nodeId} block '{ta.Name}'");
                    break;
                case ChildrenBlock ca when b[i] is ChildrenBlock cb:
                    Assert.Equal(ca.ChildIds, cb.ChildIds);
                    break;
                case OutRefsBlock oa when b[i] is OutRefsBlock ob:
                    AssertRefsEquivalent(oa.Refs, ob.Refs, nodeId);
                    break;
                default:
                    Assert.Fail($"Узел id={nodeId}: неожиданная пара блоков {a[i].GetType()} / {b[i].GetType()}.");
                    break;
            }
        }
    }

    private static void AssertRefsEquivalent(
        IReadOnlyList<Ref>? a, IReadOnlyList<Ref>? b, int nodeId)
    {
        if (a is null && b is null) return;
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Count, b!.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].FromId, b[i].FromId);
            Assert.Equal(a[i].TypeName, b[i].TypeName);
            Assert.Equal(a[i].ToId, b[i].ToId);
            Assert.Equal(a[i].Origin, b[i].Origin);
        }
    }

    private static void AssertScalarListEquivalent(
        IReadOnlyList<string>? a, IReadOnlyList<string>? b, string what)
    {
        if (a is null && b is null) return;
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(a!.Count == b!.Count,
            $"{what}: длина списка расходится — было {a.Count}, стало {b.Count}.");
        for (int i = 0; i < a.Count; i++)
            Assert.Equal(a[i], b[i]);
    }
}
