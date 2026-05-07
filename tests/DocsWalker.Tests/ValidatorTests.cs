using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Validation;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Tests;

public class ValidatorTests
{
    private static (Validator V, GraphModel G, MetaSchemaDocument Meta, SchemaDocument Schema) Setup()
    {
        var meta = SchemaLoader.LoadMetaSchema(TestPaths.MetaSchemaPath);
        var schema = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var docs = DocumentLoader.Load(TestPaths.DocsRoot, schema);
        return (new Validator(meta, schema), docs.Graph, meta, schema);
    }

    [Fact]
    public void Validate_RealDocs_ReturnsNoErrors()
    {
        var (v, g, _, _) = Setup();
        var result = v.Validate(g);
        if (!result.IsValid)
        {
            var lines = string.Join(
                Environment.NewLine,
                result.Errors.Select(e =>
                    $"[{e.Code}] file={e.FilePath ?? "-"} id={e.NodeId?.ToString() ?? "-"}: {e.Message}"));
            Assert.Fail($"Реальные docs/ не прошли валидатор:{Environment.NewLine}{lines}");
        }
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_TextWithNewline_IsAllowed()
    {
        // text с \n больше не запрещён (правило #139): \n допустим, лимит — длина 1000 chars.
        var (v, g, _, schema) = Setup();
        var node = g.GetById(127)!; // statement «Ядро на C#»
        var rebuilt = RebuildWithReplacement(g, schema, ReplaceText(node, "первая строка\nвторая строка"));
        var result = v.Validate(rebuilt);
        Assert.DoesNotContain(result.Errors, e =>
            e.NodeId == node.Id && (e.Code == "invalid_text" || e.Code == "multiline_value"));
    }

    [Fact]
    public void Validate_TextWithTab_Reports_InvalidText()
    {
        var (v, g, _, schema) = Setup();
        var node = g.GetById(127)!;
        var rebuilt = RebuildWithReplacement(g, schema, ReplaceText(node, "строка с\tтабом"));
        var result = v.Validate(rebuilt);
        Assert.Contains(result.Errors, e => e.Code == "invalid_text" && e.NodeId == node.Id);
    }

    [Fact]
    public void Validate_TitleWithNewline_Reports_InvalidText()
    {
        var (v, g, _, schema) = Setup();
        var node = g.GetById(127)!;
        var rebuilt = RebuildWithReplacement(g, schema, ReplaceTitle(node, "плохой\nтайтл"));
        var result = v.Validate(rebuilt);
        Assert.Contains(result.Errors, e => e.Code == "invalid_text" && e.NodeId == node.Id);
    }

    [Fact]
    public void Validate_TextOver1000Chars_Reports_TextTooLong()
    {
        var (v, g, _, schema) = Setup();
        var node = g.GetById(127)!;
        var longText = new string('a', 1001);
        var rebuilt = RebuildWithReplacement(g, schema, ReplaceText(node, longText));
        var result = v.Validate(rebuilt);
        Assert.Contains(result.Errors, e => e.Code == "text_too_long" && e.NodeId == node.Id);
    }

    [Fact]
    public void Validate_TextAt1000Chars_Passes()
    {
        var (v, g, _, schema) = Setup();
        var node = g.GetById(127)!;
        var exactlyAtLimit = new string('a', 1000);
        var rebuilt = RebuildWithReplacement(g, schema, ReplaceText(node, exactlyAtLimit));
        var result = v.Validate(rebuilt);
        Assert.DoesNotContain(result.Errors, e => e.Code == "text_too_long" && e.NodeId == node.Id);
    }

    [Fact]
    public void Validate_SequenceBelowMaxId_Reports_SequenceUnderflow()
    {
        var (v, g, _, _) = Setup();
        var maxId = g.ById.Keys.Max();
        var result = v.Validate(g, sequence: maxId - 1);
        Assert.Contains(result.Errors, e => e.Code == "sequence_underflow");
    }

    [Fact]
    public void Validate_SequenceAtMaxId_Passes_SequenceCheck()
    {
        var (v, g, _, _) = Setup();
        var maxId = g.ById.Keys.Max();
        var result = v.Validate(g, sequence: maxId);
        Assert.DoesNotContain(result.Errors, e => e.Code == "sequence_underflow");
    }

    private static Node ReplaceText(Node original, string newText) => new()
    {
        Id = original.Id,
        TypeName = original.TypeName,
        Title = original.Title,
        Text = newText,
        OutRefs = original.OutRefs,
        SourceFile = original.SourceFile,
    };

    private static Node ReplaceTitle(Node original, string newTitle) => new()
    {
        Id = original.Id,
        TypeName = original.TypeName,
        Title = newTitle,
        Text = original.Text,
        OutRefs = original.OutRefs,
        SourceFile = original.SourceFile,
    };

    /// <summary>
    /// Пересобирает граф из исходного, заменяя один узел. Документы добавляются первыми,
    /// чтобы при обходе path-родители уже присутствовали (см. <see cref="GraphModel.Add"/>).
    /// </summary>
    private static GraphModel RebuildWithReplacement(GraphModel original, SchemaDocument schema, Node replacement)
    {
        var rebuilt = new GraphModel();
        rebuilt.AttachSchema(schema);
        var documentTypes = new HashSet<string>(StringComparer.Ordinal) { "document", "folder" };

        // Сначала структурные (root присутствует виртуально, его не добавляем).
        foreach (var n in original.ById.Values
            .Where(n => n.Id != Node.RootId && documentTypes.Contains(n.TypeName))
            .OrderBy(n => n.Id))
        {
            var node = n.Id == replacement.Id ? replacement : n;
            rebuilt.Add(node);
        }
        foreach (var n in original.ById.Values
            .Where(n => n.Id != Node.RootId && !documentTypes.Contains(n.TypeName))
            .OrderBy(n => n.Id))
        {
            var node = n.Id == replacement.Id ? replacement : n;
            rebuilt.Add(node);
        }
        return rebuilt;
    }
}
