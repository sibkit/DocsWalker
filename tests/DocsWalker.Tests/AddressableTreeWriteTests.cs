using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;

namespace DocsWalker.Tests;

/// <summary>
/// E2e write-path для addressable-tree collision (stg-0010 step-02 + step-05):
/// <see cref="WriteApi.Apply"/> для <c>create-node</c>, <c>update-node</c>,
/// <c>move-node</c> и пакетной записи должен бракоlocateть sibling-title-collision
/// в любом addressable tree через <see cref="WriteValidationException"/> с
/// error-code <c>duplicate_sibling_title</c>.
///
/// Тесты идут на изолированном клоне реального <c>docs/</c>
/// (<see cref="WriteTestEnvironment"/>); реальная Схема DocsWalker'а имеет
/// единственный addressable tree <c>path</c>, чего достаточно для покрытия
/// всех write-операций. Validator-уровень покрыт в <see cref="ValidatorTests"/>.
/// </summary>
public class AddressableTreeWriteTests
{
    private const int DocsWalkerDocumentId = 1;

    [Fact]
    public void CreateNode_DuplicateSiblingTitle_Reports_DuplicateSiblingTitle()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromStoragePath(env.DocsRoot));

        var firstOp = NewSectionOp("step05-create-collision-victim", DocsWalkerDocumentId);
        api.ApplyOne(firstOp);

        var secondOp = NewSectionOp("step05-create-collision-victim", DocsWalkerDocumentId);
        var ex = Assert.Throws<WriteValidationException>(() => api.ApplyOne(secondOp));
        Assert.Contains(ex.Errors, e => e.Code == "duplicate_sibling_title");
    }

    [Fact]
    public void UpdateNode_TitleCollidesWithSibling_Reports_DuplicateSiblingTitle()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromStoragePath(env.DocsRoot));

        api.ApplyOne(NewSectionOp("step05-update-collision-A", DocsWalkerDocumentId));
        var createBResult = api.ApplyOne(NewSectionOp("step05-update-collision-B", DocsWalkerDocumentId));
        var idB = createBResult.OpResults[0].Data["id"]!.GetValue<int>();

        var renameOp = new UpdateNodeOp(Id: idB, NewTitle: "step05-update-collision-A", NewText: null);
        var ex = Assert.Throws<WriteValidationException>(() => api.ApplyOne(renameOp));
        Assert.Contains(ex.Errors, e => e.Code == "duplicate_sibling_title");
    }

    [Fact]
    public void MoveNode_NewParentHasSiblingWithSameTitle_Reports_DuplicateSiblingTitle()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromStoragePath(env.DocsRoot));

        // Дли move-node нужны два разных parent'а одного типа, способных принимать
        // section'ы. Берём два первых документа из реального docs/ — там их 3
        // (DocsWalker, Стек, Правила оформления).
        var (docA, docB) = FindTwoDocumentIds(env.DocsRoot);

        api.ApplyOne(NewSectionOp("step05-move-collision-shared", docA));
        var createBResult = api.ApplyOne(NewSectionOp("step05-move-collision-shared", docB));
        var idB = createBResult.OpResults[0].Data["id"]!.GetValue<int>();

        var moveOp = new MoveNodeOp(Id: idB, NewParentId: docA);
        var ex = Assert.Throws<WriteValidationException>(() => api.ApplyOne(moveOp));
        Assert.Contains(ex.Errors, e => e.Code == "duplicate_sibling_title");
    }

    [Fact]
    public void Transaction_BatchCreatesCollision_Reports_DuplicateSiblingTitle()
    {
        using var env = new WriteTestEnvironment();
        var api = new WriteApi(WriteContext.FromStoragePath(env.DocsRoot));

        var ops = new WriteOp[]
        {
            NewSectionOp("step05-batch-collision", DocsWalkerDocumentId),
            NewSectionOp("step05-batch-collision", DocsWalkerDocumentId),
        };
        var ex = Assert.Throws<WriteValidationException>(() => api.Apply(ops));
        Assert.Contains(ex.Errors, e => e.Code == "duplicate_sibling_title");
    }

    private static CreateNodeOp NewSectionOp(string title, int parentDocumentId) => new(
        TypeName: "section",
        Title: title,
        Text: null,
        Refs: new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            ["path"] = new[] { parentDocumentId },
        });

    private static (int docA, int docB) FindTwoDocumentIds(string docsRoot)
    {
        var schema = SchemaLoader.LoadSchema(Path.Combine(docsRoot, "Схема.yml"));
        var loaded = DocumentLoader.Load(docsRoot, schema);
        var documents = loaded.Graph.GetByType("document");
        if (documents.Count < 2)
            throw new InvalidOperationException(
                $"Real docs/ ожидается ≥2 documents, но найдено {documents.Count}.");
        return (documents[0].Id, documents[1].Id);
    }
}
