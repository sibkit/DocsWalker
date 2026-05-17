using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DocsWalker.Core.Storage;

/// <summary>
/// Управляет подключением к SQLite-БД DocsWalker: connection string,
/// PRAGMA, custom-функция regex_match и bootstrap схемы по
/// database-model/schema.md.
/// </summary>
public sealed class SqliteStore
{
    public static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromMilliseconds(500);

    private readonly string _connectionString;
    private readonly TimeSpan _regexTimeout;

    public SqliteStore(string connectionString, TimeSpan? regexTimeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        _connectionString = connectionString;
        _regexTimeout = regexTimeout ?? DefaultRegexTimeout;
    }

    public string ConnectionString => _connectionString;

    public TimeSpan RegexTimeout => _regexTimeout;

    /// <summary>
    /// Открывает file-based SQLite-БД по абсолютному пути. Файл создаётся
    /// при первом подключении (Microsoft.Data.Sqlite default).
    /// </summary>
    public static SqliteStore ForFile(string filePath, TimeSpan? regexTimeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        return new SqliteStore(csb.ToString(), regexTimeout);
    }

    /// <summary>
    /// Создаёт shared in-memory-БД с указанным именем. Все коннекты с тем
    /// же `name` внутри процесса видят один in-memory DB, пока хотя бы
    /// один открыт. Используется в тестах.
    /// </summary>
    public static SqliteStore ForSharedInMemory(string name, TimeSpan? regexTimeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = name,
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
        };
        return new SqliteStore(csb.ToString(), regexTimeout);
    }

    /// <summary>
    /// Открывает новое соединение, применяет PRAGMA и регистрирует
    /// regex_match. Caller обязан Dispose возвращённый коннект.
    /// </summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ApplyPragmas(connection);
        RegisterRegexMatch(connection, _regexTimeout);
        return connection;
    }

    /// <summary>
    /// Однократно открывает соединение и применяет DDL. Идемпотентно
    /// (`CREATE TABLE/INDEX IF NOT EXISTS`).
    /// </summary>
    public void EnsureBootstrapped()
    {
        using var connection = Open();
        Bootstrap(connection);
    }

    /// <summary>
    /// Применяет DDL схемы к уже открытому соединению. Идемпотентно.
    /// Не применяет PRAGMA и не регистрирует функции — это делает
    /// <see cref="Open"/>.
    /// </summary>
    public static void Bootstrap(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SchemaDdl;
        cmd.ExecuteNonQuery();
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA case_sensitive_like = ON;
            """;
        cmd.ExecuteNonQuery();
    }

    private static void RegisterRegexMatch(SqliteConnection connection, TimeSpan timeout)
    {
        connection.CreateFunction<string?, string?, long, long?>(
            "regex_match",
            (text, pattern, caseSensitive) =>
            {
                if (text is null || pattern is null)
                {
                    return null;
                }
                var options = RegexOptions.CultureInvariant;
                if (caseSensitive == 0)
                {
                    options |= RegexOptions.IgnoreCase;
                }
                var regex = new Regex(pattern, options, timeout);
                return regex.IsMatch(text) ? 1L : 0L;
            },
            isDeterministic: false);
    }

    private const string SchemaDdl = """
        CREATE TABLE IF NOT EXISTS graph (
          name TEXT PRIMARY KEY
        );

        CREATE TABLE IF NOT EXISTS sequence (
          graph_name TEXT PRIMARY KEY,
          next_id INTEGER NOT NULL,
          FOREIGN KEY (graph_name) REFERENCES graph(name)
        );

        CREATE TABLE IF NOT EXISTS node (
          graph_name TEXT NOT NULL,
          id         TEXT NOT NULL,
          scope      TEXT NOT NULL CHECK (scope IN ('main', 'usage', 'scheme')),
          path       TEXT NOT NULL,
          title      TEXT NOT NULL,
          content    TEXT NOT NULL DEFAULT '',
          version    INTEGER NOT NULL DEFAULT 1,
          PRIMARY KEY (graph_name, id),
          FOREIGN KEY (graph_name) REFERENCES graph(name)
        );

        CREATE INDEX IF NOT EXISTS node_path
          ON node (graph_name, scope, path);

        CREATE UNIQUE INDEX IF NOT EXISTS node_path_lower
          ON node (graph_name, scope, LOWER(path));

        CREATE INDEX IF NOT EXISTS node_scope
          ON node (graph_name, scope);

        CREATE TABLE IF NOT EXISTS node_map_binding (
          graph_name  TEXT NOT NULL,
          node_id     TEXT NOT NULL,
          map_name    TEXT NOT NULL,
          branch_path TEXT NOT NULL,
          PRIMARY KEY (graph_name, node_id, map_name),
          FOREIGN KEY (graph_name, node_id)
            REFERENCES node(graph_name, id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS node_map_binding_by_map
          ON node_map_binding (graph_name, map_name, branch_path);

        CREATE TABLE IF NOT EXISTS link (
          graph_name TEXT NOT NULL,
          name       TEXT NOT NULL,
          from_id    TEXT NOT NULL,
          to_id      TEXT NOT NULL,
          PRIMARY KEY (graph_name, name, from_id, to_id),
          FOREIGN KEY (graph_name, from_id)
            REFERENCES node(graph_name, id),
          FOREIGN KEY (graph_name, to_id)
            REFERENCES node(graph_name, id)
        );

        CREATE INDEX IF NOT EXISTS link_by_from
          ON link (graph_name, from_id, name);

        CREATE INDEX IF NOT EXISTS link_by_to
          ON link (graph_name, to_id, name);

        CREATE TABLE IF NOT EXISTS tx_event (
          graph_name    TEXT NOT NULL,
          id            TEXT NOT NULL,
          title         TEXT NOT NULL,
          date          TEXT NOT NULL,
          description   TEXT,
          rollback_of   TEXT,
          tx_scope      TEXT NOT NULL CHECK (tx_scope IN ('main', 'usage', 'scheme')),
          ordinal       INTEGER NOT NULL,
          sections_json TEXT NOT NULL,
          PRIMARY KEY (graph_name, id),
          FOREIGN KEY (graph_name) REFERENCES graph(name),
          FOREIGN KEY (graph_name, rollback_of)
            REFERENCES tx_event(graph_name, id)
        );

        CREATE INDEX IF NOT EXISTS tx_event_date
          ON tx_event (graph_name, date, ordinal);

        CREATE INDEX IF NOT EXISTS tx_event_rollback_of
          ON tx_event (graph_name, rollback_of);

        CREATE INDEX IF NOT EXISTS tx_event_tx_scope
          ON tx_event (graph_name, tx_scope);

        CREATE UNIQUE INDEX IF NOT EXISTS tx_event_date_ordinal
          ON tx_event (graph_name, date, ordinal);

        CREATE TABLE IF NOT EXISTS tx_touches_node (
          graph_name TEXT NOT NULL,
          tx_id      TEXT NOT NULL,
          node_id    TEXT NOT NULL,
          role       TEXT NOT NULL CHECK (role IN ('created', 'changed', 'deleted')),
          PRIMARY KEY (graph_name, tx_id, node_id, role),
          FOREIGN KEY (graph_name, tx_id)
            REFERENCES tx_event(graph_name, id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS tx_touches_node_by_node
          ON tx_touches_node (graph_name, node_id);

        CREATE TABLE IF NOT EXISTS tx_touches_link (
          graph_name TEXT NOT NULL,
          tx_id      TEXT NOT NULL,
          link_name  TEXT NOT NULL,
          from_id    TEXT NOT NULL,
          to_id      TEXT NOT NULL,
          role       TEXT NOT NULL CHECK (role IN ('created', 'deleted')),
          PRIMARY KEY (graph_name, tx_id, link_name, from_id, to_id, role),
          FOREIGN KEY (graph_name, tx_id)
            REFERENCES tx_event(graph_name, id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS tx_touches_link_by_link
          ON tx_touches_link (graph_name, link_name, from_id, to_id);
        """;
}
