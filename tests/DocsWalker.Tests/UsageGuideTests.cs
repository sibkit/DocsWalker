using System.Text.Json;
using DocsWalker.Cli.Cli.Handlers;

namespace DocsWalker.Tests;

/// <summary>
/// Поведение <see cref="SchemaHandlers.GetUsageGuide"/> с опциональным фильтром
/// по имени команды (#stg-0008 step-08, sub-task 3).
/// Серилизуется с другими тестами, использующими <see cref="Console.SetOut"/>,
/// через collection "ConsoleRedirect": handler ловит stdout через
/// <see cref="Output"/> → <c>Console.Out</c>; параллельный запуск ломает
/// перехват.
/// </summary>
[Collection("ConsoleRedirect")]
public class UsageGuideTests
{
    private static JsonDocument CaptureGuide(string? commandFilter)
    {
        var oldOut = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var exit = SchemaHandlers.GetUsageGuide(TestPaths.RepoRoot, commandFilter);
            Assert.Equal(0, exit);
        }
        finally { Console.SetOut(oldOut); }
        return JsonDocument.Parse(sw.ToString());
    }

    [Fact]
    public void NoFilter_ReturnsFullCommandsManifest()
    {
        using var doc = CaptureGuide(null);
        var commands = doc.RootElement.GetProperty("commands").EnumerateArray().ToList();
        // Минимум — read+write команды CLI; точное число определяется CommandsToTools,
        // нам важен факт, что фильтр не применился (массив длиннее одного).
        Assert.True(commands.Count > 1);
    }

    [Fact]
    public void Filter_KnownCommand_ReturnsSingleCommandPlusOtherFields()
    {
        using var doc = CaptureGuide("get-nodes");
        var commands = doc.RootElement.GetProperty("commands").EnumerateArray().ToList();

        Assert.Single(commands);
        Assert.Equal("get-nodes", commands[0].GetProperty("name").GetString());

        // Остальные поля guide — на месте.
        Assert.True(doc.RootElement.TryGetProperty("mental_model", out _));
        Assert.True(doc.RootElement.TryGetProperty("trees", out _));
        Assert.True(doc.RootElement.TryGetProperty("graph_snapshot", out _));
    }

    [Fact]
    public void TransactionOperations_ContainsAllSevenOps()
    {
        using var doc = CaptureGuide(null);
        var ops = doc.RootElement.GetProperty("transaction_operations").EnumerateArray()
            .Select(o => o.GetProperty("op").GetString())
            .ToHashSet();

        // 7 операций в TransactionParser: create-node, update-node, delete-nodes,
        // move-node, create-ref, delete-ref, redirect-refs.
        Assert.Contains("create-node", ops);
        Assert.Contains("update-node", ops);
        Assert.Contains("delete-nodes", ops);
        Assert.Contains("move-node", ops);
        Assert.Contains("create-ref", ops);
        Assert.Contains("delete-ref", ops);
        Assert.Contains("redirect-refs", ops);
        Assert.Equal(7, ops.Count);
    }

    [Fact]
    public void TransactionOperations_RedirectRefs_DocumentsFromIdsAsRequiredArrayWithCliMapping()
    {
        using var doc = CaptureGuide(null);
        var redirect = doc.RootElement.GetProperty("transaction_operations").EnumerateArray()
            .First(o => o.GetProperty("op").GetString() == "redirect-refs");

        var fields = redirect.GetProperty("fields").EnumerateArray().ToList();
        var fromIds = fields.First(f => f.GetProperty("json_key").GetString() == "from_ids");

        // Главный фокус под-задачи: from_ids — массив, required, маппится на --from / --from-subtree.
        Assert.Equal("integer[]", fromIds.GetProperty("json_type").GetString());
        Assert.True(fromIds.GetProperty("required").GetBoolean());
        var cliFlag = fromIds.GetProperty("cli_flag").GetString();
        Assert.Contains("--from", cliFlag);
    }

    [Fact]
    public void TransactionOperations_MoveNode_NewParentIdJsonKey_MapsToCliFlagTo()
    {
        using var doc = CaptureGuide(null);
        var moveNode = doc.RootElement.GetProperty("transaction_operations").EnumerateArray()
            .First(o => o.GetProperty("op").GetString() == "move-node");

        var fields = moveNode.GetProperty("fields").EnumerateArray().ToList();
        var newParent = fields.First(f => f.GetProperty("json_key").GetString() == "new_parent_id");

        // JSON-ключ — new_parent_id, CLI-флаг — --to. LLM должна видеть рассинхрон.
        Assert.Equal("--to", newParent.GetProperty("cli_flag").GetString());
        Assert.True(newParent.GetProperty("required").GetBoolean());
    }

    [Fact]
    public void Filter_UnknownCommand_ReturnsExitOneWithUnknownCommandError()
    {
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        var sw = new StringWriter();
        var sErr = new StringWriter();
        Console.SetOut(sw);
        Console.SetError(sErr);
        int exit;
        try { exit = SchemaHandlers.GetUsageGuide(TestPaths.RepoRoot, "no-such-command"); }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }

        Assert.Equal(1, exit);
        var stderr = sErr.ToString();
        // Парсим error envelope из stderr: {"code":"unknown_command", ...}
        using var doc = JsonDocument.Parse(stderr);
        Assert.Equal("unknown_command", doc.RootElement.GetProperty("code").GetString());
    }
}
