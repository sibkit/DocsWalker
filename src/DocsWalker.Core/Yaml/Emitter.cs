using System.Globalization;
using System.Text;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Yaml;

/// <summary>
/// Сериализатор docs/*.yml. Принимает <see cref="Graph"/>, схему и узел-document,
/// возвращает текст YAML по правилам docs/ (см. docs/Правила оформления.yml,
/// docs/DocsWalker.yml/«Стек реализации»).
///
/// Стиль: 2-space indent, block-mapping и block-sequence как основные конструкции,
/// inline-key склейка ключа single_key_mapping в двойных кавычках, минимум кавычек
/// для остальных скаляров (см. <see cref="Quoting"/>). Запрещённые YAML-конструкции
/// ("|", ">", "&amp;", "*", "!", "%") эмиттер не порождает.
/// </summary>
public sealed class Emitter
{
    private readonly StringBuilder _sb = new();
    private readonly SchemaDocument _schema;
    private readonly GraphModel _graph;
    private readonly Dictionary<string, NodeType> _nodeTypeByName;

    private Emitter(SchemaDocument schema, GraphModel graph)
    {
        _schema = schema;
        _graph = graph;
        _nodeTypeByName = new Dictionary<string, NodeType>(StringComparer.Ordinal);
        foreach (var t in schema.Types)
            if (t is NodeType nt) _nodeTypeByName[nt.Name] = nt;
    }

    /// <summary>
    /// Сериализует один документ docs/ (узел типа "document") в YAML-текст.
    /// Текст оканчивается переводом строки, как принято для UTF-8 yml-файлов.
    /// </summary>
    public static string EmitDocument(GraphModel graph, SchemaDocument schema, Node document)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(document.TypeName, "document", StringComparison.Ordinal))
            throw new ArgumentException(
                $"Узел id={document.Id} типа '{document.TypeName}' не является document.",
                nameof(document));

        var em = new Emitter(schema, graph);
        em.WriteDocument(document);
        return em._sb.ToString();
    }

    private void WriteDocument(Node document)
    {
        // Поля документа в фиксированном порядке: id первым, затем description.
        // Дочерние секции идут под "content:" — отдельным sequence-блоком, потому что
        // в Node.Fields поле content не материализовано (children подвешены через ParentId).
        WriteIdLine(0, document);
        var description = GetFieldScalar(document, "description") ?? string.Empty;
        WriteLine(0, "description: " + Quoting.Format(description));

        var sections = _graph.GetChildren(document.Id);
        if (sections.Count == 0)
        {
            WriteLine(0, "content: []");
            return;
        }
        WriteLine(0, "content:");
        // Стиль docs/: items блок-sequence сдвигаются на 2 пробела от родительского ключа;
        // dash родительского ключа лежит на col 0, тогда dash дочернего — на col 2.
        foreach (var section in sections)
            WriteSection(2, section);
    }

    /// <summary>
    /// dashIndent — колонка символа '-' у текущего sequence-item. Внутреннее значение
    /// mapping-ключа (block-sequence элементов внутри) выводится на dashIndent + 2.
    /// </summary>
    private void WriteSection(int dashIndent, Node section)
    {
        var nodeType = ResolveNodeType(section.TypeName);
        var key = TitleFormat.Format(
            nodeType.TitleFormat ?? throw new InvalidOperationException(
                $"У типа '{nodeType.Name}' отсутствует title_format в схеме."),
            section.Id, section.Title);
        var encodedKey = Quoting.Format(key, forceDoubleQuoted: true);

        var blocks = section.Blocks;
        if (blocks is null || blocks.Count == 0)
        {
            WriteLine(dashIndent, "- " + encodedKey + ": []");
            return;
        }
        WriteLine(dashIndent, "- " + encodedKey + ":");
        var childDash = dashIndent + 2;
        foreach (var block in blocks)
            WriteBlock(childDash, block);
    }

    private void WriteBlock(int dashIndent, NodeBlock block)
    {
        var innerDash = dashIndent + 2;
        switch (block)
        {
            case TextBlock tb:
                if (tb.Items.Count == 0)
                {
                    WriteLine(dashIndent, "- " + tb.Name + ": []");
                    return;
                }
                WriteLine(dashIndent, "- " + tb.Name + ":");
                foreach (var item in tb.Items)
                    WriteLine(innerDash, "- " + Quoting.Format(item));
                break;

            case ChildrenBlock cb:
                if (cb.ChildIds.Count == 0)
                {
                    WriteLine(dashIndent, "- " + cb.Name + ": []");
                    return;
                }
                WriteLine(dashIndent, "- " + cb.Name + ":");
                foreach (var childId in cb.ChildIds)
                {
                    var child = _graph.GetById(childId)
                        ?? throw new InvalidOperationException(
                            $"Граф не содержит узел id={childId}, упомянутый в блоке '{cb.Name}'.");
                    WriteChild(innerDash, child);
                }
                break;

            case OutRefsBlock orb:
                if (orb.Refs.Count == 0)
                {
                    WriteLine(dashIndent, "- " + orb.Name + ": []");
                    return;
                }
                WriteLine(dashIndent, "- " + orb.Name + ":");
                foreach (var r in orb.Refs)
                    WriteLine(innerDash,
                        "- " + r.TypeName + ": " + r.ToId.ToString(CultureInfo.InvariantCulture));
                break;

            default:
                throw new InvalidOperationException(
                    $"Неизвестный тип блока: {block.GetType().Name}.");
        }
    }

    private void WriteChild(int dashIndent, Node child)
    {
        var nodeType = ResolveNodeType(child.TypeName);
        switch (nodeType.Kind)
        {
            case TypeKind.SingleKeyMapping:
                WriteSingleKeyMappingChild(dashIndent, child, nodeType);
                break;
            case TypeKind.Mapping:
                WriteMappingChild(dashIndent, child, nodeType);
                break;
            default:
                throw new InvalidOperationException(
                    $"Не поддерживается дочерний узел kind={nodeType.Kind} (id={child.Id}, type='{nodeType.Name}').");
        }
    }

    private void WriteSingleKeyMappingChild(int dashIndent, Node child, NodeType nodeType)
    {
        var format = nodeType.TitleFormat ?? throw new InvalidOperationException(
            $"У типа '{nodeType.Name}' отсутствует title_format в схеме.");
        var key = TitleFormat.Format(format, child.Id, child.Title);
        var encodedKey = Quoting.Format(key, forceDoubleQuoted: true);

        if (child.InlineValue is null)
            throw new InvalidOperationException(
                $"Single_key_mapping узел id={child.Id} типа '{nodeType.Name}' без InlineValue не поддерживается эмиттером.");

        WriteLine(dashIndent, "- " + encodedKey + ": " + Quoting.Format(child.InlineValue));
    }

    private void WriteMappingChild(int dashIndent, Node child, NodeType nodeType)
    {
        var fields = child.Fields ?? Array.Empty<FieldValue>();
        if (fields.Count == 0)
        {
            WriteLine(dashIndent, "- {}");
            return;
        }

        var fieldDefs = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        if (nodeType.Fields is not null)
            foreach (var fd in nodeType.Fields) fieldDefs[fd.Name] = fd;

        // Первая пара ключ-значение поджата к "- "; остальные сдвинуты на 2 пробела.
        // Содержимое list-полей (например, values: [...]) идёт на dashIndent + 4.
        bool first = true;
        var listItemIndent = dashIndent + 4;
        foreach (var f in fields)
        {
            var prefix = first ? "- " : "  ";
            first = false;
            WriteFieldOnLine(dashIndent, prefix, f, fieldDefs, listItemIndent);
        }
    }

    private void WriteFieldOnLine(
        int dashIndent, string prefix, FieldValue f,
        IReadOnlyDictionary<string, FieldDefinition> fieldDefs,
        int listItemIndent)
    {
        if (f.Items is not null)
        {
            if (f.Items.Count == 0)
            {
                WriteLine(dashIndent, prefix + f.Name + ": []");
                return;
            }
            WriteLine(dashIndent, prefix + f.Name + ":");
            foreach (var item in f.Items)
                WriteLine(listItemIndent, "- " + Quoting.Format(item));
            return;
        }

        var raw = f.Scalar ?? string.Empty;
        var encoded = FormatFieldScalar(f.Name, raw, fieldDefs);
        WriteLine(dashIndent, prefix + f.Name + ": " + encoded);
    }

    private static string FormatFieldScalar(
        string fieldName, string raw,
        IReadOnlyDictionary<string, FieldDefinition> fieldDefs)
    {
        // id всегда integer; иначе берём тип поля из схемы.
        if (string.Equals(fieldName, "id", StringComparison.Ordinal)) return raw;
        if (!fieldDefs.TryGetValue(fieldName, out var fd)) return Quoting.Format(raw);
        return fd.Type switch
        {
            "integer" or "bool" or "enum" => raw,
            _ => Quoting.Format(raw),
        };
    }

    private NodeType ResolveNodeType(string name)
    {
        if (_nodeTypeByName.TryGetValue(name, out var t)) return t;
        throw new InvalidOperationException(
            $"Тип '{name}' не объявлен в схеме как mapping/single_key_mapping/list.");
    }

    private void WriteIdLine(int indent, Node node)
    {
        // id всегда первый ключ в mapping (см. Правила оформления/«Порядок полей»).
        WriteLine(indent, "id: " + node.Id.ToString(CultureInfo.InvariantCulture));
    }

    private static string? GetFieldScalar(Node node, string name)
    {
        if (node.Fields is null) return null;
        foreach (var f in node.Fields)
            if (string.Equals(f.Name, name, StringComparison.Ordinal)) return f.Scalar;
        return null;
    }

    private void WriteLine(int indent, string line)
    {
        if (indent > 0) _sb.Append(' ', indent);
        _sb.Append(line);
        _sb.Append('\n');
    }
}
