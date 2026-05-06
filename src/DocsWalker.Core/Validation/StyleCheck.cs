using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Style-проверки под refs-модель: ни одна строка-значение узла (title, text)
/// не содержит управляющих символов \n, \r, \t — все значения помещаются на
/// одну YAML-строку (см. docs/Правила оформления.yml/«Одна строка»).
/// title-format-roundtrip отдельной проверкой не нужен — формат универсален
/// `"(#id) title"`, и парсер DocumentLoader валидирует его при чтении.
/// </summary>
internal static class StyleCheck
{
    private static readonly char[] ForbiddenChars = ['\n', '\r', '\t'];

    public static void Run(SchemaDocument schema, GraphModel graph, List<ValidationError> errors)
    {
        _ = schema;
        foreach (var node in graph.ById.Values)
        {
            if (node.Id == Node.RootId) continue;

            CheckSingleLine(node, "title", node.Title, errors);
            CheckSingleLine(node, "text", node.Text, errors);
        }
    }

    private static void CheckSingleLine(
        Node node, string what, string value, List<ValidationError> errors)
    {
        if (string.IsNullOrEmpty(value)) return;
        var idx = value.IndexOfAny(ForbiddenChars);
        if (idx < 0) return;
        var marker = value[idx] switch
        {
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            _ => "?",
        };
        errors.Add(new ValidationError(
            "multiline_value",
            $"Узел id={node.Id}: значение {what} содержит '{marker}' (запрещены переводы строки и табуляции).",
            node.SourceFile, node.Id,
            Hint: "Значения помещаются на одну YAML-строку; разбей длинный текст на несколько атомов вместо одного многострочного значения."));
    }
}
