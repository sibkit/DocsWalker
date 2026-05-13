using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Style-проверки под refs-модель (см. docs/DocsWalker.yml/«Style-проверка»):
///
/// title узла — всегда одна строка, ни \n, ни \r, ни \t. Это путь-сегмент,
/// многострочный title не имеет смысла.
///
/// text узла — длиной не более <see cref="MaxTextLength"/> символов; \n и пустые
/// строки внутри text допустимы (LLM может писать многоабзацные формулировки),
/// но \r и \t запрещены — это управляющие символы, которые ломают сериализацию
/// и чтение и почти всегда — артефакт скопированного из IDE текста.
///
/// Соответствие правилу title_format ("(#id) title") универсально и проверяется
/// парсером <see cref="Graph.DocumentLoader"/> при чтении документа; здесь не дублируется.
/// </summary>
internal static class StyleCheck
{
    /// <summary>Максимальная длина text в символах (см. правило #139).</summary>
    public const int MaxTextLength = 2000;
    private const int LossyPlaceholderRunLength = 4;

    private static readonly char[] ForbiddenInTitle = ['\n', '\r', '\t'];
    private static readonly char[] ForbiddenInText = ['\r', '\t'];

    public static void Run(SchemaDocument schema, GraphModel graph, List<ValidationError> errors)
    {
        _ = schema;
        foreach (var node in graph.ById.Values)
        {
            if (node.Id == Node.RootId) continue;

            CheckTitle(node, errors);
            CheckText(node, errors);
        }
    }

    private static void CheckTitle(Node node, List<ValidationError> errors)
    {
        if (string.IsNullOrEmpty(node.Title)) return;
        var idx = node.Title.IndexOfAny(ForbiddenInTitle);
        if (idx >= 0)
        {
            errors.Add(new ValidationError(
                "invalid_text",
                $"Узел id={node.Id}: title содержит '{Marker(node.Title[idx])}' (в title запрещены \\n, \\r, \\t — это всегда одна строка).",
                node.SourceFile, node.Id,
                Hint: "Title — это 1–2-словный path-сегмент; многострочные title не используются."));
        }

        CheckCorruptTextArtifact(node, node.Title, "title", errors);
    }

    private static void CheckText(Node node, List<ValidationError> errors)
    {
        if (string.IsNullOrEmpty(node.Text)) return;

        if (node.Text.Length > MaxTextLength)
        {
            errors.Add(new ValidationError(
                "text_too_long",
                $"Узел id={node.Id}: длина text — {node.Text.Length} символов, лимит — {MaxTextLength}.",
                node.SourceFile, node.Id,
                Hint: $"Сократи формулировку или разбей атом на несколько (statement/rule/example) — лимит {MaxTextLength} символов."));
        }

        var idx = node.Text.IndexOfAny(ForbiddenInText);
        if (idx >= 0)
        {
            errors.Add(new ValidationError(
                "invalid_text",
                $"Узел id={node.Id}: text содержит '{Marker(node.Text[idx])}' (в text запрещены \\r и \\t; \\n допустим).",
                node.SourceFile, node.Id,
                Hint: "Замени табуляцию на пробелы; \\r — побочный артефакт CRLF, удали."));
        }

        CheckCorruptTextArtifact(node, node.Text, "text", errors);
    }

    private static void CheckCorruptTextArtifact(
        Node node,
        string value,
        string fieldName,
        List<ValidationError> errors)
    {
        var artifact = FindCorruptTextArtifact(value);
        if (artifact is null) return;

        errors.Add(new ValidationError(
            "corrupt_text",
            $"Узел id={node.Id}: {fieldName} содержит {artifact.Value.Description} около позиции {artifact.Value.Index}.",
            node.SourceFile,
            node.Id,
            Hint: artifact.Value.Hint));
    }

    private static TextCorruptionArtifact? FindCorruptTextArtifact(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\uFFFD')
                return new TextCorruptionArtifact(
                    i,
                    "Unicode replacement character U+FFFD",
                    "Перепиши значение из исходного смысла: U+FFFD означает, что декодер уже потерял исходный символ.");
        }

        var mojibakeIdx = IndexOfMojibake(value);
        if (mojibakeIdx >= 0)
        {
            return new TextCorruptionArtifact(
                mojibakeIdx,
                "похоже на mojibake после неверного декодирования UTF-8",
                "Перепиши значение нормальным Unicode-текстом; последовательности вида 'РџСЂ...'/'Ã...' в docs/ запрещены.");
        }

        var placeholderIdx = IndexOfLossyPlaceholderRun(value);
        if (placeholderIdx >= 0)
        {
            return new TextCorruptionArtifact(
                placeholderIdx,
                "серию placeholder-вопросов вместо текста",
                "Перепиши значение из исходного смысла; длинные серии '?' после потери кодировки в docs/ запрещены.");
        }

        return null;
    }

    private static int IndexOfMojibake(string value)
    {
        var cp1251Idx = IndexOfUtf8DecodedAsWindows1251(value);
        if (cp1251Idx >= 0) return cp1251Idx;

        return IndexOfUtf8DecodedAsWestern(value);
    }

    private static int IndexOfUtf8DecodedAsWindows1251(string value)
    {
        var first = -1;
        var pairs = 0;

        for (var i = 0; i + 1 < value.Length; i++)
        {
            if (IsWindows1251MojibakePair(value[i], value[i + 1]))
            {
                if (first < 0) first = i;
                pairs++;
                if (pairs >= 2) return first;
                i++;
                continue;
            }

            first = -1;
            pairs = 0;
        }

        return -1;
    }

    private static bool IsWindows1251MojibakePair(char lead, char tail)
    {
        if (lead is not ('Р' or 'С')) return false;

        return tail is >= '\u00A0' and <= '\u00BF'
            || tail is >= '\u0400' and <= '\u045F'
            || IsCp1251Punctuation(tail);
    }

    private static bool IsCp1251Punctuation(char c) => c is
        '\u20AC' or '\u201A' or '\u0192' or '\u201E' or '\u2026' or '\u2020' or '\u2021' or
        '\u02C6' or '\u2030' or '\u2039' or '\u2018' or '\u2019' or '\u201C' or '\u201D' or
        '\u2022' or '\u2013' or '\u2014' or '\u2122' or '\u203A';

    private static int IndexOfUtf8DecodedAsWestern(string value)
    {
        for (var i = 0; i + 1 < value.Length; i++)
        {
            var current = value[i];
            var next = value[i + 1];

            if (current is 'Ã' or 'Â' or 'Ð' or 'Ñ')
            {
                if (IsWesternMojibakeContinuation(next))
                    return i;
            }
            else if (current == 'â')
            {
                if (next is '\u0080' or '\u0081' or '\u0082' or '\u0083' or '\u0084' or '\u0085'
                    or '\u0086' or '\u0087' or '\u0088' or '\u0089' or '\u008A' or '\u008B'
                    or '\u008C' or '\u008D' or '\u008E' or '\u008F' or '€' or '„' or 'œ')
                    return i;
            }
        }

        return -1;
    }

    private static bool IsWesternMojibakeContinuation(char c) =>
        c is >= '\u0080' and <= '\u00BF'
        || c is '\u2018' or '\u2019' or '\u201C' or '\u201D' or '\u20AC' or '\u2122';

    private static int IndexOfLossyPlaceholderRun(string value)
    {
        var questionRun = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '?')
            {
                questionRun = 0;
                continue;
            }

            questionRun++;
            if (questionRun >= LossyPlaceholderRunLength)
                return i - questionRun + 1;
        }

        return -1;
    }

    private readonly record struct TextCorruptionArtifact(int Index, string Description, string Hint);

    private static string Marker(char c) => c switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        _ => "?",
    };
}
