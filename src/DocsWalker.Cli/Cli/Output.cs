using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DocsWalker.Cli.Cli;

internal sealed class SuccessEnvelope
{
    public bool Ok { get; init; } = true;
    public JsonNode? Result { get; init; }
}

internal sealed class ErrorEnvelope
{
    public bool Ok { get; init; } = false;
    public required ErrorBody Error { get; init; }
}

internal sealed class ErrorBody
{
    public required string Code { get; init; }
    public string? Path { get; init; }
    public required string Message { get; init; }
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
    public static void WriteSuccess(JsonNode? result)
    {
        var envelope = new SuccessEnvelope { Result = result };
        var json = JsonSerializer.Serialize(envelope, CliJsonContext.Default.SuccessEnvelope);
        Console.Out.WriteLine(json);
    }

    public static void WriteError(string code, string? path, string message)
    {
        var envelope = new ErrorEnvelope
        {
            Error = new ErrorBody
            {
                Code = code,
                Path = path,
                Message = message,
            },
        };
        var json = JsonSerializer.Serialize(envelope, CliJsonContext.Default.ErrorEnvelope);
        Console.Error.WriteLine(json);
    }
}
