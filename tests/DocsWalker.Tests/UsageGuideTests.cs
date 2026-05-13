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
    private static JsonDocument CaptureGuide(string? commandFilter, string? fieldsFilter = null)
    {
        var oldOut = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var exit = SchemaHandlers.GetUsageGuide(TestPaths.DocsRoot, commandFilter, fieldsFilter);
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
        using var doc = CaptureGuide("describe-type");
        var commands = doc.RootElement.GetProperty("commands").EnumerateArray().ToList();

        Assert.Single(commands);
        Assert.Equal("describe-type", commands[0].GetProperty("name").GetString());

        // Остальные поля guide — на месте.
        Assert.True(doc.RootElement.TryGetProperty("mental_model", out _));
        Assert.True(doc.RootElement.TryGetProperty("trees", out _));
        Assert.True(doc.RootElement.TryGetProperty("graph_snapshot", out _));
    }

    [Fact]
    public void Fields_CommandsWithKnownCommand_ReturnsOnlySingleCommandSection()
    {
        using var doc = CaptureGuide("tx", "commands");
        var root = doc.RootElement;
        var commands = root.GetProperty("commands").EnumerateArray().ToList();

        Assert.Single(commands);
        Assert.Equal("tx", commands[0].GetProperty("name").GetString());
        Assert.False(root.TryGetProperty("mental_model", out _));
        Assert.False(root.TryGetProperty("trees", out _));
        Assert.False(root.TryGetProperty("graph_snapshot", out _));
    }

    [Fact]
    public void Fields_Trees_ReturnsOnlyTreesSection()
    {
        using var doc = CaptureGuide(commandFilter: null, fieldsFilter: "trees");
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("trees", out var trees));
        Assert.True(trees.GetArrayLength() > 0);
        Assert.False(root.TryGetProperty("mental_model", out _));
        Assert.False(root.TryGetProperty("commands", out _));
        Assert.False(root.TryGetProperty("graph_snapshot", out _));
    }

    [Fact]
    public void NoFilter_IncludesLlmJsonApiToolsAndNoTransactionSurface()
    {
        using var doc = CaptureGuide(null);
        var names = doc.RootElement.GetProperty("commands").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
            .ToHashSet();

        Assert.Contains("hit", names);
        Assert.Contains("query", names);
        Assert.Contains("tx", names);
        Assert.Contains("get-overview", names);
        Assert.Contains("get-usage-guide", names);
        Assert.Contains("describe-type", names);
        Assert.Contains("get-schema", names);
        Assert.DoesNotContain("check-integrity", names);
        Assert.DoesNotContain("brief", names);
        Assert.DoesNotContain("checkpoint", names);
        Assert.DoesNotContain("resume", names);
        Assert.DoesNotContain("context-check", names);
        Assert.DoesNotContain("get-nodes", names);
        Assert.DoesNotContain("search", names);
        Assert.DoesNotContain("transaction", names);
        Assert.DoesNotContain("get-tree", names);
        Assert.DoesNotContain("get-refs", names);
        Assert.DoesNotContain("create-node", names);
        Assert.DoesNotContain("update-schema", names);
        Assert.False(doc.RootElement.TryGetProperty("transaction_operations", out _));
    }

    [Fact]
    public void NoFilter_IsMcpKernelOnlyAndDoesNotAdvertiseCli()
    {
        using var doc = CaptureGuide(null);
        var raw = doc.RootElement.GetRawText();
        var names = doc.RootElement.GetProperty("commands").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
            .ToHashSet();

        Assert.DoesNotContain("docswalker ", raw);
        Assert.DoesNotContain("Примеры CLI", raw);
        Assert.DoesNotContain("Контракт CLI", raw);
        Assert.DoesNotContain("repl", names);
    }

    [Fact]
    public void Filter_Tx_ReturnsLlmJsonApiTool()
    {
        using var doc = CaptureGuide("tx");
        var commands = doc.RootElement.GetProperty("commands").EnumerateArray().ToList();

        Assert.Single(commands);
        Assert.Equal("tx", commands[0].GetProperty("name").GetString());
        Assert.Equal("write", commands[0].GetProperty("kind").GetString());
        Assert.Contains("LLM-facing JSON API", commands[0].GetProperty("description").GetString());
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
        try { exit = SchemaHandlers.GetUsageGuide(TestPaths.DocsRoot, "no-such-command"); }
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

    [Fact]
    public void Fields_UnknownField_ReturnsExitOneWithInvalidParameter()
    {
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        var sw = new StringWriter();
        var sErr = new StringWriter();
        Console.SetOut(sw);
        Console.SetError(sErr);
        int exit;
        try { exit = SchemaHandlers.GetUsageGuide(TestPaths.DocsRoot, commandFilter: null, fieldsFilter: "commands,nope"); }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }

        Assert.Equal(1, exit);
        using var doc = JsonDocument.Parse(sErr.ToString());
        Assert.Equal("invalid_parameter", doc.RootElement.GetProperty("code").GetString());
    }
}
