namespace DocsWalker.Cli.Cli;

/// <summary>
/// Преобразование сырого FS-пути YAML-файла (как его видит ядро в <c>SourceFile</c>
/// или в исключениях вроде <c>SchemaLoadException.FilePath</c>) в FS-агностичный
/// идентификатор документа для LLM: title документа без расширения и без префиксов
/// каталогов (см. правило #277 «LLM не видит файлы»).
///
/// Превращения:
/// <list type="bullet">
///   <item>абсолютный или относительный путь приводится к виду <c>docs/...</c> (или ниже);</item>
///   <item>если первый сегмент — <c>docs</c>, он отбрасывается;</item>
///   <item>если первый оставшийся сегмент — <c>.docswalker</c>, путь объявлен служебным
///         и метод возвращает <c>null</c> (LLM этих файлов не знает);</item>
///   <item>расширение <c>.yml</c>/<c>.yaml</c> обрезается;</item>
///   <item>обратные слеши Windows нормализуются на прямые.</item>
/// </list>
/// </summary>
internal static class DocumentPath
{
    public static string? NormalizeForLlm(string? rawPath)
    {
        if (string.IsNullOrEmpty(rawPath)) return null;

        var s = rawPath.Replace('\\', '/');

        // Срезаем всё до и включая первый сегмент "docs/", если такой есть.
        // Если "docs/" нет — путь, возможно, относительный изнутри docs (например, "DocsWalker.yml") —
        // оставляем как есть.
        var docsIdx = s.IndexOf("/docs/", StringComparison.Ordinal);
        if (docsIdx >= 0)
        {
            s = s.Substring(docsIdx + "/docs/".Length);
        }
        else if (s.StartsWith("docs/", StringComparison.Ordinal))
        {
            s = s.Substring("docs/".Length);
        }

        // Служебная папка — наружу не торчим.
        if (s.StartsWith(".docswalker/", StringComparison.Ordinal) || s == ".docswalker")
            return null;

        // Обрезаем расширение.
        const string YmlExt = ".yml";
        const string YamlExt = ".yaml";
        if (s.EndsWith(YmlExt, StringComparison.OrdinalIgnoreCase))
            s = s.Substring(0, s.Length - YmlExt.Length);
        else if (s.EndsWith(YamlExt, StringComparison.OrdinalIgnoreCase))
            s = s.Substring(0, s.Length - YamlExt.Length);

        return s.Length == 0 ? null : s;
    }
}
