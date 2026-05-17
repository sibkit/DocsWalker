using DocsWalker.Core.Yaml;

namespace DocsWalker.Tests;

public class QuotingTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("Описание поля.")]
    [InlineData("docs/.docswalker/meta-schema.yml")]
    [InlineData("Утилита управления docs/.")]
    public void Format_PlainStringsStayUnquoted(string value)
    {
        Assert.Equal(value, Quoting.Format(value));
    }

    [Theory]
    [InlineData("a: b", "'a: b'")]
    [InlineData("# leading", "'# leading'")]
    [InlineData("- leading", "'- leading'")]
    [InlineData("trailing ", "'trailing '")]
    [InlineData("space then # comment", "'space then # comment'")]
    [InlineData("trailing colon:", "'trailing colon:'")]
    [InlineData("{leading", "'{leading'")]
    [InlineData("[leading", "'[leading'")]
    public void Format_SpecialCharsForceSingleQuoted(string raw, string expected)
    {
        Assert.Equal(expected, Quoting.Format(raw));
    }

    [Theory]
    // Внутренние '{', '[', одиночная кавычка в block-context — plain валиден,
    // лишних кавычек не ставим.
    [InlineData("foo {bar}")]
    [InlineData("a [b]")]
    [InlineData("can't")]
    public void Format_InlineSpecialCharsStayPlain(string value)
    {
        Assert.Equal(value, Quoting.Format(value));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("yes")]
    [InlineData("42")]
    [InlineData("-7")]
    [InlineData("3.14")]
    public void Format_ReservedScalarsAreQuoted(string raw)
    {
        var formatted = Quoting.Format(raw);
        Assert.NotEqual(raw, formatted);
        Assert.StartsWith("'", formatted);
        Assert.EndsWith("'", formatted);
    }

    [Fact]
    public void Format_EmptyStringIsQuoted()
    {
        var formatted = Quoting.Format(string.Empty);
        Assert.Contains(formatted, new[] { "''", "\"\"" });
    }

    [Fact]
    public void Format_SingleQuoteCombinedWithSpecial_UsesSingleQuotedWithDoubling()
    {
        // Если строка одновременно требует кавычек (например, начинается с '#')
        // и содержит одинарную кавычку — выбирается single-quoted с удвоением.
        var formatted = Quoting.Format("# can't");
        Assert.Equal("'# can''t'", formatted);
    }

    [Fact]
    public void Format_StringWithControlChars_UsesDoubleQuotedWithEscape()
    {
        var formatted = Quoting.Format("line1\nline2");
        Assert.StartsWith("\"", formatted);
        Assert.EndsWith("\"", formatted);
        Assert.Contains("\\n", formatted);
    }

    [Fact]
    public void Format_ForceDoubleQuoted_AlwaysWrapsInDoubleQuotes()
    {
        Assert.Equal("\"abc\"", Quoting.Format("abc", forceDoubleQuoted: true));
        Assert.Equal("\"(#42) Заголовок\"",
            Quoting.Format("(#42) Заголовок", forceDoubleQuoted: true));
    }

    [Fact]
    public void Format_DoubleQuoted_EscapesQuoteAndBackslash()
    {
        // forceDoubleQuoted-путь должен корректно экранировать спецсимволы.
        var formatted = Quoting.Format("a\\b\"c", forceDoubleQuoted: true);
        Assert.Equal("\"a\\\\b\\\"c\"", formatted);
    }
}
