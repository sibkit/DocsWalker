namespace DocsWalker.Core.Server.Protocol;

public static class ProtocolVersion
{
    // 2: добавлено опциональное поле session_id в request-frame (stg-0005).
    public const string Current = "2";
}
