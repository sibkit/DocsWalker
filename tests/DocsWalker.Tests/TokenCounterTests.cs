using DocsWalker.Core.Graph;
using DocsWalker.Core.Tokens;

namespace DocsWalker.Tests;

/// <summary>
/// Sanity-проверки BPE-счётчика. Точные значения зависят от cl100k_base — здесь
/// проверяем только инварианты: пустота, монотонность по длине, корректность
/// сериализации структуры узла в одну строку.
/// </summary>
public class TokenCounterTests
{
    [Fact]
    public void Count_Empty_Returns_Zero()
    {
        Assert.Equal(0, TokenCounter.Count(null));
        Assert.Equal(0, TokenCounter.Count(string.Empty));
    }

    [Fact]
    public void Count_NonEmpty_Returns_Positive()
    {
        Assert.True(TokenCounter.Count("hello") > 0);
        Assert.True(TokenCounter.Count("привет") > 0);
    }

    [Fact]
    public void Count_LongerStringHasNoLessTokens()
    {
        var a = TokenCounter.Count("foo");
        var b = TokenCounter.Count("foo bar baz qux");
        Assert.True(b >= a);
    }

    [Fact]
    public void CountNode_Includes_AllFiveFields()
    {
        var withText = MakeNode(id: 42, type: "rule", title: "T", text: "длинный текст правила");
        var noText = MakeNode(id: 42, type: "rule", title: "T", text: "");
        Assert.True(TokenCounter.CountNode(withText) > TokenCounter.CountNode(noText),
            "Узел с непустым text должен иметь больше токенов, чем тот же узел без text.");
    }

    [Fact]
    public void CountNode_Includes_OutRefs()
    {
        var noRefs = MakeNode(id: 42, type: "rule", title: "T", text: "ok");
        var withRefs = new Node
        {
            Id = 42,
            TypeName = "rule",
            Title = "T",
            Text = "ok",
            OutRefs = new Dictionary<string, IReadOnlyList<int>>
            {
                ["path"] = new[] { 1 },
                ["examples"] = new[] { 100, 101, 102 },
            },
            SourceFile = "test.yml",
        };
        Assert.True(TokenCounter.CountNode(withRefs) > TokenCounter.CountNode(noRefs),
            "Узел с out_refs должен иметь больше токенов, чем тот же узел без out_refs.");
    }

    private static Node MakeNode(int id, string type, string title, string text) => new()
    {
        Id = id,
        TypeName = type,
        Title = title,
        Text = text,
        OutRefs = new Dictionary<string, IReadOnlyList<int>>(),
        SourceFile = "test.yml",
    };
}
