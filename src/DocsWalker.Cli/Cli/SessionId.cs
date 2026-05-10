namespace DocsWalker.Cli.Cli;

/// <summary>
/// Резолв <c>session_id</c> для CLI-вызова: явный <c>--session-id=&lt;uuid&gt;</c> в argv
/// перебивает env-переменную <c>CLAUDE_CODE_SESSION_ID</c>; оба пустые → null
/// (сервер не ведёт seen-set для этого запроса).
/// <para>
/// Контракт — docs/DocsWalker.yml #342.
/// </para>
/// </summary>
internal static class SessionId
{
    public const string EnvVar = "CLAUDE_CODE_SESSION_ID";
    public const string FlagPrefix = "--session-id=";

    public static string? Resolve(string[] argv)
    {
        foreach (var token in argv)
        {
            if (token.StartsWith(FlagPrefix, StringComparison.Ordinal))
            {
                var value = token[FlagPrefix.Length..];
                return value.Length == 0 ? null : value;
            }
        }
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        return string.IsNullOrEmpty(fromEnv) ? null : fromEnv;
    }
}
