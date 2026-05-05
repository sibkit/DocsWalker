using System.Text;
using DocsWalker.Core.Schema;

namespace DocsWalker.Core.Yaml;

/// <summary>
/// Сериализатор schema-файла (docs/Схема.yml). Принимает <see cref="SchemaDocument"/>,
/// возвращает текст YAML по правилам docs/. Используется операцией
/// <c>add_ref_type</c> и (потенциально) другими write-операциями над Схемой.
///
/// Стиль совпадает с <see cref="Emitter"/>: 2-space indent, block-mapping и
/// block-sequence как основные конструкции, минимум кавычек, запрещённые YAML-конструкции
/// ("|", "&gt;", "&amp;", "*", "!", "%") эмиттер не порождает.
///
/// Порядок полей внутри одного <c>type</c> фиксирован: name, kind, далее общие признаки
/// node/title_*/key_type/value_type/of, direction/system (для ref_type),
/// description, fields, blocks, constraints. Это не reверс-парс исходного файла —
/// эмиттер строит каноничный вид.
/// </summary>
public static class SchemaEmitter
{
    public static string EmitSchema(SchemaDocument schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var sb = new StringBuilder();

        WriteScalarLine(sb, 0, "description", schema.Description);

        if (schema.Types.Count == 0)
        {
            sb.Append("types: []\n");
            return sb.ToString();
        }
        sb.Append("types:\n");
        foreach (var t in schema.Types)
            WriteType(sb, 2, t);

        return sb.ToString();
    }

    private static void WriteType(StringBuilder sb, int dashIndent, TypeDefinition type)
    {
        bool first = true;
        switch (type)
        {
            case NodeType nt:
                WriteFirstFieldOfType(sb, dashIndent, ref first, "name", Quoting.Format(nt.Name));
                WriteField(sb, dashIndent, ref first, "kind", TypeKindToString(nt.Kind));
                if (nt.Node is bool node)
                    WriteField(sb, dashIndent, ref first, "node", node ? "true" : "false");
                if (nt.TitleSource is TitleSourceKind ts)
                    WriteField(sb, dashIndent, ref first, "title_source", TitleSourceToString(ts));
                if (nt.TitleField is not null)
                    WriteField(sb, dashIndent, ref first, "title_field", Quoting.Format(nt.TitleField));
                if (nt.TitleFormat is not null)
                    WriteField(sb, dashIndent, ref first, "title_format", Quoting.Format(nt.TitleFormat));
                if (nt.KeyType is not null)
                    WriteField(sb, dashIndent, ref first, "key_type", Quoting.Format(nt.KeyType));
                if (nt.ValueType is not null)
                    WriteField(sb, dashIndent, ref first, "value_type", Quoting.Format(nt.ValueType));
                if (nt.Of is not null)
                    WriteField(sb, dashIndent, ref first, "of", Quoting.Format(nt.Of));
                if (nt.Description is not null)
                    WriteField(sb, dashIndent, ref first, "description", Quoting.Format(nt.Description));
                if (nt.Fields is not null)
                    WriteFieldDefinitions(sb, dashIndent, ref first, nt.Fields);
                if (nt.Blocks is not null)
                    WriteBlockDefinitions(sb, dashIndent, ref first, nt.Blocks);
                if (nt.Constraints is not null)
                    WriteStringList(sb, dashIndent, ref first, "constraints", nt.Constraints);
                break;

            case RefType rt:
                WriteFirstFieldOfType(sb, dashIndent, ref first, "name", Quoting.Format(rt.Name));
                WriteField(sb, dashIndent, ref first, "kind", "ref_type");
                WriteField(sb, dashIndent, ref first, "direction", RefDirectionToString(rt.Direction));
                WriteField(sb, dashIndent, ref first, "system", rt.System ? "true" : "false");
                if (rt.Description is not null)
                    WriteField(sb, dashIndent, ref first, "description", Quoting.Format(rt.Description));
                break;

            case Primitive p:
                WriteFirstFieldOfType(sb, dashIndent, ref first, "name", Quoting.Format(p.Name));
                WriteField(sb, dashIndent, ref first, "kind", "primitive");
                if (p.Description is not null)
                    WriteField(sb, dashIndent, ref first, "description", Quoting.Format(p.Description));
                if (p.Constraints is not null)
                    WriteStringList(sb, dashIndent, ref first, "constraints", p.Constraints);
                break;

            default:
                throw new InvalidOperationException(
                    $"Неизвестный TypeDefinition: {type.GetType().Name}.");
        }
    }

    private static void WriteFieldDefinitions(
        StringBuilder sb, int dashIndent, ref bool first,
        IReadOnlyList<FieldDefinition> fields)
    {
        var prefix = first ? "- " : "  ";
        first = false;
        if (fields.Count == 0)
        {
            WriteLine(sb, dashIndent, prefix + "fields: []");
            return;
        }
        WriteLine(sb, dashIndent, prefix + "fields:");
        var inner = dashIndent + 2;
        foreach (var f in fields) WriteFieldDefinition(sb, inner, f);
    }

    private static void WriteFieldDefinition(StringBuilder sb, int dashIndent, FieldDefinition f)
    {
        bool firstInner = true;
        WriteFirstFieldOfType(sb, dashIndent, ref firstInner, "name", Quoting.Format(f.Name));
        WriteField(sb, dashIndent, ref firstInner, "type", Quoting.Format(f.Type));
        if (f.Of is not null)
            WriteField(sb, dashIndent, ref firstInner, "of", Quoting.Format(f.Of));
        if (f.Values is not null)
            WriteStringList(sb, dashIndent, ref firstInner, "values", f.Values);
        WriteField(sb, dashIndent, ref firstInner, "required", f.Required ? "true" : "false");
        if (f.Default is not null)
            WriteField(sb, dashIndent, ref firstInner, "default", Quoting.Format(f.Default));
        if (f.Description is not null)
            WriteField(sb, dashIndent, ref firstInner, "description", Quoting.Format(f.Description));
    }

    private static void WriteBlockDefinitions(
        StringBuilder sb, int dashIndent, ref bool first,
        IReadOnlyList<BlockDefinition> blocks)
    {
        var prefix = first ? "- " : "  ";
        first = false;
        if (blocks.Count == 0)
        {
            WriteLine(sb, dashIndent, prefix + "blocks: []");
            return;
        }
        WriteLine(sb, dashIndent, prefix + "blocks:");
        var inner = dashIndent + 2;
        foreach (var b in blocks) WriteBlockDefinition(sb, inner, b);
    }

    private static void WriteBlockDefinition(StringBuilder sb, int dashIndent, BlockDefinition b)
    {
        bool firstInner = true;
        WriteFirstFieldOfType(sb, dashIndent, ref firstInner, "name", Quoting.Format(b.Name));
        WriteField(sb, dashIndent, ref firstInner, "of", Quoting.Format(b.Of));
        WriteField(sb, dashIndent, ref firstInner, "required", b.Required ? "true" : "false");
        if (b.Description is not null)
            WriteField(sb, dashIndent, ref firstInner, "description", Quoting.Format(b.Description));
    }

    private static void WriteStringList(
        StringBuilder sb, int dashIndent, ref bool first,
        string name, IReadOnlyList<string> items)
    {
        var prefix = first ? "- " : "  ";
        first = false;
        if (items.Count == 0)
        {
            WriteLine(sb, dashIndent, prefix + name + ": []");
            return;
        }
        WriteLine(sb, dashIndent, prefix + name + ":");
        var inner = dashIndent + 2;
        foreach (var s in items) WriteLine(sb, inner, "- " + Quoting.Format(s));
    }

    private static void WriteFirstFieldOfType(
        StringBuilder sb, int dashIndent, ref bool first, string name, string encoded)
    {
        var prefix = first ? "- " : "  ";
        first = false;
        WriteLine(sb, dashIndent, prefix + name + ": " + encoded);
    }

    private static void WriteField(
        StringBuilder sb, int dashIndent, ref bool first, string name, string encoded)
    {
        var prefix = first ? "- " : "  ";
        first = false;
        WriteLine(sb, dashIndent, prefix + name + ": " + encoded);
    }

    private static void WriteScalarLine(StringBuilder sb, int indent, string key, string value)
    {
        if (indent > 0) sb.Append(' ', indent);
        sb.Append(key);
        sb.Append(": ");
        sb.Append(Quoting.Format(value));
        sb.Append('\n');
    }

    private static void WriteLine(StringBuilder sb, int indent, string line)
    {
        if (indent > 0) sb.Append(' ', indent);
        sb.Append(line);
        sb.Append('\n');
    }

    private static string TypeKindToString(TypeKind k) => k switch
    {
        TypeKind.Mapping => "mapping",
        TypeKind.SingleKeyMapping => "single_key_mapping",
        TypeKind.List => "list",
        TypeKind.Primitive => "primitive",
        TypeKind.RefType => "ref_type",
        _ => k.ToString(),
    };

    private static string TitleSourceToString(TitleSourceKind t) => t switch
    {
        TitleSourceKind.Filename => "filename",
        TitleSourceKind.InlineKey => "inline_key",
        TitleSourceKind.Field => "field",
        _ => t.ToString(),
    };

    private static string RefDirectionToString(RefDirection d) => d switch
    {
        RefDirection.ChildToParent => "child_to_parent",
        RefDirection.FromTo => "from_to",
        _ => d.ToString(),
    };
}
