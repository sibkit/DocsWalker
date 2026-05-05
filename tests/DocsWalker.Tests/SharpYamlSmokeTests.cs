using SharpYaml;
using SharpYaml.Events;

namespace DocsWalker.Tests;

public class SharpYamlSmokeTests
{
    private static List<ParsingEvent> Parse(string yaml)
    {
        using var reader = new StringReader(yaml);
        var parser = Parser.CreateParser(reader);
        var events = new List<ParsingEvent>();
        while (parser.MoveNext())
        {
            events.Add(parser.Current!);
        }
        return events;
    }

    private static List<string> Scalars(IEnumerable<ParsingEvent> events) =>
        events.OfType<Scalar>().Select(s => s.Value).ToList();

    [Fact]
    public void BlockMapping_Parses()
    {
        var events = Parse("a: 1\nb: 2\n");

        var mapStart = Assert.Single(events.OfType<MappingStart>());
        Assert.Equal(YamlStyle.Block, mapStart.Style);
        Assert.Single(events.OfType<MappingEnd>());
        Assert.Equal(new[] { "a", "1", "b", "2" }, Scalars(events));
    }

    [Fact]
    public void BlockSequence_Parses()
    {
        var events = Parse("- one\n- two\n- three\n");

        var seqStart = Assert.Single(events.OfType<SequenceStart>());
        Assert.Equal(YamlStyle.Block, seqStart.Style);
        Assert.Single(events.OfType<SequenceEnd>());
        Assert.Equal(new[] { "one", "two", "three" }, Scalars(events));
    }

    [Fact]
    public void FlowSequence_Parses()
    {
        var events = Parse("[a, b, c]\n");

        var seqStart = Assert.Single(events.OfType<SequenceStart>());
        Assert.Equal(YamlStyle.Flow, seqStart.Style);
        Assert.Equal(new[] { "a", "b", "c" }, Scalars(events));
    }

    [Fact]
    public void FlowMapping_Parses()
    {
        // Критичная проверка: flow-mapping {k: v, k2: v2} — частая дыра
        // в YAML-парсерах. Используется в нашей форме reference.
        var events = Parse("{k: v, k2: v2}\n");

        var mapStart = Assert.Single(events.OfType<MappingStart>());
        Assert.Equal(YamlStyle.Flow, mapStart.Style);
        Assert.Equal(new[] { "k", "v", "k2", "v2" }, Scalars(events));
    }

    [Fact]
    public void Scalars_UnquotedSingleDouble_ParseToSameValue()
    {
        var unquoted = Parse("text: hello world\n");
        var single = Parse("text: 'hello world'\n");
        var dbl = Parse("text: \"hello world\"\n");

        Assert.Equal("hello world", Scalars(unquoted).Last());
        Assert.Equal(ScalarStyle.Plain, unquoted.OfType<Scalar>().Last().Style);
        Assert.Equal("hello world", Scalars(single).Last());
        Assert.Equal(ScalarStyle.SingleQuoted, single.OfType<Scalar>().Last().Style);
        Assert.Equal("hello world", Scalars(dbl).Last());
        Assert.Equal(ScalarStyle.DoubleQuoted, dbl.OfType<Scalar>().Last().Style);
    }

    [Fact]
    public void IntegerAndBool_ParseAsScalars()
    {
        // На event-stream уровне SharpYaml выдаёт строковое значение,
        // тип фиксируется тегом или неявно — нам важно, что значения
        // дочитываются без ошибки.
        var events = Parse("count: 42\nflag_true: true\nflag_false: false\n");

        var scalars = Scalars(events);
        Assert.Contains("42", scalars);
        Assert.Contains("true", scalars);
        Assert.Contains("false", scalars);
    }

    [Fact]
    public void Comments_AreStrippedFromEventStream()
    {
        // В SharpYaml комментарии в event-stream не попадают —
        // отдельного типа события Comment нет, парсер их пропускает.
        // Проверяем, что комментарии не ломают разбор и не появляются
        // как лишние скаляры.
        var events = Parse("# leading comment\nkey: value # inline\n");

        Assert.Equal(new[] { "key", "value" }, Scalars(events));
    }
}
