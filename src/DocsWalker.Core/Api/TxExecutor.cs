using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DocsWalker.Core.Api;

/// <summary>
/// Исполнитель метода <c>tx</c> (per api/tx.md). Атомарно применяет
/// массив ops к выбранному editable scope (<c>main</c>/<c>usage</c>/
/// <c>scheme</c>), пишет event-узел <c>hist/transaction</c> с тремя
/// секциями + строки в <c>tx_touches_*</c> индексные таблицы.
/// Все DML внутри одной SQLite-write-транзакции <c>BEGIN IMMEDIATE</c>;
/// любая ошибка → <c>ROLLBACK</c>, hist остаётся пустой.
///
/// Текущие ограничения v2 (полная семантика — см. api/ + database-model/):
/// <list type="bullet">
/// <item>Schema-уровневые проверки (<c>unknown_map</c>,
/// <c>unknown_link</c>, <c>validation_failed</c>,
/// <c>schema_breaks_existing_data</c>) — deferred to step 7 (scheme scope).</item>
/// <item>Лимит 100 токенов на <c>tx.title</c> — deferred (нет токенайзера).</item>
/// <item>Path normalization с <c>defaults.path_parent</c> — применяется
/// только к <c>create.path</c> и <c>move.to.parent_path</c>; вложенные
/// <c>selector.path</c> в bulk-op-ах префиксом не дополняются (на v2
/// LLM передаёт абсолютный path).</item>
/// </list>
/// </summary>
public sealed class TxExecutor
{
    private readonly SqliteConnection _connection;
    private readonly string _graphName;
    private readonly Func<DateTime> _utcNow;

    public TxExecutor(SqliteConnection connection, string graphName, Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(graphName);
        _connection = connection;
        _graphName = graphName;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public TxResponse Execute(TxRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Scope == Scope.Hist)
        {
            throw new ApiException(ApiErrorCodes.HistReadOnly);
        }

        using var tx = _connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        try
        {
            var ctx = new TxContext(_connection, tx, _graphName, request.Scope, _utcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), request.Defaults);
            var opResults = new List<TxOpResponse>(request.Ops.Count);
            string? txScopeOverride = null;
            foreach (var op in request.Ops)
            {
                if (op is RollbackOp rb)
                {
                    var (rbResult, rbScope) = TxRollback.Apply(ctx, rb);
                    opResults.Add(rbResult);
                    txScopeOverride = rbScope;
                }
                else
                {
                    opResults.Add(ApplyOp(ctx, op));
                }
            }
            var txId = ctx.WriteHist(request.Title, request.Description, txScopeOverride);
            tx.Commit();
            return new TxResponse(txId, opResults);
        }
        catch
        {
            try { tx.Rollback(); } catch { /* ignore */ }
            throw;
        }
    }

    private static TxOpResponse ApplyOp(TxContext ctx, TxOp op)
    {
        return op switch
        {
            CreateOp c => TxOps.Create(ctx, c),
            UpdateOp u => TxOps.Update(ctx, u),
            MoveOp m => TxOps.Move(ctx, m),
            DeleteOp d => TxOps.Delete(ctx, d),
            LinkOp l => TxOps.Link(ctx, l),
            UnlinkOp u => TxOps.Unlink(ctx, u),
            _ => throw new InvalidOperationException($"Unknown TxOp {op.GetType().Name}"),
        };
    }
}
