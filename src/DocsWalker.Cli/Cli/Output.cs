using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DocsWalker.Cli.Cli;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(JsonValue))]
internal partial class CliJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Контракт вывода CLI envelope-free: успех — exit 0, stdout — JSON-результат
/// команды напрямую (без обёртки <c>{ok,result,…}</c>); ошибка — exit ≠ 0,
/// stderr — плоский JSON <c>{code, message, path?, hint?, describe_type?}</c>.
/// Дискриминатор — exit-code и поток.
/// </summary>
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
        // Read- и Schema-handler'ы возвращают не-null результат; null-ветка — защита от
        // регрессии: пустой объект {} парсится LLM проще, чем литерал null.
        var node = result ?? new JsonObject();
        var json = JsonSerializer.Serialize(node, Ctx.JsonNode);
        Console.Out.WriteLine(json);
    }

    /// <summary>
    /// Перегрузка для одиночных write-команд: подмешивает поле <c>applied</c> в сам
    /// result-объект (true — реально записано, false — dry-run). Требует
    /// <see cref="JsonObject"/>: applied — top-level-поле результата, для массива/скаляра
    /// места нет. Для <c>transaction</c>, у которой результат — top-level массив, эта
    /// перегрузка не используется: handler сам впечатывает <c>applied</c> в каждый
    /// элемент массива и зовёт single-arg <see cref="WriteSuccess(JsonNode?)"/>.
    /// </summary>
    public static void WriteSuccess(JsonNode? result, bool applied)
    {
        if (result is not JsonObject obj)
            throw new InvalidOperationException(
                "Одиночная write-команда должна возвращать JsonObject — applied подмешивается top-level.");

        obj["applied"] = JsonValue.Create(applied);
        var json = JsonSerializer.Serialize(obj, Ctx.JsonObject);
        Console.Out.WriteLine(json);
    }

    /// <summary>
    /// Печатает ошибку в stderr плоским объектом без обёртки <c>error: {…}</c>.
    /// <paramref name="path"/> — сырой путь к YAML-файлу (или null, если ошибка не
    /// связана с конкретным документом); метод сам обрезает расширение и FS-префикс
    /// через <see cref="DocumentPath.NormalizeForLlm"/>, чтобы LLM не видела имена
    /// файлов (правило #277). <paramref name="describeType"/> — опциональный
    /// встроенный ответ describe-type для типа из контекста (см. <see cref="ErrorEnrichment"/>).
    /// </summary>
    public static void WriteError(string code, string? path, string message, string? hint = null, JsonNode? describeType = null)
    {
        var obj = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        };

        var normalizedPath = DocumentPath.NormalizeForLlm(path);
        if (normalizedPath is not null)
            obj["path"] = normalizedPath;
        if (hint is not null)
            obj["hint"] = hint;
        if (describeType is not null)
            obj["describe_type"] = describeType.DeepClone();

        var json = JsonSerializer.Serialize(obj, Ctx.JsonObject);
        Console.Error.WriteLine(json);
    }
}
