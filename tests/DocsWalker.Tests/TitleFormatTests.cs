using DocsWalker.Core.Graph;

namespace DocsWalker.Tests;

public class TitleFormatTests
{
    private const string Std = "(#{id}) {title}";

    [Fact]
    public void Format_Composes_IdAndTitle()
    {
        Assert.Equal("(#42) Биекция", TitleFormat.Format(Std, 42, "Биекция"));
    }

    [Fact]
    public void TryParse_Splits_StandardForm()
    {
        Assert.True(TitleFormat.TryParse(Std, "(#42) Биекция текст ↔ граф", out var id, out var title));
        Assert.Equal(42, id);
        Assert.Equal("Биекция текст ↔ граф", title);
    }

    [Fact]
    public void TryParse_Roundtrip_AnyTitle()
    {
        // Любая нормальная склейка должна обратимо раскладываться.
        var key = TitleFormat.Format(Std, 7, "abc");
        Assert.True(TitleFormat.TryParse(Std, key, out var id, out var title));
        Assert.Equal(7, id);
        Assert.Equal("abc", title);
    }

    [Fact]
    public void TryParse_FailsOn_MissingPrefix()
    {
        Assert.False(TitleFormat.TryParse(Std, "42) что-то", out _, out _));
    }

    [Fact]
    public void TryParse_FailsOn_NonNumericId()
    {
        Assert.False(TitleFormat.TryParse(Std, "(#abc) что-то", out _, out _));
    }

    [Fact]
    public void TryParse_FailsOn_NegativeId()
    {
        Assert.False(TitleFormat.TryParse(Std, "(#-1) что-то", out _, out _));
    }

    [Fact]
    public void TryParse_FailsOn_EmptyTitle()
    {
        Assert.False(TitleFormat.TryParse(Std, "(#1) ", out _, out _));
    }

    [Fact]
    public void TryParse_FailsOn_AdjacentVariables()
    {
        // Соседние подстановки без литерала между ними однозначно не разбираются.
        Assert.False(TitleFormat.TryParse("{id}{title}", "42hello", out _, out _));
    }

    [Fact]
    public void Format_TitleMayContain_TemplateLiteralChars()
    {
        // title может содержать круглые скобки и решётку — Format просто подставляет.
        var key = TitleFormat.Format(Std, 1, "(побочное) #имя");
        Assert.Equal("(#1) (побочное) #имя", key);
        // Обратный разбор именно такой склейки выдаст другой title — это известное
        // ограничение, отражённое здесь как фиксация поведения.
        Assert.True(TitleFormat.TryParse(Std, key, out var id, out var title));
        Assert.Equal(1, id);
        Assert.Equal("(побочное) #имя", title);
    }
}
