using System.Text.Json.Nodes;
using DocsWalker.Core.Schema;

namespace DocsWalker.Tests;

/// <summary>
/// Тесты на сериализацию мета-схемы для tool <c>get-meta-schema</c>: контракт обещает
/// «полное описание мета-схемы» (см. docs/DocsWalker.yml/«(#19) get_meta_schema»),
/// поэтому JSON должен содержать все верхнеуровневые ключи docs/.docswalker/meta-schema.yml.
/// </summary>
public class MetaSchemaTests
{
    [Fact]
    public void ToJson_ContainsAllTopLevelSectionsFromYaml()
    {
        var meta = SchemaLoader.LoadMetaSchema(TestPaths.MetaSchemaPath);
        var json = SchemaJson.ToJson(meta);

        // Шапка: типизированные поля.
        Assert.True(json.ContainsKey("meta_schema_version"));
        Assert.True(json.ContainsKey("name"));
        Assert.True(json.ContainsKey("description"));
        Assert.True(json.ContainsKey("primitive_types"));

        // Структурные секции: должны попасть из generic-парсинга sections.
        Assert.True(json.ContainsKey("schema_root"),     "schema_root отсутствует в get-meta-schema JSON");
        Assert.True(json.ContainsKey("tree_definition"), "tree_definition отсутствует в get-meta-schema JSON");
        Assert.True(json.ContainsKey("type_definition"), "type_definition отсутствует в get-meta-schema JSON");
        Assert.True(json.ContainsKey("ref_def"),         "ref_def отсутствует в get-meta-schema JSON");
    }

    [Fact]
    public void ToJson_TypeDefinitionFields_HaveExpectedShape()
    {
        // type_definition.fields[*] — список mapping'ов; среди name="text_required"
        // должно быть type=bool с булевым required=true.
        var meta = SchemaLoader.LoadMetaSchema(TestPaths.MetaSchemaPath);
        var json = SchemaJson.ToJson(meta);

        var typeDef = Assert.IsType<JsonObject>(json["type_definition"]);
        var fields = Assert.IsType<JsonArray>(typeDef["fields"]);

        var textRequiredField = fields
            .OfType<JsonObject>()
            .FirstOrDefault(f => (string?)f["name"] == "text_required");
        Assert.NotNull(textRequiredField);

        Assert.Equal("bool", (string?)textRequiredField!["type"]);
        // required → bool через эвристику ReadAnyScalar.
        var required = textRequiredField["required"];
        Assert.NotNull(required);
        Assert.True(required!.GetValue<bool>());
    }

    [Fact]
    public void ToJson_IsIdempotent_DeepCloneAvoidsParentingIssue()
    {
        // Двойной вызов ToJson на одном документе не должен падать (parent-владение
        // JsonNode защищено через DeepClone() внутри ToJson).
        var meta = SchemaLoader.LoadMetaSchema(TestPaths.MetaSchemaPath);
        var first = SchemaJson.ToJson(meta);
        var second = SchemaJson.ToJson(meta);
        Assert.True(first.ContainsKey("schema_root"));
        Assert.True(second.ContainsKey("schema_root"));
    }
}
