using System.Text.Json.Nodes;
using DocsWalker.Cli.Mcp;
using DocsWalker.Core.Schema;

namespace DocsWalker.Tests;

/// <summary>
/// Контракт inputSchema для tool <c>create-node</c> (см. docs/DocsWalker.yml/«(#377)»):
/// генерация из проектной Схемы должна давать единый JSON-Schema object с
/// дискриминатором по полю <c>type</c>; <c>properties</c> описывает все
/// известные поля, <c>oneOf</c> фиксирует required-набор для каждой ветки.
/// Тесты проверяют форму на реальной Схеме docs/Схема.yml.
/// </summary>
public class CreateNodeSchemaTests
{
    private static JsonObject BuildSchema()
    {
        var schemaDoc = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        return CommandsToTools.BuildCreateNodeInputSchema(schemaDoc);
    }

    [Fact]
    public void TypeEnum_ContainsCreatableTypes_ExcludesRoot()
    {
        var schema = BuildSchema();
        var typeProp = Assert.IsType<JsonObject>(schema["properties"]!["type"]);
        var enumArr = Assert.IsType<JsonArray>(typeProp["enum"]);
        var typeNames = enumArr.Select(n => (string?)n).ToHashSet();

        // Перечисляемые типы из реальной Схемы.
        Assert.Contains("folder", typeNames);
        Assert.Contains("document", typeNames);
        Assert.Contains("section", typeNames);
        Assert.Contains("rule", typeNames);
        Assert.Contains("statement", typeNames);

        // root — синтезируется ядром, в enum не попадает.
        Assert.DoesNotContain("root", typeNames);
    }

    [Fact]
    public void Properties_PathRef_DeclaredAsInteger()
    {
        var schema = BuildSchema();
        var pathProp = Assert.IsType<JsonObject>(schema["properties"]!["path"]);
        Assert.Equal("integer", (string?)pathProp["type"]);
    }

    [Fact]
    public void Properties_ManyCardinalityRef_DeclaredAsArrayOfInteger()
    {
        // examples — у rule cardinality=many → array+items=integer.
        var schema = BuildSchema();
        var examplesProp = Assert.IsType<JsonObject>(schema["properties"]!["examples"]);
        Assert.Equal("array", (string?)examplesProp["type"]);
        var items = Assert.IsType<JsonObject>(examplesProp["items"]);
        Assert.Equal("integer", (string?)items["type"]);
    }

    [Fact]
    public void Properties_IncludesDryRunAndOmitsRoot()
    {
        // stg-0010 step-06: --root убран из всех MCP-tools (в том числе
        // из inputSchema create-node). dry-run остаётся универсальным.
        var schema = BuildSchema();
        var properties = Assert.IsType<JsonObject>(schema["properties"]);
        Assert.False(properties.ContainsKey("root"));
        Assert.True(properties.ContainsKey("dry-run"));

        var dryRun = Assert.IsType<JsonObject>(properties["dry-run"]);
        Assert.Equal("boolean", (string?)dryRun["type"]);
    }

    [Fact]
    public void OneOf_RuleBranch_RequiresTypeTitleTextPathExamples()
    {
        // type=rule: text_required=true + required out_ref examples (cardinality=many).
        var schema = BuildSchema();
        var oneOf = Assert.IsType<JsonArray>(schema["oneOf"]);
        var ruleBranch = oneOf.OfType<JsonObject>()
            .First(b => (string?)b["properties"]!["type"]!["const"] == "rule");

        var required = Assert.IsType<JsonArray>(ruleBranch["required"]);
        var requiredSet = required.Select(n => (string?)n).ToHashSet();

        Assert.Contains("type", requiredSet);
        Assert.Contains("title", requiredSet);
        Assert.Contains("text", requiredSet);
        Assert.Contains("path", requiredSet);
        Assert.Contains("examples", requiredSet);
    }

    [Fact]
    public void OneOf_SectionBranch_RequiresTypeTitlePath_ButNotText()
    {
        // type=section: text_required=false; нет required out_refs кроме path.
        var schema = BuildSchema();
        var oneOf = Assert.IsType<JsonArray>(schema["oneOf"]);
        var sectionBranch = oneOf.OfType<JsonObject>()
            .First(b => (string?)b["properties"]!["type"]!["const"] == "section");

        var required = Assert.IsType<JsonArray>(sectionBranch["required"]);
        var requiredSet = required.Select(n => (string?)n).ToHashSet();

        Assert.Contains("type", requiredSet);
        Assert.Contains("title", requiredSet);
        Assert.Contains("path", requiredSet);
        Assert.DoesNotContain("text", requiredSet);
    }

    [Fact]
    public void OneOf_HasBranchPerCreatableType()
    {
        var schemaDoc = SchemaLoader.LoadSchema(TestPaths.SchemaPath);
        var schema = CommandsToTools.BuildCreateNodeInputSchema(schemaDoc);
        var oneOf = Assert.IsType<JsonArray>(schema["oneOf"]);

        var creatableCount = schemaDoc.Types.Count(t => t.Name != "root");
        Assert.Equal(creatableCount, oneOf.Count);
    }

    [Fact]
    public void TopLevel_TypeIsObject_DescriptionPresent()
    {
        var schema = BuildSchema();
        Assert.Equal("object", (string?)schema["type"]);
        Assert.False(string.IsNullOrEmpty((string?)schema["description"]));
    }
}
