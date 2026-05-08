using DocsWalker.Cli.Cli.Handlers;
using DocsWalker.Core.Api;
using DocsWalker.Core.Server;
using DocsWalker.Core.Sessions;

namespace DocsWalker.Tests;

/// <summary>
/// Поведение write-invalidation (#358): после успешного <see cref="WriteApi.Apply"/>
/// touched id должны исчезнуть из seen-set всех активных sessions, чтобы LLM
/// перестал получать placeholder для изменённого узла.
/// </summary>
public class WriteInvalidationTests
{
    private static readonly DateTime Now = new(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Узел из <c>docs/DocsWalker.yml</c>, по которому копия в WriteTestEnvironment
    /// гарантированно содержит обновляемый контент. Проверочная константа: если
    /// номер сместится — тест явно укажет источник проблемы.
    /// </summary>
    private const int TargetStatementId = 123;

    [Fact]
    public void Apply_UpdateNode_TouchedIdsContainsUpdatedId()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromRoot(env.Root));

        var op = new UpdateNodeOp(Id: TargetStatementId, NewTitle: null, NewText: "Изменённый текст");
        var result = api.ApplyOne(op);

        Assert.True(result.Applied);
        Assert.Contains(TargetStatementId, result.TouchedIds);
    }

    [Fact]
    public void Apply_DryRun_TouchedIdsStillPopulated()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromRoot(env.Root));

        var op = new UpdateNodeOp(Id: TargetStatementId, NewTitle: null, NewText: "Только проверка");
        var result = api.ApplyOne(op, dryRun: true);

        // Dry-run: на FS ничего не пишем, но touched id даём — пригождается тестам и
        // проверочным сценариям; реальный invalidation не делаем (в RunCore проверка
        // result.Applied перед вызовом RemoveFromAll).
        Assert.False(result.Applied);
        Assert.Contains(TargetStatementId, result.TouchedIds);
    }

    [Fact]
    public void WriteHandlers_UpdateNode_RemovesTouchedFromSeenSet()
    {
        using var env = new WriteTestEnvironment();

        var sessions = new SessionState();
        var sid = Guid.NewGuid();
        // 123 уже в seen, плюс посторонний 999 — должен остаться нетронутым.
        sessions.MarkSeen(sid, new[] { TargetStatementId, 999 }, Now);

        var args = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = TargetStatementId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["text"] = "Текст после правки",
        };

        var prevOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        int exitCode;
        try
        {
            using (RequestContext.Push(sid.ToString(), sessions))
            {
                exitCode = WriteHandlers.UpdateNode(env.Root, args, dryRun: false);
            }
        }
        finally
        {
            Console.SetOut(prevOut);
        }

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(TargetStatementId, sessions.Sessions[sid].Ids);
        Assert.Contains(999, sessions.Sessions[sid].Ids);
    }

    [Fact]
    public void WriteHandlers_UpdateNode_DryRun_DoesNotInvalidate()
    {
        using var env = new WriteTestEnvironment();

        var sessions = new SessionState();
        var sid = Guid.NewGuid();
        sessions.MarkSeen(sid, new[] { TargetStatementId }, Now);

        var args = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = TargetStatementId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["text"] = "Только проверка",
        };

        var prevOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            using (RequestContext.Push(sid.ToString(), sessions))
            {
                _ = WriteHandlers.UpdateNode(env.Root, args, dryRun: true);
            }
        }
        finally
        {
            Console.SetOut(prevOut);
        }

        // На FS ничего не записалось — seen-set не трогаем (LLM продолжает считать
        // узел «уже видел», и это корректно: реального изменения не было).
        Assert.Contains(TargetStatementId, sessions.Sessions[sid].Ids);
    }

    [Fact]
    public void WriteHandlers_UpdateNode_NoSessionContext_DoesNotThrow()
    {
        using var env = new WriteTestEnvironment();

        var args = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = TargetStatementId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["text"] = "Без активной сессии",
        };

        var prevOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        int exitCode;
        try
        {
            // Без RequestContext.Push: ambient ctx == null, invalidation просто пропускается.
            exitCode = WriteHandlers.UpdateNode(env.Root, args, dryRun: false);
        }
        finally
        {
            Console.SetOut(prevOut);
        }

        Assert.Equal(0, exitCode);
    }
}
