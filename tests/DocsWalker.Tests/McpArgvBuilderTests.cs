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
        using var doc = JsonDocument.Parse(@"{""query"":""validator""}");
        var argv = McpArgvBuilder.BuildArgvFromArguments("search", doc.RootElement);
        Assert.Equal(new[] { "search", "--query=validator" }, argv);
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
        var argv = McpArgvBuilder.BuildArgvFromArguments("get-nodes", doc.RootElement);
        Assert.Equal(new[] { "get-nodes", "--ids=1,8,42" }, argv);
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
        var argv = McpArgvBuilder.BuildArgvFromArguments("transaction", doc.RootElement);
        Assert.Equal(2, argv.Length);
        Assert.Equal("transaction", argv[0]);
        Assert.StartsWith("--payload=", argv[1]);
        Assert.Contains("\"op\":\"create-node\"", argv[1]);
        Assert.Contains("{", argv[1]);
        Assert.Contains("}", argv[1]);
    }

    [Fact]
    public void BuildArgv_TransactionOperationsArray_WithDescriptor_PassedAsRawJsonWithBrackets()
    {
        // transaction.operations объявлен в Schema как array-of-object — серверный
        // конвертер должен передать raw JSON-массив со скобками [{...},{...}], чтобы
        // CLI получил валидный JSON. Без скобок CLI-парсер падает на 'invalid_parameter'.
        var tools = CommandsToTools.Build();
        var transactionTool = tools.First(t => t.Name == "transaction");
        var paramByName = transactionTool.Params.ToDictionary(p => p.Name, StringComparer.Ordinal);

        using var doc = JsonDocument.Parse(
            @"{""operations"":[{""op"":""create-node"",""type"":""section""},{""op"":""create-ref"",""from_id"":1}]}");
        var argv = McpArgvBuilder.BuildArgvFromArguments("transaction", doc.RootElement, paramByName);

        Assert.Equal(2, argv.Length);
        Assert.Equal("transaction", argv[0]);
        Assert.StartsWith("--operations=[", argv[1]);
        Assert.EndsWith("]", argv[1]);
        Assert.Contains("\"op\":\"create-node\"", argv[1]);
        Assert.Contains("\"op\":\"create-ref\"", argv[1]);
    }

    [Fact]
    public void BuildArgv_TransactionInputSchema_DeclaresOperationsAsArrayOfObject()
    {
        // Контракт MCP-схемы: transaction.operations объявлен как array+items=object,
        // не как object. Иначе LLM не пошлёт массив через arguments напрямую.
        var tools = CommandsToTools.Build();
        var transactionTool = tools.First(t => t.Name == "transaction");
        var operationsParam = transactionTool.Params.First(p => p.Name == "operations");
        Assert.Equal("array", operationsParam.JsonType);
        Assert.Equal("object", operationsParam.ItemsJsonType);
        Assert.True(operationsParam.Required);
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
        var argv = McpArgvBuilder.BuildArgvFromArguments("check-integrity", null);
        Assert.Equal(new[] { "check-integrity" }, argv);
    }

    [Fact]
    public void BuildArgv_RootKeyPassedThrough_ForLoudUnknownParameter()
    {
        // stg-0010 step-06: --root убран целиком; silent-strip снят. Если
        // LLM-клиент всё же передаст arguments.root, builder пропускает
        // ключ в argv (--root=...) — Dispatcher.Run на kernel-стороне
        // вернёт unknown_parameter (loud failure вместо тихой совместимости).
        using var doc = JsonDocument.Parse(@"{""root"":""C:\\bogus"",""query"":""x""}");
        var argv = McpArgvBuilder.BuildArgvFromArguments("search", doc.RootElement);
        Assert.Contains(argv, a => a.StartsWith("--root="));
        Assert.Contains("--query=x", argv);
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
