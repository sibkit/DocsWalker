using System.Text.Json.Nodes;
using DocsWalker.Core.Schema;
using DocsWalker.Kernel;

namespace DocsWalker.Tests;

/// <summary>
/// Контракт inputSchema для tool <c>create-node</c> (см. docs/DocsWalker.yml/«(#377)»):
/// единый JSON-Schema object без top-level <c>oneOf</c>/<c>allOf</c>/<c>anyOf</c>
/// (запрещены Anthropic API). <c>properties</c> описывает все известные поля,
/// <c>required</c> на верхнем уровне — <c>[type, title]</c>; per-type required
/// транслируется в <c>description</c> корневой схемы и поля <c>type</c>.
/// Источник истины для required по типу — серверный валидатор create-node.
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
    public void TopLevel_DoesNotContain_OneOfAllOfAnyOf()
    {
        // Anthropic API на этапе валидации tool-list отвергает inputSchema с
        // oneOf/allOf/anyOf на верхнем уровне. Регрессия — сессии падают
        // с ошибкой "input_schema does not support oneOf, allOf, or anyOf
        // at the top level". См. (#377).
        var schema = BuildSchema();
        Assert.False(schema.ContainsKey("oneOf"));
        Assert.False(schema.ContainsKey("allOf"));
        Assert.False(schema.ContainsKey("anyOf"));
    }

    [Fact]
    public void TopLevel_Required_IsExactlyTypeAndTitle()
    {
        // Per-type required (text при text_required=true, обязательные out_refs)
        // вынесен в description: на верхнем уровне фиксируем только два поля,
        // которые обязательны для всех creatable-типов.
        var schema = BuildSchema();
        var required = Assert.IsType<JsonArray>(schema["required"]);
        var requiredSet = required.Select(n => (string?)n).Where(s => s is not null).Select(s => s!).ToHashSet();

        Assert.Equal(new HashSet<string> { "type", "title" }, requiredSet);
    }

    [Fact]
    public void TopLevelDescription_ContainsPerTypeRequiredTable()
    {
        // Required по типу транслируется в текстовую таблицу в description,
        // чтобы модель видела required-наборы при выборе типа.
        var schema = BuildSchema();
        var description = (string?)schema["description"];
        Assert.NotNull(description);

        // rule: text_required=true + required out_ref examples (cardinality=many)
        // + classifier-tree-refs (subject/subsystem/audience/csharp_structure, все required).
        var ruleLine = ExtractRequiredLine(description!, "rule");
        Assert.Contains("type", ruleLine);
        Assert.Contains("title", ruleLine);
        Assert.Contains("text", ruleLine);
        Assert.Contains("path", ruleLine);
        Assert.Contains("examples", ruleLine);

        // section: text_required=false, обязательны type+title+path.
        var sectionLine = ExtractRequiredLine(description!, "section");
        Assert.Contains("type", sectionLine);
        Assert.Contains("title", sectionLine);
        Assert.Contains("path", sectionLine);
    }

    [Fact]
    public void TypeProperty_Description_ContainsPerTypeRequiredTable()
    {
        // Та же таблица дублируется в description поля type — это самое
        // вероятное место, куда модель смотрит при выборе значения type.
        var schema = BuildSchema();
        var typeProp = Assert.IsType<JsonObject>(schema["properties"]!["type"]);
        var description = (string?)typeProp["description"];
        Assert.NotNull(description);

        var ruleLine = ExtractRequiredLine(description!, "rule");
        Assert.Contains("type", ruleLine);
        Assert.Contains("title", ruleLine);
        Assert.Contains("text", ruleLine);
        Assert.Contains("path", ruleLine);
        Assert.Contains("examples", ruleLine);

        var sectionLine = ExtractRequiredLine(description!, "section");
        Assert.Contains("type", sectionLine);
        Assert.Contains("title", sectionLine);
        Assert.Contains("path", sectionLine);
    }

    /// <summary>
    /// Извлекает из текстовой таблицы required-полей строку для конкретного типа,
    /// например "<c>rule: [type, title, ...]</c>". Тест устойчив к расширению
    /// required-набора (например, при добавлении новых tree-refs в Схему).
    /// </summary>
    private static string ExtractRequiredLine(string description, string typeName)
    {
        var prefix = typeName + ": [";
        var idx = description.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"В description не найдена строка для типа '{typeName}'.");
        var endIdx = description.IndexOf(']', idx);
        Assert.True(endIdx > idx, $"В description строка для '{typeName}' не закрыта ']'.");
        return description.Substring(idx, endIdx - idx + 1);
    }

    [Fact]
    public void TopLevel_TypeIsObject_DescriptionPresent()
    {
        var schema = BuildSchema();
        Assert.Equal("object", (string?)schema["type"]);
        Assert.False(string.IsNullOrEmpty((string?)schema["description"]));
    }
}
