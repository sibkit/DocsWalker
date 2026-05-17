namespace DocsWalker.Core.Graph;

/// <summary>
/// Ошибка загрузки in-memory графа из docs/. Несёт код, путь к файлу и (если применимо)
/// человекочитаемый путь к месту в графе.
/// </summary>
public sealed class GraphLoadException : Exception
{
    public string Code { get; }
    public string? FilePath { get; }
    public string? NodePath { get; }

    public GraphLoadException(string code, string? filePath, string message, string? nodePath = null)
        : base(message)
    {
        Code = code;
        FilePath = filePath;
        NodePath = nodePath;
    }
}
