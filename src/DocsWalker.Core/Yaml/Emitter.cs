using System.Globalization;
using System.Text;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Yaml;

/// <summary>
/// Сериализатор docs/*.yml под refs-модель. Принимает <see cref="Graph"/>, схему и
/// узел-document, возвращает текст YAML по правилам docs/ (см. docs/Правила оформления.yml,
/// docs/DocsWalker.yml/«Стек реализации»).
///
/// Формат:
/// <list type="bullet">
///   <item><description>Документ — block-mapping с полями id, text, далее каждый
///   non-path out_ref как ключ-блок (например, sections).</description></item>
///   <item><description>Смысловой узел-контейнер (есть out_refs в типе) — single_key_mapping
///   "(#id) title" со списком ключ-блоков, каждый блок — single_key_mapping
///   `имя_связи: [...]` с последовательностью атомов.</description></item>
///   <item><description>Атом (text_required=true и нет out_refs в типе) —
///   "(#id) title": text как одна строка.</description></item>
/// </list>
///
/// Стиль: 2-space indent; block-mapping и block-sequence как основные конструкции;
/// inline-key склейка ключа в двойных кавычках; минимум кавычек для прочих скаляров
/// (см. <see cref="Quoting"/>). Запрещённые YAML-конструкции
/// (<c>|</c>, <c>&gt;</c>, <c>&amp;</c>, <c>*</c>, <c>!</c>, <c>%</c>) эмиттер не порождает.
/// </summary>
public sealed class Emitter
{
    private readonly StringBuilder _sb = new();
    private readonly SchemaDocument _schema;
    private readonly GraphModel _graph;
    private readonly Dictionary<string, TypeDefinition> _typeByName;

    private Emitter(SchemaDocument schema, GraphModel graph)
    {
        _schema = schema;
        _graph = graph;
        _typeByName = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var t in schema.Types) _typeByName[t.Name] = t;
    }

    /// <summary>
    /// Сериализует один документ docs/ (узел типа "document") в YAML-текст.
    /// Текст оканчивается переводом строки.
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
        WriteLine(0, "id: " + document.Id.ToString(CultureInfo.InvariantCulture));
        WriteLine(0, "text: " + Quoting.Format(document.Text));
        foreach (var (refName, targetIds) in EnumerateOrderedRefs(document))
            WriteRefBlockTopLevel(refName, targetIds);
    }

    /// <summary>
    /// Эмитит верхний out_ref документа: <c>name:</c> на col 0, далее sequence
    /// дочерних узлов с dash на col 2.
    /// </summary>
    private void WriteRefBlockTopLevel(string refName, IReadOnlyList<int> targetIds)
    {
        if (targetIds.Count == 0)
        {
            WriteLine(0, refName + ": []");
            return;
        }
        WriteLine(0, refName + ":");
        foreach (var targetId in targetIds)
        {
            var child = ResolveStructuralChild(targetId, refName);
            WriteSemanticNode(2, child);
        }
    }

    /// <summary>
    /// Эмитит смысловой узел (title_source=inline_key) под dash <paramref name="dashIndent"/>.
    /// Решение по форме — по типу:
    ///   - <b>atom</b> (только text, без semantic-refs): <c>"(#id) title": text</c>.
    ///   - <b>container</b> (есть semantic-refs или нет text): <c>"(#id) title":\n  - block...</c>.
    /// В container-форме сначала идёт <c>- text: ...</c> (если text_required и текст не пуст),
    /// затем по одному блоку на каждую заполненную semantic-ref. Per-ref форма:
    ///   - path-child ref (target.path_targets ⊇ source.type) — nested-atoms (sequence атомов);
    ///   - cross-ref (siblings/cross-tree) — flow-list id (<c>name: [188, 189]</c>).
    /// </summary>
    private void WriteSemanticNode(int dashIndent, Node node)
    {
        var type = ResolveType(node);
        var key = TitleFormat.Format(DocumentLoader.InlineKeyFormat, node.Id, node.Title);
        var encodedKey = Quoting.Format(key, forceDoubleQuoted: true);

        bool typeHasSemanticRefs = type.OutRefs.Any(rd =>
            !string.Equals(rd.Name, Node.PathRefName, StringComparison.Ordinal));
        bool isAtomForm = type.TextRequired && !typeHasSemanticRefs;

        if (isAtomForm)
        {
            WriteLine(dashIndent, "- " + encodedKey + ": " + Quoting.Format(node.Text));
            return;
        }

        var orderedRefs = EnumerateOrderedRefs(node).ToList();
        bool hasText = type.TextRequired && !string.IsNullOrEmpty(node.Text);

        if (orderedRefs.Count == 0 && !hasText)
        {
            WriteLine(dashIndent, "- " + encodedKey + ": []");
            return;
        }

        WriteLine(dashIndent, "- " + encodedKey + ":");
        var blockDashIndent = dashIndent + 2;
        if (hasText)
        {
            WriteLine(blockDashIndent, "- text: " + Quoting.Format(node.Text));
        }
        foreach (var (refName, targetIds) in orderedRefs)
        {
            var rd = type.OutRefs.FirstOrDefault(r => string.Equals(r.Name, refName, StringComparison.Ordinal));
            if (rd is not null && !IsPathChildRef(type.Name, rd))
                WriteRefIdList(blockDashIndent, refName, targetIds);
            else
                WriteRefBlockNested(blockDashIndent, refName, targetIds);
        }
    }

    /// <summary>
    /// Эмитит block-seq path-child связи как элемент sequence на dash <paramref name="dashIndent"/>:
    /// <c>- name:</c> + sequence дочерних узлов на dash <paramref name="dashIndent"/>+2.
    /// </summary>
    private void WriteRefBlockNested(int dashIndent, string refName, IReadOnlyList<int> targetIds)
    {
        if (targetIds.Count == 0)
        {
            WriteLine(dashIndent, "- " + refName + ": []");
            return;
        }
        WriteLine(dashIndent, "- " + refName + ":");
        var atomDashIndent = dashIndent + 2;
        foreach (var targetId in targetIds)
        {
            var child = ResolveStructuralChild(targetId, refName);
            WriteSemanticNode(atomDashIndent, child);
        }
    }

    /// <summary>
    /// Эмитит cross-ref как flow-list id: <c>- name: [188, 189]</c>. Цели не сериализуются
    /// здесь — они уже эмитятся в собственном path-родителе (siblings/cross-tree).
    /// </summary>
    private void WriteRefIdList(int dashIndent, string refName, IReadOnlyList<int> targetIds)
    {
        if (targetIds.Count == 0)
        {
            WriteLine(dashIndent, "- " + refName + ": []");
            return;
        }
        var ids = string.Join(", ", targetIds.Select(t => t.ToString(CultureInfo.InvariantCulture)));
        WriteLine(dashIndent, "- " + refName + ": [" + ids + "]");
    }

    /// <summary>
    /// path-child ref: связь, через которую цели становятся path-детьми источника
    /// (target.path_targets ⊇ source.type). Только такие refs сериализуются как nested-atoms;
    /// остальные — id-list flow.
    /// </summary>
    private bool IsPathChildRef(string sourceType, RefDef rd)
    {
        // Любая tree-ref (включая classifier-tree-refs типа subject_parent) — не path-child:
        // на self-referencing типах (subject_category и т.п.) tree-ref имеет target=self и
        // совпадает с path-self-target по типу, что путает простой target_types-тест.
        if (rd.Tree is not null) return false;
        foreach (var targetTypeName in rd.TargetTypes)
        {
            if (!_typeByName.TryGetValue(targetTypeName, out var targetType)) continue;
            if (targetType.PathTargets.Any(pt => string.Equals(pt, sourceType, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Возвращает out_refs узла в стабильном порядке: сначала по объявлению в типе
    /// (если тип найден), затем оставшиеся неизвестные имена. Связь path не входит в
    /// результат. Пустые списки целей пропускаются — эмиттер не пишет шумных <c>: []</c>
    /// для незаполненных дополнительных связей.
    /// </summary>
    private IEnumerable<(string Name, IReadOnlyList<int> Targets)> EnumerateOrderedRefs(Node node)
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        if (_typeByName.TryGetValue(node.TypeName, out var type))
        {
            foreach (var rd in type.OutRefs)
            {
                // path — встроенная структурная связь, не сериализуется в YAML
                // (выводится из размещения файла в docs/ при загрузке).
                if (string.Equals(rd.Name, Node.PathRefName, StringComparison.Ordinal)) continue;
                if (node.OutRefs.TryGetValue(rd.Name, out var targets) && targets.Count > 0)
                {
                    emitted.Add(rd.Name);
                    yield return (rd.Name, targets);
                }
            }
        }
        foreach (var (name, targets) in node.OutRefs)
        {
            if (string.Equals(name, Node.PathRefName, StringComparison.Ordinal)) continue;
            if (emitted.Contains(name)) continue;
            if (targets.Count == 0) continue;
            yield return (name, targets);
        }
    }

    private TypeDefinition ResolveType(Node node)
    {
        if (_typeByName.TryGetValue(node.TypeName, out var t)) return t;
        throw new InvalidOperationException(
            $"Тип '{node.TypeName}' (узел id={node.Id}) не объявлен в Схеме.");
    }

    private Node ResolveStructuralChild(int targetId, string refName)
    {
        var child = _graph.GetById(targetId)
            ?? throw new InvalidOperationException(
                $"Граф не содержит узел id={targetId}, упомянутый в связи '{refName}'.");
        return child;
    }

    private void WriteLine(int indent, string line)
    {
        if (indent > 0) _sb.Append(' ', indent);
        _sb.Append(line);
        _sb.Append('\n');
    }
}
