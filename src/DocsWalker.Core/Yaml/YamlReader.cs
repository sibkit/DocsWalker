using SharpYaml;
using SharpYaml.Events;

namespace DocsWalker.Core.Yaml;

/// <summary>
/// Ошибка, возникающая при чтении YAML-потока на низком уровне:
/// неожиданное событие, неожиданный конец потока, ожидание скаляра.
/// Каждый прикладной loader (SchemaLoader, DocumentLoader) ловит её
/// и переоборачивает в собственное доменное исключение.
/// </summary>
public sealed class YamlReadException : Exception
{
    public string Code { get; }
    public string FilePath { get; }

    public YamlReadException(string code, string filePath, string message)
        : base(message)
    {
        Code = code;
        FilePath = filePath;
    }
}

/// <summary>
/// Тонкая надстройка над SharpYaml IParser с peek-операцией и типизированным
/// ожиданием очередного события. Используется обоими loader-ами проекта.
/// </summary>
public sealed class YamlReader
{
    private readonly IParser _parser;
    private readonly string _filePath;
    private bool _hasPeeked;
    private ParsingEvent? _peeked;

    public YamlReader(TextReader reader, string filePath)
    {
        _parser = Parser.CreateParser(reader);
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    public ParsingEvent? Peek()
    {
        if (!_hasPeeked)
        {
            _hasPeeked = _parser.MoveNext();
            _peeked = _hasPeeked ? _parser.Current : null;
        }
        return _peeked;
    }

    public ParsingEvent? Next()
    {
        if (_hasPeeked)
        {
            var ev = _peeked;
            _hasPeeked = false;
            _peeked = null;
            return ev;
        }
        return _parser.MoveNext() ? _parser.Current : null;
    }

    public T Expect<T>() where T : ParsingEvent
    {
        var ev = Next();
        if (ev is null)
            throw new YamlReadException(
                "yaml_eof",
                _filePath,
                $"Неожиданный конец YAML-потока, ожидалось событие {typeof(T).Name}.");
        if (ev is not T typed)
            throw new YamlReadException(
                "yaml_unexpected",
                _filePath,
                $"Ожидалось событие {typeof(T).Name}, получено {ev.GetType().Name}.");
        return typed;
    }

    public string NextScalarValue()
    {
        var ev = Next();
        if (ev is not Scalar s)
            throw new YamlReadException(
                "yaml_unexpected",
                _filePath,
                $"Ожидался скаляр, получено {ev?.GetType().Name ?? "null"}.");
        return s.Value;
    }

    /// <summary>
    /// Пропускает значение текущей позиции: один скаляр, либо целый mapping/sequence
    /// со всем содержимым.
    /// </summary>
    public void SkipValue()
    {
        var ev = Next();
        if (ev is null) return;
        if (ev is Scalar) return;
        if (ev is MappingStart || ev is SequenceStart)
        {
            var depth = 1;
            while (depth > 0)
            {
                var inner = Next();
                if (inner is null) return;
                if (inner is MappingStart || inner is SequenceStart) depth++;
                else if (inner is MappingEnd || inner is SequenceEnd) depth--;
            }
        }
    }
}
