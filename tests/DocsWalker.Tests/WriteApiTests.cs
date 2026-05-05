using System.Text.Json.Nodes;
using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;

namespace DocsWalker.Tests;

public class WriteApiTests
{
    [Fact]
    public void CreateNode_Section_UnderDocument_AssignsNextId_AndPersists()
    {
        using var env = new WriteTestEnvironment();
        var apiBefore = LoadGraph(env);
        var docId = apiBefore.GetDocumentByTitle("DocsWalker")!.Id;
        var sequenceBefore = int.Parse(File.ReadAllText(env.SequencePath).Trim());
        var expectedId = sequenceBefore + 1;

        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);
        var op = new CreateNodeOp(
            ParentId: docId,
            TypeName: "section",
            Title: "TEST",
            Name: null,
            Body: new JsonObject
            {
                ["statements"] = new JsonArray { (JsonNode)"одно утверждение" },
            });
        var result = write.ApplyOne(op);

        Assert.Single(result.OpResults);
        var data = result.OpResults[0].Data;
        var newId = data["id"]!.GetValue<int>();
        Assert.Equal(expectedId, newId);

        // Проверяем, что граф после перезагрузки содержит новый узел.
        var apiAfter = LoadGraph(env);
        var newSection = apiAfter.GetById(newId);
        Assert.NotNull(newSection);
        Assert.Equal("section", newSection!.TypeName);
        Assert.Equal("TEST", newSection.Title);
        Assert.Equal(docId, newSection.ParentId);

        // sequence.txt обновился до expectedId.
        Assert.Equal(expectedId.ToString(), File.ReadAllText(env.SequencePath).Trim());
    }

    [Fact]
    public void CreateNode_Definition_UnderSection_UpdatesParentChildrenBlock()
    {
        using var env = new WriteTestEnvironment();
        var sequenceBefore = int.Parse(File.ReadAllText(env.SequencePath).Trim());
        var expectedId = sequenceBefore + 1;

        // Section "Уровни схемы" (id=4) содержит definitions [5, 6].
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);
        var op = new CreateNodeOp(
            ParentId: 4,
            TypeName: "definition",
            Title: "новый термин",
            Name: null,
            Body: new JsonObject { ["value"] = "содержание" });
        var result = write.ApplyOne(op);
        var newId = result.OpResults[0].Data["id"]!.GetValue<int>();
        Assert.Equal(expectedId, newId);

        var graph = LoadGraph(env);
        var section = graph.GetById(4)!;
        var definitionsBlock = section.Blocks!.OfType<ChildrenBlock>()
            .FirstOrDefault(b => b.Name == "definitions");
        Assert.NotNull(definitionsBlock);
        Assert.Equal(new[] { 5, 6, newId }, definitionsBlock!.ChildIds);

        var newNode = graph.GetById(newId)!;
        Assert.Equal("definition", newNode.TypeName);
        Assert.Equal("новый термин", newNode.Title);
        Assert.Equal("содержание", newNode.InlineValue);
        Assert.Equal(4, newNode.ParentId);
    }

    [Fact]
    public void UpdateNode_Section_RenamesTitle()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);
        var op = new UpdateNodeOp(
            Id: 2,
            Patch: new JsonObject { ["title"] = "Назначение!" });
        write.ApplyOne(op);

        var graph = LoadGraph(env);
        Assert.Equal("Назначение!", graph.GetById(2)!.Title);
    }

    [Fact]
    public void DeleteNode_Definition_RemovesFromGraph_AndFromParentBlock()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);
        // Definition id=5 ("мета-схема") — без входящих явных связей.
        write.ApplyOne(new DeleteNodeOp(5));

        var graph = LoadGraph(env);
        Assert.Null(graph.GetById(5));

        var parent = graph.GetById(4)!;
        var defsBlock = parent.Blocks!.OfType<ChildrenBlock>()
            .First(b => b.Name == "definitions");
        Assert.DoesNotContain(5, defsBlock.ChildIds);
        Assert.Contains(6, defsBlock.ChildIds);
    }

    [Fact]
    public void DeleteNode_WithIncomingExplicitRef_IsRejected()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);
        // Section 4 («Уровни схемы») в DocsWalker.yml — на неё есть входящие явные
        // ссылки ref:4 из секций 47 и 62 в Правилах оформления.
        var ex = Assert.Throws<WriteApiException>(() => write.ApplyOne(new DeleteNodeOp(4)));
        Assert.Equal("incoming_refs", ex.Code);
    }

    [Fact]
    public void CreateRef_AppendsExplicitRef_AndPersistsInYaml()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);
        // Из section 2 («Назначение») в definition 8 («узел»). Тип — "ref".
        write.ApplyOne(new CreateRefOp(FromId: 2, RefType: "ref", ToId: 8));

        var graph = LoadGraph(env);
        var src = graph.GetById(2)!;
        Assert.Contains(src.ExplicitOutRefs!, r => r.TypeName == "ref" && r.ToId == 8);
    }

    [Fact]
    public void DeleteRef_RemovesExplicitRef()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);
        // Section 45 имеет ref:46 — удалим её.
        write.ApplyOne(new DeleteRefOp(FromId: 45, RefType: "ref", ToId: 46));

        var graph = LoadGraph(env);
        var src = graph.GetById(45)!;
        Assert.DoesNotContain(src.ExplicitOutRefs ?? Array.Empty<Ref>(),
            r => r.TypeName == "ref" && r.ToId == 46);
    }

    [Fact]
    public void AddRefType_AppendsToSchema_AndPersists()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);
        write.ApplyOne(new AddRefTypeOp("defines", "from_to", "Источник определяет цель."));

        var schema = SchemaLoader.LoadSchema(env.SchemaPath);
        var rt = schema.Types.OfType<RefType>().FirstOrDefault(t => t.Name == "defines");
        Assert.NotNull(rt);
        Assert.Equal(RefDirection.FromTo, rt!.Direction);
        Assert.False(rt.System);
        Assert.Equal("Источник определяет цель.", rt.Description);
    }

    [Fact]
    public void AddRefType_ReservedName_IsRejected()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);
        var ex = Assert.Throws<WriteApiException>(() =>
            write.ApplyOne(new AddRefTypeOp("definitions", "from_to", "...")));
        Assert.Equal("reserved_name", ex.Code);
    }

    [Fact]
    public void AddRefType_DuplicateName_IsRejected()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);
        var ex = Assert.Throws<WriteApiException>(() =>
            write.ApplyOne(new AddRefTypeOp("ref", "from_to", "...")));
        Assert.Equal("duplicate_type", ex.Code);
    }

    [Fact]
    public void CreateNode_NonExistentParent_IsRejected()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);
        var ex = Assert.Throws<WriteApiException>(() =>
            write.ApplyOne(new CreateNodeOp(99999, "section", "X", null, null)));
        Assert.Equal("parent_not_found", ex.Code);
    }

    [Fact]
    public void CreateRef_DuplicateExplicitRef_IsRejected()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);
        // У section 45 уже есть ref:46.
        var ex = Assert.Throws<WriteApiException>(() =>
            write.ApplyOne(new CreateRefOp(45, "ref", 46)));
        Assert.Equal("duplicate_ref", ex.Code);
    }

    [Fact]
    public void Apply_FailedValidation_DoesNotChangeFiles()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);

        var docsWalkerYml = Path.Combine(env.DocsRoot, "DocsWalker.yml");
        var schemaYml = env.SchemaPath;
        var beforeDoc = File.ReadAllText(docsWalkerYml);
        var beforeSchema = File.ReadAllText(schemaYml);
        var beforeSeq = File.ReadAllText(env.SequencePath);

        // Положить в title многострочное значение — WriteApi его примет (тип патча корректен),
        // но StyleCheck в валидаторе обязан отклонить.
        Assert.Throws<WriteValidationException>(() =>
            write.ApplyOne(new UpdateNodeOp(2, new JsonObject { ["title"] = "плохой\nтайтл" })));

        Assert.Equal(beforeDoc, File.ReadAllText(docsWalkerYml));
        Assert.Equal(beforeSchema, File.ReadAllText(schemaYml));
        Assert.Equal(beforeSeq, File.ReadAllText(env.SequencePath));
    }

    private static DocsWalker.Core.Graph.Graph LoadGraph(WriteTestEnvironment env)
    {
        var schema = SchemaLoader.LoadSchema(env.SchemaPath);
        return DocumentLoader.Load(env.DocsRoot, schema).Graph;
    }
}
