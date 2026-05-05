using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Style-проверки по содержимому Node (см. docs/DocsWalker.yml/«Контракт валидации»).
/// Сырой YAML-текст не разбирается. Что проверяем:
///   1) title узла соответствует title_format его типа (Format → TryParse roundtrip);
///   2) ни одна строка-значение узла не содержит управляющих символов перевода строки
///      (\n, \r) и табуляции (\t) — все значения помещаются на одну YAML-строку.
/// </summary>
internal static class StyleCheck
{
    private static readonly char[] ForbiddenChars = ['\n', '\r', '\t'];

    public static void Run(SchemaDocument schema, GraphModel graph, List<ValidationError> errors)
    {
        var byName = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var t in schema.Types) byName[t.Name] = t;

        foreach (var node in graph.ById.Values)
        {
            if (byName.TryGetValue(node.TypeName, out var tdef)
                && tdef is NodeType nt
                && nt.TitleFormat is not null)
            {
                CheckTitleFormat(node, nt.TitleFormat, errors);
            }

            CheckSingleLine(node, "title", node.Title, errors);
            if (node.InlineValue is not null)
                CheckSingleLine(node, "inline value", node.InlineValue, errors);

            if (node.Fields is not null)
            {
                foreach (var fv in node.Fields)
                {
                    if (fv.Scalar is not null)
                        CheckSingleLine(node, $"field '{fv.Name}'", fv.Scalar, errors);
                    if (fv.Items is not null)
                    {
                        for (int i = 0; i < fv.Items.Count; i++)
                            CheckSingleLine(node, $"field '{fv.Name}'[{i}]", fv.Items[i], errors);
                    }
                }
            }

            if (node.Blocks is not null)
            {
                foreach (var b in node.Blocks)
                {
                    if (b is TextBlock tb)
                    {
                        for (int i = 0; i < tb.Items.Count; i++)
                            CheckSingleLine(node, $"block '{tb.Name}'[{i}]", tb.Items[i], errors);
                    }
                }
            }
        }
    }

    private static void CheckTitleFormat(Node node, string format, List<ValidationError> errors)
    {
        try
        {
            var formatted = TitleFormat.Format(format, node.Id, node.Title);
            if (!TitleFormat.TryParse(format, formatted, out var rid, out var rtitle))
            {
                errors.Add(new ValidationError(
                    "invalid_title_format",
                    $"Узел id={node.Id} типа '{node.TypeName}': '{node.Title}' не парсится обратно по title_format='{format}'.",
                    node.SourceFile, node.Id));
                return;
            }
            if (rid != node.Id || !string.Equals(rtitle, node.Title, StringComparison.Ordinal))
                errors.Add(new ValidationError(
                    "invalid_title_format",
                    $"Узел id={node.Id} типа '{node.TypeName}': roundtrip через title_format='{format}' дал id={rid}, title='{rtitle}'.",
                    node.SourceFile, node.Id));
        }
        catch (ArgumentException ex)
        {
            errors.Add(new ValidationError(
                "invalid_title_format",
                $"Узел id={node.Id} типа '{node.TypeName}': ошибка форматирования по title_format='{format}': {ex.Message}",
                node.SourceFile, node.Id));
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
            Hint: "Значения помещаются на одну YAML-строку; разбей длинный текст на несколько элементов блока (массив строк) вместо одного многострочного значения."));
    }
}
