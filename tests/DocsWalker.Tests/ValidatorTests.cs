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
        // text с \n больше не запрещён (правило #139): \n допустим, лимит — длина 2000 chars.
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
    public void Validate_TextOver2000Chars_Reports_TextTooLong()
    {
        var (v, g, _, schema) = Setup();
        var node = g.GetById(127)!;
        var longText = new string('a', 2001);
        var rebuilt = RebuildWithReplacement(g, schema, ReplaceText(node, longText));
        var result = v.Validate(rebuilt);
        Assert.Contains(result.Errors, e => e.Code == "text_too_long" && e.NodeId == node.Id);
    }

    [Fact]
    public void Validate_TextAt2000Chars_Passes()
    {
        var (v, g, _, schema) = Setup();
        var node = g.GetById(127)!;
        var exactlyAtLimit = new string('a', 2000);
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

    [Fact]
    public void Validate_SiblingTitleCollisionInPathTree_Reports_DuplicateSiblingTitle()
    {
        // stg-0010 step-02: ключ uniqueness под addressable tree (path) — (parent, title).
        // Берём двух siblings одного path-parent и переименовываем второго в title первого.
        var (v, g, _, schema) = Setup();
        var (donor, victim) = FindTwoSiblingsUnderSamePathParent(g);
        var renamed = ReplaceTitle(victim, donor.Title);
        var rebuilt = RebuildWithReplacement(g, schema, renamed);
        var result = v.Validate(rebuilt);
        Assert.Contains(result.Errors, e => e.Code == "duplicate_sibling_title" && e.NodeId == victim.Id);
    }

    /// <summary>
    /// Возвращает пару узлов (donor, victim), у которых общий path-родитель;
    /// donor.Title будет «отдан» victim'у, чтобы вызвать sibling-collision.
    /// Для path-tree ключ collision'а — <c>(parent_id, title)</c> (без типа), т. е.
    /// donor и victim могут быть как одного типа, так и разных. Реальный <c>docs/</c>
    /// DocsWalker гарантированно содержит пары siblings одного типа (атомы внутри section).
    /// </summary>
    private static (Node Donor, Node Victim) FindTwoSiblingsUnderSamePathParent(GraphModel graph)
    {
        var byParent = new Dictionary<int, List<Node>>();
        foreach (var n in graph.ById.Values)
        {
            if (n.Id == Node.RootId) continue;
            if (n.ParentId is not int pid) continue;
            if (!byParent.TryGetValue(pid, out var list))
            {
                list = new List<Node>();
                byParent[pid] = list;
            }
            list.Add(n);
        }
        foreach (var (_, siblings) in byParent)
        {
            if (siblings.Count < 2) continue;
            // Берём первых двух с разными title (чтобы переименование действительно меняло title).
            for (int i = 0; i < siblings.Count; i++)
            {
                for (int j = i + 1; j < siblings.Count; j++)
                {
                    if (!string.Equals(siblings[i].Title, siblings[j].Title, StringComparison.Ordinal))
                        return (siblings[i], siblings[j]);
                }
            }
        }
        throw new InvalidOperationException(
            "В docs/ нет двух siblings под одним path-parent с разными title — невозможно построить тест.");
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
