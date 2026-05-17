namespace DocsWalker.Core.Validation;

/// <summary>
/// Структурированная ошибка валидации (см. docs/DocsWalker.yml/«Контракт валидации»).
/// Code — стабильный машинный код, пригодный для сопоставления в тестах и в CLI;
/// Message — человекочитаемое описание; FilePath/NodeId/Path — координаты места ошибки,
/// если применимо. Hint — короткая подсказка LLM-агенту «как починить»: имя команды,
/// рекомендуемая последовательность вызовов или ссылка на инвариант. null означает,
/// что осмысленной подсказки нет; CLI/MCP должны опускать поле в JSON.
/// </summary>
public sealed record ValidationError(
    string Code,
    string Message,
    string? FilePath = null,
    int? NodeId = null,
    string? Path = null,
    string? Hint = null,
    string? RefName = null);

/// <summary>
/// Результат прогона валидатора. <see cref="IsValid"/> — короткий ответ, нужен ли
/// откат записи; <see cref="Errors"/> — полный список найденных нарушений.
/// </summary>
public sealed class ValidationResult
{
    public IReadOnlyList<ValidationError> Errors { get; }
    public bool IsValid => Errors.Count == 0;

    public ValidationResult(IReadOnlyList<ValidationError> errors)
    {
        Errors = errors;
    }
}
