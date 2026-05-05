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
    public void CreateNode_NonExistentParent_Includes_Hint()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);
        var ex = Assert.Throws<WriteApiException>(() =>
            write.ApplyOne(new CreateNodeOp(99999, "section", "X", null, null)));
        Assert.Equal("parent_not_found", ex.Code);
        Assert.False(string.IsNullOrEmpty(ex.Hint),
            "WriteApiException(parent_not_found) должна нести непустой Hint для LLM-агента.");
    }

    [Fact]
    public void AddRefType_ReservedName_Includes_Hint()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);
        var ex = Assert.Throws<WriteApiException>(() =>
            write.ApplyOne(new AddRefTypeOp("definitions", "from_to", "...")));
        Assert.Equal("reserved_name", ex.Code);
        Assert.False(string.IsNullOrEmpty(ex.Hint));
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

    [Fact]
    public void MoveNode_Definition_BetweenSections_InSameDocument()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);

        // Definition id=5 ("мета-схема") сейчас лежит в section 4 ("Уровни схемы"),
        // блок definitions=[5, 6]. Переносим его в section 17 ("Операции чтения"),
        // у которой definitions содержат [18..26, 70] — после переноса должен
        // появиться 5 в конце.
        var result = write.ApplyOne(new MoveNodeOp(Id: 5, NewParentId: 17, NewBlockName: null));
        Assert.Single(result.OpResults);
        var data = result.OpResults[0].Data;
        Assert.Equal(5, data["id"]!.GetValue<int>());
        Assert.Equal(17, data["new_parent_id"]!.GetValue<int>());
        Assert.Equal("definitions", data["new_block_name"]!.GetValue<string>());

        var graph = LoadGraph(env);
        var oldParent = graph.GetById(4)!;
        var oldDefs = oldParent.Blocks!.OfType<ChildrenBlock>().First(b => b.Name == "definitions");
        Assert.DoesNotContain(5, oldDefs.ChildIds);
        Assert.Contains(6, oldDefs.ChildIds);

        var newParent = graph.GetById(17)!;
        var newDefs = newParent.Blocks!.OfType<ChildrenBlock>().First(b => b.Name == "definitions");
        Assert.Contains(5, newDefs.ChildIds);
        Assert.Equal(5, newDefs.ChildIds[^1]); // Добавлено в конец.

        var moved = graph.GetById(5)!;
        Assert.Equal(17, moved.ParentId);
        Assert.Equal("definitions", moved.ParentBlockName);
        Assert.Equal("DocsWalker.yml", moved.SourceFile);
    }

    [Fact]
    public void MoveNode_Section_BetweenDocuments_UpdatesSubtreeSourceFile()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);

        // Section id=4 ("Уровни схемы") в DocsWalker.yml (doc id=1) содержит definitions [5, 6].
        // Переносим всю section в Стек.yml (doc id=64). Проверяем, что у section и
        // обоих определений source_file обновился на "Стек.yml".
        write.ApplyOne(new MoveNodeOp(Id: 4, NewParentId: 64, NewBlockName: null));

        var graph = LoadGraph(env);
        var moved = graph.GetById(4)!;
        Assert.Equal(64, moved.ParentId);
        Assert.Equal("content", moved.ParentBlockName);
        Assert.Equal("Стек.yml", moved.SourceFile);

        var def5 = graph.GetById(5)!;
        Assert.Equal(4, def5.ParentId);
        Assert.Equal("Стек.yml", def5.SourceFile);

        var def6 = graph.GetById(6)!;
        Assert.Equal(4, def6.ParentId);
        Assert.Equal("Стек.yml", def6.SourceFile);

        // Старый документ DocsWalker.yml больше не содержит section 4 среди children.
        var oldDoc = graph.GetById(1)!;
        var oldDocChildren = graph.GetChildren(oldDoc.Id);
        Assert.DoesNotContain(oldDocChildren, c => c.Id == 4);

        // Новый документ Стек.yml теперь содержит section 4.
        var newDoc = graph.GetById(64)!;
        var newDocChildren = graph.GetChildren(newDoc.Id);
        Assert.Contains(newDocChildren, c => c.Id == 4);
    }

    [Fact]
    public void MoveNode_IncompatibleChildType_IsRejected()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);

        // Definition id=5 нельзя положить под document id=1 — у document в content
        // только section, definitions он не принимает.
        var ex = Assert.Throws<WriteApiException>(() =>
            write.ApplyOne(new MoveNodeOp(Id: 5, NewParentId: 1, NewBlockName: null)));
        Assert.Equal("invalid_child_type", ex.Code);
    }

    [Fact]
    public void MoveNode_RequestedBlockName_NotOnParent_IsRejected()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);

        // Просим положить definition 5 в блок "examples" под section 17 — у section
        // блок "examples" есть, но он принимает 'example', а не 'definition'.
        var ex = Assert.Throws<WriteApiException>(() =>
            write.ApplyOne(new MoveNodeOp(Id: 5, NewParentId: 17, NewBlockName: "examples")));
        Assert.Equal("unknown_block", ex.Code);
    }

    [Fact]
    public void MoveNode_DocumentRoot_IsRejected()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);

        var ex = Assert.Throws<WriteApiException>(() =>
            write.ApplyOne(new MoveNodeOp(Id: 1, NewParentId: 64, NewBlockName: null)));
        Assert.Equal("cannot_move_document", ex.Code);
    }

    [Fact]
    public void MoveNode_Self_IsRejected()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);

        var ex = Assert.Throws<WriteApiException>(() =>
            write.ApplyOne(new MoveNodeOp(Id: 17, NewParentId: 17, NewBlockName: null)));
        Assert.Equal("invalid_move", ex.Code);
    }

    [Fact]
    public void MoveNode_NoEffect_SameParentSameBlock_IsRejected()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);

        // Definition 5 уже в section 4, блок "definitions" — повторный перенос туда же.
        var ex = Assert.Throws<WriteApiException>(() =>
            write.ApplyOne(new MoveNodeOp(Id: 5, NewParentId: 4, NewBlockName: "definitions")));
        Assert.Equal("no_effect", ex.Code);
    }

    private static DocsWalker.Core.Graph.Graph LoadGraph(WriteTestEnvironment env)
    {
        var schema = SchemaLoader.LoadSchema(env.SchemaPath);
        return DocumentLoader.Load(env.DocsRoot, schema).Graph;
    }
}
