using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Core.Validation;

/// <summary>
/// Sequence-инвариант: значение sequence-счётчика должно покрывать все id, встречающиеся
/// в графе. Иначе следующий ReserveId выдаст id, уже занятый каким-то узлом — после чего
/// валидация упадёт по duplicate_id, причём с непрозрачной диагностикой.
/// Для прогона передаётся текущее значение sequence (из <see cref="DocsWalker.Core.Store.SequenceCounter"/>);
/// проверка одна — sequence ≥ max(id).
/// </summary>
internal static class SequenceCheck
{
    public static void Run(GraphModel graph, int? sequence, List<ValidationError> errors)
    {
        if (sequence is not int seq) return; // вызван без sequence — пропускаем.

        var maxId = 0;
        foreach (var n in graph.ById.Values)
            if (n.Id > maxId) maxId = n.Id;
        if (seq < maxId)
        {
            errors.Add(new ValidationError(
                "sequence_underflow",
                $"sequence={seq}, но в графе встречается id={maxId} (>{seq}); следующий ReserveId выдаст уже занятый id.",
                FilePath: ".docswalker/sequence.txt",
                Hint: $"Запиши в .docswalker/sequence.txt значение ≥ {maxId}; обычно это устраняется однократной правкой файла."));
        }
    }
}
