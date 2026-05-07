namespace DocsWalker.Core.Server.Protocol;

public sealed record HandshakeRequest(string ClientVersion, string ProtocolVersion);

public sealed record HandshakeResponse(string ServerVersion, bool Accepted, string? Reason = null);
