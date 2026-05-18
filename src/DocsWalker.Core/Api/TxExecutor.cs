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
/// После применения ops (до записи hist) запускается
/// <see cref="SchemeValidator"/>:
/// <list type="bullet">
/// <item>Для <c>tx scope=scheme</c> — breaking-change-check существующих
/// main/usage узлов (per api/scheme-scope.md, раздел
/// «Breaking-change-check») с кодом <c>schema_breaks_existing_data</c>.</item>
/// <item>Для <c>tx scope=main</c> и <c>scope=usage</c> —
/// data-against-scheme проверка (per api/errors.md, коды
/// <c>unknown_map</c>, <c>unknown_link</c> через
/// <c>validation_failed</c>) при наличии непустой схемы.</item>
/// </list>
///
/// Текущие ограничения v2 (полная семантика — см. api/ + database-model/):
/// <list type="bullet">
/// <item>Лимит 100 токенов на <c>tx.title</c> — deferred (нет токенайзера).</item>
/// <item>Path normalization с <c>defaults.path_parent</c> — применяется
/// только к <c>create.path</c> и <c>move.to.parent_path</c>; вложенные
/// <c>selector.path</c> в bulk-op-ах префиксом не дополняются (на v2
/// LLM передаёт абсолютный path).</item>
/// <item>В scheme-валидаторе не реализованы <c>cardinality</c> и
/// <c>required_when</c>; есть только <c>branch</c>/<c>required</c> для
/// maps и <c>from/to.map_bindings</c>/<c>required_for</c> для links.</item>
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
            for (int i = 0; i < request.Ops.Count; i++)
            {
                var op = request.Ops[i];
                ctx.OpIndex = i;
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
            // Schema validation: до записи hist, чтобы schema_breaks /
            // validation_failed откатывали tx без оседающего event-узла.
            // Rollback-tx наследует scope исходной — используем txScopeOverride.
            var effectiveScope = txScopeOverride is { } over
                ? (over switch
                {
                    ScopeNames.Main => Scope.Main,
                    ScopeNames.Usage => Scope.Usage,
                    ScopeNames.Scheme => Scope.Scheme,
                    _ => request.Scope,
                })
                : request.Scope;
            if (effectiveScope == Scope.Scheme)
            {
                SchemeValidator.ValidateSchemeTx(_connection, tx, _graphName);
            }
            else
            {
                var touchedIds = CollectTouchedNodeIds(ctx);
                SchemeValidator.ValidateDataTx(_connection, tx, _graphName, effectiveScope,
                    touchedIds, ctx.CreatedLinks);
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

    private static HashSet<string> CollectTouchedNodeIds(TxContext ctx)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in ctx.CreatedNodes) set.Add(c.Id);
        foreach (var k in ctx.ChangedNodes.Keys) set.Add(k);
        return set;
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
