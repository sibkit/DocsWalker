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
    public const int MaxTextLength = 1000;

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
        if (idx < 0) return;
        errors.Add(new ValidationError(
            "invalid_text",
            $"Узел id={node.Id}: title содержит '{Marker(node.Title[idx])}' (в title запрещены \\n, \\r, \\t — это всегда одна строка).",
            node.SourceFile, node.Id,
            Hint: "Title — это 1–2-словный path-сегмент; многострочные title не используются."));
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
    }

    private static string Marker(char c) => c switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        _ => "?",
    };
}
