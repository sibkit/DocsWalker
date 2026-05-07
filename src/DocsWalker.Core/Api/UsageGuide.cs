namespace DocsWalker.Core.Api;

/// <summary>
/// Источник данных для команды <c>get-usage-guide</c>: ментальная модель и manifest
/// CLI-команд. Реализуется CLI-слоем (имена и параметры команд знает только он),
/// внедряется в <see cref="ReadApi.GetUsageGuide"/> при сборке ответа. Ядро само
/// о CLI ничего не знает — это позволяет переиспользовать GetUsageGuide и в MCP-сервере
/// со своим набором tool-описаний.
/// </summary>
public interface IUsageGuideSource
{
    /// <summary>
    /// Краткая выжимка ментальной модели для LLM (≤30 строк): граф, tree-scopes,
    /// порядок работы, главные запреты. Полная версия — в docs/DocsWalker.yml.
    /// </summary>
    string GetMentalModel();

    /// <summary>
    /// Manifest всех команд: имя, kind, параметры, примеры. Порядок — стабильный
    /// (как объявлено в источнике), чтобы LLM могла стабильно ссылаться на команды.
    /// </summary>
    IReadOnlyList<UsageGuideCommand> GetCommands();
}

/// <summary>
/// Описание одной команды в manifest'е. <see cref="Kind"/> — строка <c>"read"</c>
/// или <c>"write"</c> (а не enum, чтобы DTO был стабилен по содержимому JSON).
/// </summary>
public sealed record UsageGuideCommand(
    string Name,
    string Kind,
    string? Description,
    IReadOnlyList<UsageGuideParameter> Parameters,
    IReadOnlyList<string> Examples);

public sealed record UsageGuideParameter(
    string Name,
    string Type,
    bool Required,
    string? Description);

/// <summary>
/// Слепок состояния графа на момент вызова <c>get-usage-guide</c>: суммарное число
/// узлов, прямые дети root (для быстрой ориентации в составе docs/) и количество
/// типов в Схеме. Не содержит ни text узлов, ни связей — это manifest-данные,
/// не контент.
/// </summary>
public sealed record GraphSnapshot(
    int TotalNodes,
    IReadOnlyList<RootChild> RootChildren,
    int SchemaTypesCount);

public sealed record RootChild(int Id, string Type, string Title);

/// <summary>
/// Полный ответ <c>get-usage-guide</c>: ментальная модель + декларация деревьев +
/// manifest команд + слепок графа. LLM-агент дёргает один раз в начале сессии и
/// получает всё необходимое, чтобы не блуждать по docs/ и не угадывать имена команд.
/// </summary>
public sealed record UsageGuideResponse(
    string MentalModel,
    IReadOnlyList<DocsWalker.Core.Schema.TreeDefinition> Trees,
    IReadOnlyList<UsageGuideCommand> Commands,
    GraphSnapshot Snapshot);
