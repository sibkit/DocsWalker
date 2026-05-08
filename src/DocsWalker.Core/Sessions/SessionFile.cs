using System.Globalization;
using System.Text;
using DocsWalker.Core.Store;
using DocsWalker.Core.Yaml;
using SharpYaml.Events;

namespace DocsWalker.Core.Sessions;

/// <summary>
/// Persistence одной LLM-сессии в YAML-файл
/// <c>docs/.docswalker/sessions/&lt;uuid&gt;.yml</c>. Формат фиксирован
/// в спецификации (<c>docs/DocsWalker.yml</c> §Контекст-aware-выдача,
/// rule «Persistence сессий»):
/// <code>
/// created: 2026-05-09T10:00:00Z
/// last_used: 2026-05-09T10:30:00Z
/// seen: [12, 34, 56]
/// </code>
/// Папку создаём лениво при первой записи; повреждённые/посторонние файлы
/// в этой папке молча игнорируем — папка служебная.
/// </summary>
public static class SessionFile
{
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";

    /// <summary>
    /// Загрузить все session-файлы из <paramref name="sessionsDir"/>.
    /// Не выполняет TTL-эвикцию — её вызовом является
    /// <see cref="SessionState.EvictExpired"/>; так separation остаётся чистым:
    /// файл «знает» только формат, state — только модель.
    /// Если папки нет — возвращает пустой <see cref="SessionState"/>.
    /// </summary>
    public static SessionState LoadAll(string sessionsDir)
    {
        var state = new SessionState();
        if (!Directory.Exists(sessionsDir)) return state;

        foreach (var path in Directory.EnumerateFiles(sessionsDir, "*.yml"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(name, "D", out var sessionId)) continue;

            SeenSet? set;
            try { set = ReadFile(path); }
            catch { set = null; }
            if (set is null) continue;

            state.RestoreSession(sessionId, set);
        }
        return state;
    }

    /// <summary>
    /// Записать все dirty-сессии атомарной пачкой. После успешной записи
    /// dirty снимается. Папка создаётся лениво. Если dirty-сессий нет —
    /// никаких I/O.
    /// </summary>
    public static void SaveAll(SessionState state, string sessionsDir)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(sessionsDir);

        var targets = new List<AtomicWriteTarget>();
        foreach (var (id, set) in state.Sessions)
        {
            if (!set.Dirty) continue;
            var path = Path.Combine(sessionsDir, id.ToString("D") + ".yml");
            targets.Add(new AtomicWriteTarget(path, EmitSession(set)));
        }
        if (targets.Count == 0) return;

        Directory.CreateDirectory(sessionsDir);
        AtomicWriter.WriteAll(targets);

        foreach (var (_, set) in state.Sessions)
        {
            if (set.Dirty) set.Dirty = false;
        }
    }

    /// <summary>
    /// Удалить файлы сессий <paramref name="sessionIds"/>. Best-effort:
    /// отсутствующий файл не считается ошибкой (другой инстанс мог уже
    /// убрать), любая иная ошибка ввода-вывода молча игнорируется.
    /// </summary>
    public static void DeleteSessions(string sessionsDir, IEnumerable<Guid> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        if (!Directory.Exists(sessionsDir)) return;
        foreach (var id in sessionIds)
        {
            var path = Path.Combine(sessionsDir, id.ToString("D") + ".yml");
            try { File.Delete(path); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Стереть все session-файлы. Применяется при mismatch checksum
    /// (внешняя ручная правка docs/) — все seen-state считаются протухшими.
    /// Сам каталог и <c>.checksum</c> не трогаются.
    /// </summary>
    public static void WipeAll(string sessionsDir)
    {
        if (!Directory.Exists(sessionsDir)) return;
        foreach (var path in Directory.EnumerateFiles(sessionsDir, "*.yml"))
        {
            try { File.Delete(path); }
            catch { /* best-effort */ }
        }
    }

    // ── формат ──────────────────────────────────────────────────────────────

    private static string EmitSession(SeenSet set)
    {
        var sb = new StringBuilder();
        sb.Append("created: ").Append(FormatTimestamp(set.Created)).Append('\n');
        sb.Append("last_used: ").Append(FormatTimestamp(set.LastUsed)).Append('\n');
        if (set.Ids.Count == 0)
        {
            sb.Append("seen: []\n");
            return sb.ToString();
        }
        var sorted = set.Ids.OrderBy(x => x);
        sb.Append("seen: [")
          .Append(string.Join(", ", sorted.Select(x => x.ToString(CultureInfo.InvariantCulture))))
          .Append("]\n");
        return sb.ToString();
    }

    private static string FormatTimestamp(DateTime dt)
    {
        var utc = dt.Kind switch
        {
            DateTimeKind.Utc         => dt,
            DateTimeKind.Local       => dt.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            _                        => dt.ToUniversalTime(),
        };
        return utc.ToString(TimestampFormat, CultureInfo.InvariantCulture);
    }

    private static SeenSet? ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var yr = new YamlReader(reader, path);

        if (yr.Next() is not StreamStart) return null;
        if (yr.Next() is not DocumentStart) return null;
        if (yr.Next() is not MappingStart) return null;

        DateTime? created = null;
        DateTime? lastUsed = null;
        var ids = new HashSet<int>();

        while (yr.Peek() is not MappingEnd and not null)
        {
            var key = yr.NextScalarValue();
            switch (key)
            {
                case "created":
                    created = ParseTimestamp(yr.NextScalarValue());
                    break;
                case "last_used":
                    lastUsed = ParseTimestamp(yr.NextScalarValue());
                    break;
                case "seen":
                    ReadIntSequence(yr, ids);
                    break;
                default:
                    yr.SkipValue();
                    break;
            }
        }

        if (created is null || lastUsed is null) return null;
        return new SeenSet(ids, created.Value, lastUsed.Value);
    }

    private static void ReadIntSequence(YamlReader yr, HashSet<int> ids)
    {
        if (yr.Next() is not SequenceStart) return;
        while (yr.Peek() is not SequenceEnd and not null)
        {
            var s = yr.NextScalarValue();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                ids.Add(n);
        }
        yr.Next(); // SequenceEnd
    }

    private static DateTime? ParseTimestamp(string s)
    {
        if (DateTime.TryParseExact(
                s,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }
        return null;
    }
}
