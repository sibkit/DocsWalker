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

    [Fact]
    public void Validate_RefTargetNotFound_HasHint()
    {
        var (v, g) = Setup();
        var section = g.GetById(45)!;
        var bad = WithExplicitRefs(section, new[] { new Ref(section.Id, "ref", 99999, RefOrigin.Explicit) });
        var mutated = RebuildWithReplacement(g, bad);
        var result = v.Validate(mutated);
        var err = result.Errors.First(e => e.Code == "ref_target_not_found");
        Assert.False(string.IsNullOrEmpty(err.Hint));
    }

    [Fact]
    public void Validate_DuplicateChildInBlock_Reports_DuplicateChildInBlock()
    {
        var (v, g) = Setup();
        var section = g.GetById(7)!; // section с блоком definitions
        var newBlocks = section.Blocks!
            .Select<NodeBlock, NodeBlock>(b => b is ChildrenBlock cb && cb.Name == "definitions"
                ? new ChildrenBlock(cb.Name, cb.ChildIds.Concat(new[] { cb.ChildIds[0] }).ToList())
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
        Assert.Contains(result.Errors, e => e.Code == "duplicate_child_in_block" && e.NodeId == section.Id);
    }

    [Fact]
    public void Validate_ParentBlockMismatch_Reports_ParentBlockInconsistent()
    {
        var (v, g) = Setup();
        // Берём definition (id=8), у него parent_block_name="definitions"; ломаем имя
        // — родитель не объявляет блока 'wrong_block_name'.
        var node = g.GetById(8)!;
        var bad = new Node
        {
            Id = node.Id,
            TypeName = node.TypeName,
            Title = node.Title,
            ParentId = node.ParentId,
            ParentBlockName = "wrong_block_name",
            SourceFile = node.SourceFile,
            Blocks = node.Blocks,
            Fields = node.Fields,
            InlineValue = node.InlineValue,
            ExplicitOutRefs = node.ExplicitOutRefs,
        };
        var mutated = RebuildWithReplacement(g, bad);
        var result = v.Validate(mutated);
        Assert.Contains(result.Errors, e => e.Code == "parent_block_inconsistent" && e.NodeId == node.Id);
    }

    [Fact]
    public void Validate_SequenceBelowMaxId_Reports_SequenceUnderflow()
    {
        var (v, g) = Setup();
        var maxId = g.ById.Keys.Max();
        var result = v.Validate(g, sequence: maxId - 1);
        Assert.Contains(result.Errors, e => e.Code == "sequence_underflow");
        Assert.False(string.IsNullOrEmpty(result.Errors.First(e => e.Code == "sequence_underflow").Hint));
    }

    [Fact]
    public void Validate_SequenceAtOrAboveMaxId_Passes_SequenceCheck()
    {
        var (v, g) = Setup();
        var maxId = g.ById.Keys.Max();
        var result = v.Validate(g, sequence: maxId);
        Assert.DoesNotContain(result.Errors, e => e.Code == "sequence_underflow");
    }

    private static Node WithExplicitRefs(Node original, IReadOnlyList<Ref> refs) => new()
    {
        Id = original.Id,
        TypeName = original.TypeName,
        Title = original.Title,
        ParentId = original.ParentId,
        ParentBlockName = original.ParentBlockName,
        SourceFile = original.SourceFile,
        Blocks = original.Blocks,
        Fields = original.Fields,
        InlineValue = original.InlineValue,
        ExplicitOutRefs = refs,
    };

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
