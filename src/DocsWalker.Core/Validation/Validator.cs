using DocsWalker.Core.Schema;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Полный набор проверок целостности docs/ из «Контракта валидации» DocsWalker.yml.
/// Все проверки строятся вокруг in-memory графа узлов; сырой YAML-текст файлов
/// валидатор не разбирает (структуру файла отлавливает SharpYaml на этапе загрузки).
/// Применение — на write-пути: операция применяется только если результирующий
/// граф проходит <see cref="Validate"/>.
/// </summary>
public sealed class Validator
{
    private readonly MetaSchemaDocument _meta;
    private readonly SchemaDocument _schema;

    public Validator(MetaSchemaDocument meta, SchemaDocument schema)
    {
        _meta = meta;
        _schema = schema;
    }

    /// <summary>
    /// Полный прогон проверок. <paramref name="sequence"/> — текущее значение
    /// sequence-счётчика (из .docswalker/sequence.txt); если null, проверка
    /// sequence_underflow пропускается (для совместимости с тестовыми вызовами,
    /// у которых sequence-файла нет).
    /// </summary>
    public ValidationResult Validate(GraphModel graph, int? sequence = null)
    {
        var errors = new List<ValidationError>();
        MetaSchemaCheck.Run(_meta, _schema, errors);
        SchemaCheck.Run(_schema, graph, errors);
        RefsCheck.Run(_schema, graph, errors);
        UniqueCheck.Run(graph, errors);
        ParentBlockCheck.Run(_schema, graph, errors);
        SequenceCheck.Run(graph, sequence, errors);
        StyleCheck.Run(_schema, graph, errors);
        return new ValidationResult(errors);
    }
}
