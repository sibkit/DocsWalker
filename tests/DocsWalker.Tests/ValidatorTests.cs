using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Validation;

namespace DocsWalker.Tests;

public class ValidatorTests
{
    private static (Validator V, Graph G) Setup()
    {
        var meta = SchemaLoader.LoadMetaSchema(TestPaths.MetaSchemaPath);
        var schema = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var docs = DocumentLoader.Load(TestPaths.DocsRoot, schema);
        return (new Validator(meta, schema), docs.Graph);
    }

    [Fact]
    public void Validate_RealDocs_ReturnsNoErrors()
    {
        var (v, g) = Setup();
        var result = v.Validate(g);
        if (!result.IsValid)
        {
            // Подробный отчёт в Assert message — иначе сложно понять, что именно валидатор счёл невалидным.
            var lines = string.Join(
                Environment.NewLine,
                result.Errors.Select(e =>
                    $"[{e.Code}] file={e.FilePath ?? "-"} id={e.NodeId?.ToString() ?? "-"}: {e.Message}"));
            Assert.Fail($"Реальные docs/ не прошли валидатор:{Environment.NewLine}{lines}");
        }
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_DuplicateExplicitRefTarget_NotFound_Reports_RefTargetNotFound()
    {
        var (v, g) = Setup();
        var section = g.GetById(45)!;
        // Подменяем явные out_refs: на несуществующий id и на несуществующий тип.
        var bad = new Node
        {
            Id = section.Id,
            TypeName = section.TypeName,
            Title = section.Title,
            ParentId = section.ParentId,
            ParentBlockName = section.ParentBlockName,
            SourceFile = section.SourceFile,
            Blocks = section.Blocks,
            Fields = section.Fields,
            InlineValue = section.InlineValue,
            ExplicitOutRefs = new[]
            {
                new Ref(section.Id, "ref", 99999, RefOrigin.Explicit),
                new Ref(section.Id, "nonexistent_type", 1, RefOrigin.Explicit),
            },
        };
        var mutated = RebuildWithReplacement(g, bad);
        var result = v.Validate(mutated);
        Assert.Contains(result.Errors, e => e.Code == "ref_target_not_found" && e.NodeId == section.Id);
        Assert.Contains(result.Errors, e => e.Code == "unknown_ref_type" && e.NodeId == section.Id);
    }

    [Fact]
    public void Validate_TitleWithNewline_Reports_MultilineValue()
    {
        var (v, g) = Setup();
        var node = g.GetById(8)!; // definition «узел»
        var bad = new Node
        {
            Id = node.Id,
            TypeName = node.TypeName,
            Title = "плохой\nтайтл",
            ParentId = node.ParentId,
            ParentBlockName = node.ParentBlockName,
            SourceFile = node.SourceFile,
            InlineValue = node.InlineValue,
            Fields = node.Fields,
            Blocks = node.Blocks,
            ExplicitOutRefs = node.ExplicitOutRefs,
        };
        var mutated = RebuildWithReplacement(g, bad);
        var result = v.Validate(mutated);
        Assert.Contains(result.Errors, e => e.Code == "multiline_value" && e.NodeId == node.Id);
    }

    [Fact]
    public void Validate_TextBlockItemWithTab_Reports_MultilineValue()
    {
        var (v, g) = Setup();
        var section = g.GetById(2)!; // section «Назначение» с блоком statements
        var newBlocks = section.Blocks!
            .Select<NodeBlock, NodeBlock>(b => b is TextBlock tb && tb.Name == "statements"
                ? new TextBlock(tb.Name, new[] { "строка с\tтабом" })
                : b)
            .ToList();
        var bad = new Node
        {
            Id = section.Id,
            TypeName = section.TypeName,
            Title = section.Title,
            ParentId = section.ParentId,
            ParentBlockName = section.ParentBlockName,
            SourceFile = section.SourceFile,
            Blocks = newBlocks,
            Fields = section.Fields,
            InlineValue = section.InlineValue,
            ExplicitOutRefs = section.ExplicitOutRefs,
        };
        var mutated = RebuildWithReplacement(g, bad);
        var result = v.Validate(mutated);
        Assert.Contains(result.Errors, e => e.Code == "multiline_value" && e.NodeId == section.Id);
    }

    [Fact]
    public void Validate_RealDocs_AllSectionTitles_RoundTripThroughTitleFormat()
    {
        var (v, g) = Setup();
        var result = v.Validate(g);
        // Реальные docs не должны порождать invalid_title_format.
        Assert.DoesNotContain(result.Errors, e => e.Code == "invalid_title_format");
    }

    /// <summary>
    /// Пересобирает граф из исходного, заменяя один узел. Сохраняет порядок
    /// добавления (важно для path-связей).
    /// </summary>
    private static Graph RebuildWithReplacement(Graph original, Node replacement)
    {
        var rebuilt = new Graph();
        // Detach replacement's source from any document-uniqueness check by
        // adding documents first, then non-documents. Graph.Add отлавливает
        // дубль title документа — обходим с тем же ParentId/title.
        var nodes = original.ById.Values
            .Select(n => n.Id == replacement.Id ? replacement : n)
            .OrderBy(n => n.ParentId is null ? 0 : 1) // documents first
            .ThenBy(n => n.Id)
            .ToList();
        foreach (var n in nodes) rebuilt.Add(n);
        return rebuilt;
    }
}
