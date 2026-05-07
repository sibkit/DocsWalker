using System.Text.Json.Nodes;
using DocsWalker.Cli.Cli;

namespace DocsWalker.Tests;

/// <summary>
/// Проверяем, что при write-ошибке с известным типом узла CLI встраивает в error
/// готовый ответ describe-type — LLM получает контракт типа без отдельного вызова.
/// </summary>
public class ErrorEnrichmentTests
{
    [Fact]
    public void TryDescribeType_KnownType_ReturnsDescribeTypeShape()
    {
        var json = ErrorEnrichment.TryDescribeType(TestPaths.RepoRoot, "rule");
        Assert.NotNull(json);
        var obj = json!.AsObject();
        Assert.Equal("rule", (string?)obj["name"]);
        Assert.True((bool?)obj["text_required"] ?? false);

        var outRefs = obj["out_refs"]!.AsArray();
        Assert.NotEmpty(outRefs);
        // У rule заявлены связи path (tree) и examples (cardinality+required) — оба обязаны быть.
        Assert.Contains(outRefs, r => (string?)r!["name"] == "path");
        Assert.Contains(outRefs, r => (string?)r!["name"] == "examples");
    }

    [Fact]
    public void TryDescribeType_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(ErrorEnrichment.TryDescribeType(TestPaths.RepoRoot, null));
        Assert.Null(ErrorEnrichment.TryDescribeType(TestPaths.RepoRoot, string.Empty));
    }

    [Fact]
    public void TryDescribeType_UnknownType_ReturnsNull_NoThrow()
    {
        // Не существует, но enrichment не должен бросить — просто отдать null,
        // чтобы основная ошибка дошла до LLM как есть.
        var json = ErrorEnrichment.TryDescribeType(TestPaths.RepoRoot, "definitely_not_a_type");
        Assert.Null(json);
    }
}
