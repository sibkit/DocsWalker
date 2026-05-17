using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Validation;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Tests;

/// <summary>
/// Покрытие проверки <c>missing_classifier</c> на синтетической схеме:
/// узел-rule с required tree-ref <c>subject</c> в classifier-дерево <c>subject</c>.
/// Категории первого уровня — <c>api</c> и <c>none</c>; ожидаемые сценарии
/// (без классификатора / с none / с реальной категорией / с двумя ссылками).
/// </summary>
public class ClassifierValidatorTests
{
    private static (Validator V, GraphModel G) BuildFixture()
    {
        var meta = SchemaLoader.LoadMetaSchema(TestPaths.MetaSchemaPath);
        var schema = BuildSchema();
        var graph = BuildGraph(schema);
        return (new Validator(meta, schema), graph);
    }

    private static SchemaDocument BuildSchema()
    {
        var trees = new List<TreeDefinition>
        {
            new(TreeDefinition.PathTreeName, "Дерево хранилища"),
            new("subject", "Классификатор по предметной оси"),
        };

        var documentType = new TypeDefinition(
            "document", null, TitleSource.Filename, TextRequired: true,
            new List<RefDef>
            {
                new("path", new[] { "root" }, "path", Cardinality.One, true, null),
            });
        var sectionType = new TypeDefinition(
            "section", null, TitleSource.InlineKey, TextRequired: false,
            new List<RefDef>
            {
                new("path", new[] { "document", "section" }, "path", Cardinality.One, true, null),
            });
        // category — узел classifier-дерева subject; в path-дереве размещается под document/section.
        var categoryType = new TypeDefinition(
            "category", null, TitleSource.InlineKey, TextRequired: false,
            new List<RefDef>
            {
                new("path", new[] { "document", "section", "category" }, "path", Cardinality.One, true, null),
                new("subject_parent", new[] { "category", "root" }, "subject", Cardinality.One, true, null),
            });
        // rule — атом-узел с required tree-ref subject.
        var ruleType = new TypeDefinition(
            "rule", null, TitleSource.InlineKey, TextRequired: true,
            new List<RefDef>
            {
                new("path", new[] { "section" }, "path", Cardinality.One, true, null),
                new("subject", new[] { "category" }, "subject", Cardinality.One, true, null),
            });

        return new SchemaDocument(
            "Synthetic test schema",
            trees,
            new List<TypeDefinition> { documentType, sectionType, categoryType, ruleType });
    }

    private static GraphModel BuildGraph(SchemaDocument schema)
    {
        var g = new GraphModel();
        g.AttachSchema(schema);

        Add(g, 1, "document", "TestDoc", "desc", new() { ["path"] = new[] { Node.RootId } });
        Add(g, 2, "section", "S1", "", new() { ["path"] = new[] { 1 } });
        // Категории subject: api и none — обе под root в дереве subject.
        Add(g, 8, "category", "api", "", new()
        {
            ["path"] = new[] { 1 },
            ["subject_parent"] = new[] { Node.RootId },
        });
        Add(g, 9, "category", "none", "", new()
        {
            ["path"] = new[] { 1 },
            ["subject_parent"] = new[] { Node.RootId },
        });
        // rules
        Add(g, 4, "rule", "RuleUnset", "text", new() { ["path"] = new[] { 2 } });
        Add(g, 5, "rule", "RuleNone", "text", new()
        {
            ["path"] = new[] { 2 },
            ["subject"] = new[] { 9 },
        });
        Add(g, 6, "rule", "RuleApi", "text", new()
        {
            ["path"] = new[] { 2 },
            ["subject"] = new[] { 8 },
        });
        Add(g, 7, "rule", "RuleDouble", "text", new()
        {
            ["path"] = new[] { 2 },
            ["subject"] = new[] { 8, 9 },
        });
        return g;
    }

    private static void Add(
        GraphModel g, int id, string typeName, string title, string text,
        Dictionary<string, int[]> refs)
    {
        var outRefs = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var (k, v) in refs) outRefs[k] = v;
        g.Add(new Node
        {
            Id = id,
            TypeName = typeName,
            Title = title,
            Text = text,
            OutRefs = outRefs,
            SourceFile = "test.yml",
        });
    }

    [Fact]
    public void RuleWithoutClassifier_Reports_MissingClassifier_WithCategoriesHint()
    {
        var (v, g) = BuildFixture();
        var result = v.Validate(g);

        var err = result.Errors.FirstOrDefault(e => e.Code == "missing_classifier" && e.NodeId == 4);
        Assert.NotNull(err);
        Assert.Equal("subject", err!.RefName);
        Assert.NotNull(err.Hint);
        Assert.Contains("api", err.Hint!);
        Assert.Contains("none", err.Hint!);
        Assert.Contains("subject", err.Hint!);
    }

    [Fact]
    public void RuleWithNoneCategory_DoesNotReportMissingClassifier()
    {
        var (v, g) = BuildFixture();
        var result = v.Validate(g);
        Assert.DoesNotContain(result.Errors, e => e.Code == "missing_classifier" && e.NodeId == 5);
    }

    [Fact]
    public void RuleWithRealCategory_DoesNotReportMissingClassifier()
    {
        var (v, g) = BuildFixture();
        var result = v.Validate(g);
        Assert.DoesNotContain(result.Errors, e => e.Code == "missing_classifier" && e.NodeId == 6);
    }

    [Fact]
    public void RuleWithDoubleClassifier_Reports_InvalidCardinality()
    {
        var (v, g) = BuildFixture();
        var result = v.Validate(g);
        var err = result.Errors.FirstOrDefault(e => e.Code == "invalid_cardinality" && e.NodeId == 7);
        Assert.NotNull(err);
        Assert.Equal("subject", err!.RefName);
    }

    [Fact]
    public void MissingClassifier_RegularRequiredCrossRef_StaysAsMissingRequiredRef()
    {
        // Контроль: required cross-ref (без tree) выдаёт missing_required_ref, не missing_classifier.
        var meta = SchemaLoader.LoadMetaSchema(TestPaths.MetaSchemaPath);
        var schema = BuildSchemaWithCrossRef();
        var g = new GraphModel();
        g.AttachSchema(schema);
        Add(g, 1, "document", "Doc", "desc", new() { ["path"] = new[] { Node.RootId } });
        Add(g, 2, "section", "S", "", new() { ["path"] = new[] { 1 } });
        Add(g, 10, "example", "Ex1", "text", new() { ["path"] = new[] { 2 } });
        // rule_x без examples — required cross-ref не заполнен.
        Add(g, 11, "rule_x", "RuleX", "text", new() { ["path"] = new[] { 2 } });

        var result = new Validator(meta, schema).Validate(g);
        Assert.Contains(result.Errors, e => e.Code == "missing_required_ref" && e.NodeId == 11 && e.RefName == "examples");
        Assert.DoesNotContain(result.Errors, e => e.Code == "missing_classifier" && e.NodeId == 11);
    }

    private static SchemaDocument BuildSchemaWithCrossRef()
    {
        var trees = new List<TreeDefinition>
        {
            new(TreeDefinition.PathTreeName, "Дерево хранилища"),
        };

        var documentType = new TypeDefinition(
            "document", null, TitleSource.Filename, TextRequired: true,
            new List<RefDef>
            {
                new("path", new[] { "root" }, "path", Cardinality.One, true, null),
            });
        var sectionType = new TypeDefinition(
            "section", null, TitleSource.InlineKey, TextRequired: false,
            new List<RefDef>
            {
                new("path", new[] { "document", "section" }, "path", Cardinality.One, true, null),
            });
        var exampleType = new TypeDefinition(
            "example", null, TitleSource.InlineKey, TextRequired: true,
            new List<RefDef>
            {
                new("path", new[] { "section" }, "path", Cardinality.One, true, null),
            });
        var ruleXType = new TypeDefinition(
            "rule_x", null, TitleSource.InlineKey, TextRequired: true,
            new List<RefDef>
            {
                new("path", new[] { "section" }, "path", Cardinality.One, true, null),
                new("examples", new[] { "example" }, null, Cardinality.Many, true, null),
            });

        return new SchemaDocument(
            "Synthetic schema with cross-ref",
            trees,
            new List<TypeDefinition> { documentType, sectionType, exampleType, ruleXType });
    }
}
