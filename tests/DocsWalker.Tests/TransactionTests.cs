using System.Text.Json.Nodes;
using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;

namespace DocsWalker.Tests;

public class TransactionTests
{
    [Fact]
    public void Transaction_AddRefType_ThenCreateRef_AppliesAtomically()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);

        var ops = new WriteOp[]
        {
            new AddRefTypeOp("defines", "from_to", "Источник определяет цель."),
            new CreateRefOp(FromId: 2, RefType: "defines", ToId: 8),
        };
        var result = write.Apply(ops);
        Assert.Equal(2, result.OpResults.Count);

        var schema = SchemaLoader.LoadSchema(env.SchemaPath);
        Assert.Contains(schema.Types.OfType<RefType>(), t => t.Name == "defines");

        var graph = DocumentLoader.Load(env.DocsRoot, schema).Graph;
        var src = graph.GetById(2)!;
        Assert.Contains(src.ExplicitOutRefs!, r => r.TypeName == "defines" && r.ToId == 8);
    }

    [Fact]
    public void Transaction_FailingMidway_LeavesFilesUnchanged()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);

        var docFile = Path.Combine(env.DocsRoot, "DocsWalker.yml");
        var schemaFile = env.SchemaPath;
        var docBefore = File.ReadAllText(docFile);
        var schemaBefore = File.ReadAllText(schemaFile);
        var seqBefore = File.ReadAllText(env.SequencePath);

        // Первая операция — успешная (add-ref-type), вторая — заведомо ломающая
        // (create-ref на несуществующий тип). До применения wo вторая упадёт в WriteApi.
        var ops = new WriteOp[]
        {
            new AddRefTypeOp("defines", "from_to", "..."),
            new CreateRefOp(FromId: 2, RefType: "no_such_type", ToId: 8),
        };
        Assert.Throws<WriteApiException>(() => write.Apply(ops));

        Assert.Equal(docBefore, File.ReadAllText(docFile));
        Assert.Equal(schemaBefore, File.ReadAllText(schemaFile));
        Assert.Equal(seqBefore, File.ReadAllText(env.SequencePath));
    }

    [Fact]
    public void TransactionParser_ParsesAllOpKinds()
    {
        var json = JsonNode.Parse("""
        [
          { "op": "create-node", "parent_id": 1, "type": "section", "title": "x" },
          { "op": "update-node", "id": 2, "patch": { "title": "y" } },
          { "op": "delete-node", "id": 3 },
          { "op": "move-node", "id": 4, "new_parent_id": 5, "new_block_name": "definitions" },
          { "op": "create-ref", "from_id": 1, "type": "ref", "to_id": 2 },
          { "op": "delete-ref", "from_id": 1, "type": "ref", "to_id": 2 },
          { "op": "add-ref-type", "name": "z", "direction": "from_to", "description": "..." }
        ]
        """);
        var ops = TransactionParser.Parse(json);
        Assert.Equal(7, ops.Count);
        Assert.IsType<CreateNodeOp>(ops[0]);
        Assert.IsType<UpdateNodeOp>(ops[1]);
        Assert.IsType<DeleteNodeOp>(ops[2]);
        var move = Assert.IsType<MoveNodeOp>(ops[3]);
        Assert.Equal(4, move.Id);
        Assert.Equal(5, move.NewParentId);
        Assert.Equal("definitions", move.NewBlockName);
        Assert.IsType<CreateRefOp>(ops[4]);
        Assert.IsType<DeleteRefOp>(ops[5]);
        Assert.IsType<AddRefTypeOp>(ops[6]);
    }

    [Fact]
    public void Transaction_MoveNode_AppliesAndPersists()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);

        // Перенос definition 6 ("схема") из section 4 в section 17 в составе пачки.
        var ops = new WriteOp[]
        {
            new MoveNodeOp(Id: 6, NewParentId: 17, NewBlockName: null),
        };
        var result = write.Apply(ops);
        Assert.Single(result.OpResults);
        Assert.Equal("move-node", result.OpResults[0].Type);

        var schema = SchemaLoader.LoadSchema(env.SchemaPath);
        var graph = DocumentLoader.Load(env.DocsRoot, schema).Graph;
        var moved = graph.GetById(6)!;
        Assert.Equal(17, moved.ParentId);
        Assert.Equal("definitions", moved.ParentBlockName);
    }

    [Fact]
    public void TransactionParser_UnknownOp_Reports_OperationIndex()
    {
        var json = JsonNode.Parse("""
        [
          { "op": "create-node", "parent_id": 1, "type": "section", "title": "x" },
          { "op": "wat" }
        ]
        """);
        var ex = Assert.Throws<WriteApiException>(() => TransactionParser.Parse(json));
        Assert.Equal("unknown_op", ex.Code);
        Assert.Contains("#1", ex.Message);
    }

    [Fact]
    public void TransactionParser_RejectsNonArrayInput()
    {
        var json = JsonNode.Parse("{}");
        var ex = Assert.Throws<WriteApiException>(() => TransactionParser.Parse(json));
        Assert.Equal("invalid_transaction_input", ex.Code);
    }

    [Fact]
    public void Transaction_CreateAndDeleteSameNode_NetEffect_NoChange()
    {
        using var env = new WriteTestEnvironment();
        var ctx = WriteContext.FromRoot(env.Root);
        var write = new WriteApi(ctx);

        var schemaBefore = File.ReadAllText(env.SchemaPath);
        var sequenceBefore = int.Parse(File.ReadAllText(env.SequencePath).Trim());
        var expectedId = sequenceBefore + 1;

        var ops = new WriteOp[]
        {
            new CreateNodeOp(ParentId: 1, TypeName: "section", Title: "TMP", Name: null,
                             Body: new JsonObject { ["statements"] = new JsonArray { (JsonNode)"одно" } }),
            // Узел получает id = sequenceBefore + 1; затем удаляем его.
            new DeleteNodeOp(Id: expectedId),
        };
        var result = write.Apply(ops);
        Assert.Equal(2, result.OpResults.Count);

        // sequence.txt всё равно сместилась на +1 — id монотонен.
        Assert.Equal(expectedId.ToString(), File.ReadAllText(env.SequencePath).Trim());
        // schema не менялась.
        Assert.Equal(schemaBefore, File.ReadAllText(env.SchemaPath));

        // Удалённый узел в графе отсутствует.
        var schema = SchemaLoader.LoadSchema(env.SchemaPath);
        var graph = DocumentLoader.Load(env.DocsRoot, schema).Graph;
        Assert.Null(graph.GetById(expectedId));
    }
}
