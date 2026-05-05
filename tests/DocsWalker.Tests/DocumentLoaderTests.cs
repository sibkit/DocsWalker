using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;

namespace DocsWalker.Tests;

public class DocumentLoaderTests
{
    private static (Graph Graph, IReadOnlyList<string> Files) Load()
    {
        var schema = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var result = DocumentLoader.Load(TestPaths.DocsRoot, schema);
        return (result.Graph, result.LoadedFiles);
    }

    [Fact]
    public void Load_RealDocs_Succeeds_AndRegistersExpectedDocuments()
    {
        var (graph, files) = Load();
        // Загружено три документа: DocsWalker, Правила оформления, Стек.
        // Схема.yml исключён, .docswalker/* исключён.
        Assert.Equal(3, graph.Documents.Count);
        Assert.Equal(3, files.Count);

        Assert.NotNull(graph.GetDocumentByTitle("DocsWalker"));
        Assert.NotNull(graph.GetDocumentByTitle("Правила оформления"));
        Assert.NotNull(graph.GetDocumentByTitle("Стек"));
    }

    [Fact]
    public void Load_RealDocs_TotalNodeCount_Matches_SequenceTxt()
    {
        // Инвариант: sequence.txt — последний выданный id; число узлов в графе
        // совпадает с этим значением (никакого id ещё не было удалено в реальных docs/).
        // Тест переживает добавление новых узлов в docs/ — не зависит от конкретного значения.
        var (graph, _) = Load();
        var sequenceText = File.ReadAllText(
            Path.Combine(TestPaths.DocsRoot, ".docswalker", "sequence.txt"));
        var sequence = int.Parse(sequenceText.Trim());
        Assert.Equal(sequence, graph.NodeCount);
    }

    [Fact]
    public void Load_RealDocs_DocumentIds_AreExpected()
    {
        var (graph, _) = Load();
        Assert.Equal(1, graph.GetDocumentByTitle("DocsWalker")!.Id);
        Assert.Equal(46, graph.GetDocumentByTitle("Правила оформления")!.Id);
        Assert.Equal(64, graph.GetDocumentByTitle("Стек")!.Id);
    }

    [Fact]
    public void Load_RealDocs_SectionParents_AreCorrect()
    {
        var (graph, _) = Load();
        // Секция "Назначение" в DocsWalker.yml должна иметь parent = DocsWalker.
        var docsWalker = graph.GetDocumentByTitle("DocsWalker")!;
        var sectionsOfDocsWalker = graph.GetChildren(docsWalker.Id);
        Assert.NotEmpty(sectionsOfDocsWalker);
        Assert.All(sectionsOfDocsWalker, n =>
        {
            Assert.Equal("section", n.TypeName);
            Assert.Equal(docsWalker.Id, n.ParentId);
            Assert.Equal("content", n.ParentBlockName);
        });
    }

    [Fact]
    public void Load_RealDocs_DefinitionsHaveInlineValue()
    {
        var (graph, _) = Load();
        var defs = graph.GetByType("definition");
        Assert.NotEmpty(defs);
        // У каждого definition есть InlineValue (значение определения).
        Assert.All(defs, d =>
        {
            Assert.NotNull(d.InlineValue);
            Assert.False(string.IsNullOrWhiteSpace(d.InlineValue));
            Assert.Equal("definitions", d.ParentBlockName);
        });
    }

    [Fact]
    public void Load_RealDocs_OutRefsBlock_PopulatesExplicitRefs()
    {
        var (graph, _) = Load();
        // Section "Стек реализации" (id=45) имеет out_refs: [{ref:64}, {ref:46}].
        var node = graph.GetById(45);
        Assert.NotNull(node);
        Assert.NotNull(node!.ExplicitOutRefs);
        Assert.Equal(2, node.ExplicitOutRefs!.Count);
        Assert.All(node.ExplicitOutRefs, r =>
        {
            Assert.Equal(45, r.FromId);
            Assert.Equal("ref", r.TypeName);
            Assert.Equal(RefOrigin.Explicit, r.Origin);
        });
        Assert.Contains(node.ExplicitOutRefs, r => r.ToId == 64);
        Assert.Contains(node.ExplicitOutRefs, r => r.ToId == 46);
    }

    [Fact]
    public void GetOutRefs_ForLeafChild_Includes_SystemPath()
    {
        var (graph, _) = Load();
        // У definition id=8 ("узел") родитель — section "Модель данных" id=7.
        var refs = graph.GetOutRefs(8);
        Assert.Contains(refs, r => r.Origin == RefOrigin.System &&
                                    r.TypeName == "path" &&
                                    r.ToId == 7);
    }

    [Fact]
    public void GetOutRefs_ForSection_Includes_DefaultRefsToChildren()
    {
        var (graph, _) = Load();
        // Section "Уровни схемы" id=4 содержит блок definitions с двумя definition'ами (id=5, id=6).
        var refs = graph.GetOutRefs(4);
        Assert.Contains(refs, r => r.Origin == RefOrigin.Default &&
                                    r.TypeName == "definitions" &&
                                    r.ToId == 5);
        Assert.Contains(refs, r => r.Origin == RefOrigin.Default &&
                                    r.TypeName == "definitions" &&
                                    r.ToId == 6);
    }

    [Fact]
    public void GetOutRefs_ForDocument_Includes_DefaultContentRefs()
    {
        var (graph, _) = Load();
        var doc = graph.GetDocumentByTitle("DocsWalker")!;
        var refs = graph.GetOutRefs(doc.Id);
        // Все sections документа — default-refs с типом "content".
        var defaultContent = refs
            .Where(r => r.Origin == RefOrigin.Default && r.TypeName == "content")
            .ToList();
        Assert.NotEmpty(defaultContent);
        var sections = graph.GetChildren(doc.Id);
        Assert.Equal(sections.Count, defaultContent.Count);
    }

    [Fact]
    public void GetInRefs_ForRefTarget_Includes_ExplicitInbound()
    {
        var (graph, _) = Load();
        // Section "Стек реализации" id=45 ссылается на документ Стек id=64.
        var refs = graph.GetInRefs(64);
        Assert.Contains(refs, r => r.FromId == 45 &&
                                    r.TypeName == "ref" &&
                                    r.Origin == RefOrigin.Explicit);
    }
}
