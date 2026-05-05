using System.Globalization;
using System.Text;

namespace DocsWalker.Core.Graph;

/// <summary>
/// Парсер и сборка склеенного ключа узла по шаблону title_format
/// (например, "(#{id}) {title}" ↔ id=42, title="Биекция текст ↔ граф").
/// Шаблон состоит из литералов и подстановок {id} / {title} в любом порядке.
/// Соседние подстановки без литерала между ними не поддерживаются — однозначный
/// разбор в этом случае невозможен.
/// </summary>
public static class TitleFormat
{
    /// <summary>
    /// Собирает склеенный ключ из id и title по шаблону.
    /// </summary>
    public static string Format(string template, int id, string title)
    {
        var segments = ParseTemplate(template);
        var sb = new StringBuilder();
        foreach (var seg in segments)
        {
            if (seg.IsLiteral)
            {
                sb.Append(seg.Text);
            }
            else if (seg.Name == "id")
            {
                sb.Append(id.ToString(CultureInfo.InvariantCulture));
            }
            else if (seg.Name == "title")
            {
                sb.Append(title);
            }
            else
            {
                throw new ArgumentException(
                    $"Шаблон title_format '{template}' содержит неизвестную подстановку '{{{seg.Name}}}'.",
                    nameof(template));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Разбирает склеенный ключ по шаблону. Возвращает true и присваивает id/title,
    /// если строка точно соответствует шаблону.
    /// </summary>
    public static bool TryParse(string template, string raw, out int id, out string title)
    {
        id = 0;
        title = string.Empty;

        var segments = ParseTemplate(template);
        int pos = 0;
        int? parsedId = null;
        string? parsedTitle = null;

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (seg.IsLiteral)
            {
                if (pos + seg.Text.Length > raw.Length) return false;
                if (string.CompareOrdinal(raw, pos, seg.Text, 0, seg.Text.Length) != 0) return false;
                pos += seg.Text.Length;
                continue;
            }

            // Это подстановка. Найти границу до следующего литерала.
            int end;
            if (i == segments.Count - 1)
            {
                end = raw.Length;
            }
            else
            {
                var nextSeg = segments[i + 1];
                if (!nextSeg.IsLiteral)
                {
                    // Соседние {id}{title} без разделителя — неоднозначный разбор.
                    return false;
                }
                end = raw.IndexOf(nextSeg.Text, pos, StringComparison.Ordinal);
                if (end < 0) return false;
            }

            var value = raw.Substring(pos, end - pos);
            if (seg.Name == "id")
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return false;
                if (v < 0) return false;
                parsedId = v;
            }
            else if (seg.Name == "title")
            {
                if (value.Length == 0) return false;
                parsedTitle = value;
            }
            else
            {
                return false;
            }

            pos = end;
        }

        if (pos != raw.Length) return false;
        if (parsedId is null || parsedTitle is null) return false;

        id = parsedId.Value;
        title = parsedTitle;
        return true;
    }

    private readonly record struct Segment(bool IsLiteral, string Text, string? Name);

    private static List<Segment> ParseTemplate(string template)
    {
        var list = new List<Segment>();
        var lit = new StringBuilder();
        int i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                int close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    // Незакрытая фигурная скобка — трактуем как литерал.
                    lit.Append(template[i]);
                    i++;
                    continue;
                }
                if (lit.Length > 0)
                {
                    list.Add(new Segment(true, lit.ToString(), null));
                    lit.Clear();
                }
                var name = template.Substring(i + 1, close - i - 1);
                list.Add(new Segment(false, string.Empty, name));
                i = close + 1;
            }
            else
            {
                lit.Append(template[i]);
                i++;
            }
        }
        if (lit.Length > 0) list.Add(new Segment(true, lit.ToString(), null));
        return list;
    }
}
