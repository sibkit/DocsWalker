using System.Text.Json.Serialization;

namespace DocsWalker.Core.Server.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HandshakeRequest))]
[JsonSerializable(typeof(HandshakeResponse))]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
[JsonSerializable(typeof(string[]))]
public partial class ProtocolJsonContext : JsonSerializerContext
{
}
