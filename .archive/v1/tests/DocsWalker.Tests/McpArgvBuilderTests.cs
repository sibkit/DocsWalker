using System.Text.Json;
using DocsWalker.Core.Mcp;
using DocsWalker.Kernel;

namespace DocsWalker.Tests;

/// <summary>
/// Маршалинг MCP <c>tools/call.arguments</c>-объекта в CLI argv через
/// <see cref="McpArgvBuilder.BuildArgvFromArguments"/>. Покрывает разделение
/// «массив-id-list (CSV)» vs «массив-объектов (raw JSON)», snake_case→kebab,
/// scalar/object/null маршалинг.
/// </summary>
public class McpArgvBuilderTests
{
    [Fact]
    public void BuildArgv_StringValue_ProducesKvFlag()
    {
        using var doc = JsonDocument.Parse(@"{""name"":""section""}");
        var argv = McpArgvBuilder.BuildArgvFromArguments("describe-type", doc.RootElement);
        Assert.Equal(new[] { "describe-type", "--name=section" }, argv);
    }

    [Fact]
    public void BuildArgv_IntegerValue_ProducesKvFlag()
    {
        using var doc = JsonDocument.Parse(@"{""id"":42,""depth"":2}");
        var argv = McpArgvBuilder.BuildArgvFromArguments("get-tree", doc.RootElement);
        Assert.Contains("--id=42", argv);
        Assert.Contains("--depth=2", argv);
    }

    [Fact]
    public void BuildArgv_ArrayValue_ProducesCsvIdList()
    {
        using var doc = JsonDocument.Parse(@"{""ids"":[1,8,42]}");
        var argv = McpArgvBuilder.BuildArgvFromArguments("delete-nodes", doc.RootElement);
        Assert.Equal(new[] { "delete-nodes", "--ids=1,8,42" }, argv);
    }

    [Fact]
    public void BuildArgv_BooleanValue_TrueFalse()
    {
        using var doc = JsonDocument.Parse(@"{""dry-run"":true,""quiet"":false}");
        var argv = McpArgvBuilder.BuildArgvFromArguments("create-node", doc.RootElement);
        Assert.Contains("--dry-run=true", argv);
        Assert.Contains("--quiet=false", argv);
    }

    [Fact]
    public void BuildArgv_ObjectValue_PassedAsRawJson()
    {
        // Object-параметр (JsonValueKind.Object): сырой JSON всегда передаётся как есть.
        using var doc = JsonDocument.Parse(
            @"{""payload"":{""op"":""create-node"",""type"":""section""}}");
        var argv = McpArgvBuilder.BuildArgvFromArguments("query", doc.RootElement);
        Assert.Equal(2, argv.Length);
        Assert.Equal("query", argv[0]);
        Assert.StartsWith("--payload=", argv[1]);
        Assert.Contains("\"op\":\"create-node\"", argv[1]);
        Assert.Contains("{", argv[1]);
        Assert.Contains("}", argv[1]);
    }

    [Fact]
    public void BuildArgv_LlmJsonApiOpsArray_WithDescriptor_PassedAsRawJsonWithBrackets()
    {
        // LLM JSON API ops объявлен как array-of-object — серверный
        // конвертер должен передать raw JSON-массив со скобками [{...},{...}], чтобы
        // CLI получил валидный JSON. Без скобок CLI-парсер падает на 'invalid_parameter'.
        var tools = CommandsToTools.Build();
        var txTool = tools.First(t => t.Name == "tx");
        var paramByName = txTool.Params.ToDictionary(p => p.Name, StringComparer.Ordinal);

        using var doc = JsonDocument.Parse(
            @"{""ops"":[{""op"":""create"",""path"":""x""},{""op"":""link"",""from"":1}]}");
        var argv = McpArgvBuilder.BuildArgvFromArguments("tx", doc.RootElement, paramByName);

        Assert.Equal(2, argv.Length);
        Assert.Equal("tx", argv[0]);
        Assert.StartsWith("--ops=[", argv[1]);
        Assert.EndsWith("]", argv[1]);
        Assert.Contains("\"op\":\"create\"", argv[1]);
        Assert.Contains("\"op\":\"link\"", argv[1]);
    }

    [Fact]
    public void BuildArgv_LlmJsonApiInputSchema_DeclaresOpsAsArrayOfObject()
    {
        // Контракт MCP-схемы: tx.ops объявлен как array+items=object,
        // не как object. Иначе LLM не пошлёт массив через arguments напрямую.
        var tools = CommandsToTools.Build();
        var txTool = tools.First(t => t.Name == "tx");
        var opsParam = txTool.Params.First(p => p.Name == "ops");
        Assert.Equal("array", opsParam.JsonType);
        Assert.Equal("object", opsParam.ItemsJsonType);
        Assert.True(opsParam.Required);
    }

    [Fact]
    public void BuildArgv_SnakeCaseKeys_NormalizedToKebab()
    {
        using var doc = JsonDocument.Parse(@"{""from_id"":42,""to_id"":8,""name"":""rel""}");
        var argv = McpArgvBuilder.BuildArgvFromArguments("create-ref", doc.RootElement);
        Assert.Contains("--from-id=42", argv);
        Assert.Contains("--to-id=8", argv);
        Assert.Contains("--name=rel", argv);
    }

    [Fact]
    public void BuildArgv_NoArguments_ReturnsToolNameOnly()
    {
        var argv = McpArgvBuilder.BuildArgvFromArguments("get-overview", null);
        Assert.Equal(new[] { "get-overview" }, argv);
    }

    [Fact]
    public void BuildArgv_RootKeyPassedThrough_ForLoudUnknownParameter()
    {
        // stg-0010 step-06: --root убран целиком; silent-strip снят. Если
        // LLM-клиент всё же передаст arguments.root, builder пропускает
        // ключ в argv (--root=...) — Dispatcher.Run на kernel-стороне
        // вернёт unknown_parameter (loud failure вместо тихой совместимости).
        using var doc = JsonDocument.Parse(@"{""root"":""C:\\bogus"",""name"":""section""}");
        var argv = McpArgvBuilder.BuildArgvFromArguments("describe-type", doc.RootElement);
        Assert.Contains(argv, a => a.StartsWith("--root="));
        Assert.Contains("--name=section", argv);
    }

    [Fact]
    public void BuildArgv_FiltersStoragePathKey_EvenIfClientPassedIt()
    {
        // Та же защита для нового имени параметра. Snake_case storage_path
        // нормализуется в kebab storage-path, потом фильтруется.
        using var doc = JsonDocument.Parse(@"{""storage_path"":""C:\\bogus"",""path"":""Doc""}");
        var argv = McpArgvBuilder.BuildArgvFromArguments("get-by-path", doc.RootElement);
        Assert.DoesNotContain(argv, a => a.StartsWith("--storage-path="));
        Assert.DoesNotContain(argv, a => a.StartsWith("--storage_path="));
        Assert.Contains("--path=Doc", argv);
    }
}
