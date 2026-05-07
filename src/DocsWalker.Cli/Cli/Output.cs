using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DocsWalker.Cli.Cli;

internal sealed class SuccessEnvelope
{
    public bool Ok { get; init; } = true;
    /// <summary>
    /// Только для write-команд: <c>true</c> — изменения записаны на FS;
    /// <c>false</c> — это был dry-run (валидация прошла, файлы не изменены).
    /// У read-команд поле остаётся <c>null</c> и не сериализуется (см.
    /// <see cref="JsonIgnoreCondition.WhenWritingNull"/> в контексте сериализации).
    /// </summary>
    public bool? Applied { get; init; }
    public JsonNode? Result { get; init; }
}

internal sealed class ErrorEnvelope
{
    public bool Ok { get; init; } = false;
    public required ErrorBody Error { get; init; }
}

/// <summary>
/// Тело ошибки CLI. <see cref="Path"/> — title документа без расширения и FS-префикса
/// (или null, если ошибка не связана с конкретным документом или файл — служебный из
/// <c>.docswalker/</c>); сырые FS-пути LLM не выдаём (см. docs/«LLM не видит файлы»).
/// <see cref="DescribeType"/> — встроенный ответ <c>describe-type</c> для типа,
/// выводимого из аргументов write-команды; присутствует только при missing/invalid
/// параметрах, когда тип удалось определить.
/// </summary>
internal sealed class ErrorBody
{
    public required string Code { get; init; }
    public string? Path { get; init; }
    public required string Message { get; init; }
    public string? Hint { get; init; }
    public JsonNode? DescribeType { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SuccessEnvelope))]
[JsonSerializable(typeof(ErrorEnvelope))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(JsonValue))]
internal partial class CliJsonContext : JsonSerializerContext
{
}

internal static class Output
{
    // Кодировщик пропускает не-ASCII символы (включая кириллицу) без \uXXXX-escape:
    // CLI-stdout не HTML-контекст, экранирование «<», «>», «&», «'» здесь только засоряет
    // вывод и удваивает токены при потреблении LLM-ом.
    private static readonly JsonSerializerOptions Options = new(CliJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly CliJsonContext Ctx = new(Options);

    public static void WriteSuccess(JsonNode? result)
    {
        var envelope = new SuccessEnvelope { Result = result };
        var json = JsonSerializer.Serialize(envelope, Ctx.SuccessEnvelope);
        Console.Out.WriteLine(json);
    }

    /// <summary>
    /// Перегрузка для write-команд: добавляет поле <c>applied</c> в success-конверт
    /// (true — реально записано, false — dry-run).
    /// </summary>
    public static void WriteSuccess(JsonNode? result, bool applied)
    {
        var envelope = new SuccessEnvelope { Applied = applied, Result = result };
        var json = JsonSerializer.Serialize(envelope, Ctx.SuccessEnvelope);
        Console.Out.WriteLine(json);
    }

    /// <summary>
    /// Печатает ошибку в stderr. <paramref name="path"/> — сырой путь к YAML-файлу
    /// (или null, если ошибка не связана с конкретным документом); метод сам обрезает
    /// расширение и FS-префикс через <see cref="DocumentPath.NormalizeForLlm"/>, чтобы
    /// LLM не видела имена файлов (правило #277).
    /// <paramref name="describeType"/> — опциональный встроенный ответ describe-type
    /// для типа из контекста (см. <see cref="ErrorEnrichment"/>).
    /// </summary>
    public static void WriteError(string code, string? path, string message, string? hint = null, JsonNode? describeType = null)
    {
        var envelope = new ErrorEnvelope
        {
            Error = new ErrorBody
            {
                Code = code,
                Path = DocumentPath.NormalizeForLlm(path),
                Message = message,
                Hint = hint,
                DescribeType = describeType,
            },
        };
        var json = JsonSerializer.Serialize(envelope, Ctx.ErrorEnvelope);
        Console.Error.WriteLine(json);
    }
}
