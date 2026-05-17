using System.Globalization;
using System.Text;

namespace DocsWalker.Core.Yaml;

/// <summary>
/// Стиль YAML-скаляра, выбираемый эмиттером.
/// </summary>
public enum YamlScalarStyle
{
    Plain,
    SingleQuoted,
    DoubleQuoted,
}

/// <summary>
/// Политика выбора стиля скаляра под правила оформления docs/
/// (см. docs/Правила оформления.yml). По умолчанию предпочитается plain;
/// при наличии спецсимволов или потенциально неоднозначных значений —
/// одинарные кавычки; двойные — только для inline-key склеек и в случаях,
/// требующих escape-последовательностей.
/// </summary>
public static class Quoting
{
    /// <summary>
    /// Возвращает строку, готовую к подстановке как скалярное значение в block-context
    /// (после "key: " или после "- ").
    /// </summary>
    /// <param name="value">сырое строковое значение.</param>
    /// <param name="forceDoubleQuoted">true — всегда двойные кавычки (для inline-key
    /// склеек "(#id) title", где docs/ требуют двойных).</param>
    public static string Format(string value, bool forceDoubleQuoted = false)
    {
        if (forceDoubleQuoted) return EncodeDouble(value);
        var style = ChooseStyle(value);
        return style switch
        {
            YamlScalarStyle.Plain => value,
            YamlScalarStyle.SingleQuoted => EncodeSingle(value),
            YamlScalarStyle.DoubleQuoted => EncodeDouble(value),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary>
    /// Выбирает минимально-кавычечный стиль для скаляра.
    /// </summary>
    public static YamlScalarStyle ChooseStyle(string value)
    {
        if (CanBePlain(value)) return YamlScalarStyle.Plain;
        if (!HasControlChars(value) && !value.Contains('\'')) return YamlScalarStyle.SingleQuoted;
        if (!HasControlChars(value)) return YamlScalarStyle.SingleQuoted;
        return YamlScalarStyle.DoubleQuoted;
    }

    private static bool CanBePlain(string v)
    {
        if (string.IsNullOrEmpty(v)) return false;
        if (HasControlChars(v)) return false;

        char c0 = v[0];
        if (c0 == ' ') return false;
        if (IsForbiddenFirst(c0)) return false;
        // -, ?, : допустимы как первый символ только если не сопровождаются пробелом.
        if ((c0 == '-' || c0 == '?' || c0 == ':') && (v.Length == 1 || v[1] == ' ')) return false;

        // ": " / " #" / завершающие пробел/двоеточие/таб ломают plain-парсер YAML.
        if (v.Contains(": ", StringComparison.Ordinal)) return false;
        if (v.Contains(" #", StringComparison.Ordinal)) return false;
        char cLast = v[^1];
        if (cLast == ':' || cLast == ' ' || cLast == '\t') return false;

        // Значение, омонимичное null/bool/числу — кавычить, иначе теряется тип на парсинге.
        if (LooksLikeReservedScalar(v)) return false;

        return true;
    }

    private static bool HasControlChars(string v)
    {
        foreach (var c in v)
        {
            if (c == '\n' || c == '\r' || c == '\t') return true;
            if (c < 0x20) return true;
            if (c == '\x7f') return true;
        }
        return false;
    }

    private static bool IsForbiddenFirst(char c) =>
        c is '#' or '&' or '*' or '!' or '|' or '>' or '%' or '@' or '`' or
             '\'' or '"' or ',' or '[' or ']' or '{' or '}';

    private static bool LooksLikeReservedScalar(string v)
    {
        // YAML 1.1/1.2 имена-резервы для null/bool, нормализованные в нижний регистр.
        switch (v)
        {
            case "null":
            case "Null":
            case "NULL":
            case "~":
            case "true":
            case "True":
            case "TRUE":
            case "false":
            case "False":
            case "FALSE":
            case "yes":
            case "Yes":
            case "YES":
            case "no":
            case "No":
            case "NO":
            case "on":
            case "On":
            case "ON":
            case "off":
            case "Off":
            case "OFF":
                return true;
        }

        // Числа: int (десятичный, +/-) и float (.NET-парсер достаточно либеральный, но
        // нам важно поймать «выглядит как число»).
        var ci = CultureInfo.InvariantCulture;
        if (long.TryParse(v, NumberStyles.Integer | NumberStyles.AllowLeadingSign, ci, out _))
            return true;
        if (double.TryParse(v, NumberStyles.Float, ci, out _))
            return true;
        return false;
    }

    private static string EncodeSingle(string v)
    {
        // В single-quoted экранируется только сама одинарная кавычка — удвоением.
        return "'" + v.Replace("'", "''") + "'";
    }

    private static string EncodeDouble(string v)
    {
        var sb = new StringBuilder(v.Length + 2);
        sb.Append('"');
        foreach (var c in v)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c == '\x7f')
                        sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
