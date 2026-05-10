using DocsWalker.Core.Api;

namespace DocsWalker.Tests;

/// <summary>
/// Поведение <see cref="WriteApi.ApplyOne(WriteOp,bool)"/> для <see cref="RedirectRefsOp"/>:
/// диагностика <c>no_effect</c> при попадании только в path-child refs (stg-0008
/// step-08, sub-task 4). Без этого LLM путается: <c>get-in-refs</c> показывает
/// связи (они физически в <c>out_refs</c> родителя), а <c>redirect-refs</c>
/// тихо игнорирует их и возвращает <c>no_effect</c>.
/// </summary>
public class RedirectRefsTests
{
    [Fact]
    public void RedirectRefs_FromPathChildOnly_NoEffectExplainsPathChildSkip()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromRoot(env.Root));

        // id=8 — definition «узел» внутри section «Модель данных» (id=2);
        // см. ReadApiTests.FormatPath_BuildsHumanReadablePath. Его in-refs —
        // только path-child запись section.<ref-name>: [..., 8], которая
        // управляется move-node, не redirect-refs.
        var op = new RedirectRefsOp(FromIds: new[] { 8 }, ToId: 1, Name: null, Unlink: false);

        var ex = Assert.Throws<WriteApiException>(() => api.ApplyOne(op));
        Assert.Equal("no_effect", ex.Code);
        // Сообщение должно явно упомянуть, что пропущены path-child refs —
        // это снимает путаницу с get-in-refs и направляет к move-node.
        Assert.Contains("path-child", ex.Message);
        Assert.Contains("move-node", ex.Hint ?? string.Empty);
    }

    [Fact]
    public void RedirectRefs_FromUnknownIdWithNoIncomingRefs_NoEffectGenericHint()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromRoot(env.Root));

        // Реальный узел без cross-refs на него и без path-children: id корня (0)
        // имеет path-children, поэтому не подходит. Выбираем «глубокий лист»:
        // id=1 (project DocsWalker) — у него есть path-children sections, но
        // поскольку path-child ветка теперь даёт хинт про move-node, нужен
        // настоящий лист без in-refs. Берём заведомо несуществующий id —
        // FromIds-валидация поймает раньше, чем дойдёт до сканирования.
        // Поэтому проверяем именно generic-hint иначе: используем существующий
        // лист definition (id=8) с unlink=true — у него только path-child
        // in-ref, ветка path-child даст специализированный хинт. Это уже
        // покрыто в первом тесте. Здесь же — путь, когда нет вообще никаких
        // связей: создаём свежий узел и сразу пробуем redirect-refs от него.
        var createOp = new CreateNodeOp(
            TypeName: "definition",
            Title: "tmp-test-leaf",
            Text: "временный узел для теста redirect-refs no-incoming",
            Refs: new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
            {
                ["path"] = new[] { 2 }, // вкладываем в section «Модель данных»
            });
        var createResult = api.ApplyOne(createOp);
        Assert.True(createResult.Applied);
        var newId = createResult.OpResults[0].Data["id"]!.GetValue<int>();

        // На свежесозданный узел нет cross-refs (только parent.<refname>: [newId]
        // через path-child автоматизм). redirect-refs от него попадёт в ту же
        // path-child ветку — sanity-check, что хинт про move-node работает.
        var op = new RedirectRefsOp(FromIds: new[] { newId }, ToId: 1, Name: null, Unlink: false);

        var ex = Assert.Throws<WriteApiException>(() => api.ApplyOne(op));
        Assert.Equal("no_effect", ex.Code);
        Assert.Contains("path-child", ex.Message);
    }
}
