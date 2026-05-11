using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Store;
using GraphModel = DocsWalker.Core.Graph.Graph;


namespace DocsWalker.Cli.Cli.Handlers;

/// <summary>
/// Обработчики read-команд CLI. Каждая команда:
/// 1. Загружает Схему и граф документов из <paramref name="storagePath"/>
///    (папка <c>docs/</c> графа, передаётся kernel'ом через
///    <c>--storage-path=</c>).
/// 2. Дёргает <see cref="ReadApi"/>.
/// 3. Печатает результат через <see cref="Output"/>.
/// </summary>
internal static class ReadHandlers
{
    public static int GetNodes(
        string storagePath,
        string idsParam,
        IReadOnlyCollection<string>? fields,
        int maxTokens)
    {
        var ids = ParseIds(idsParam);
        return WithApi(storagePath, api =>
        {
            try
            {
                var nodes = api.ReadApi.GetNodes(ids);
                var autoIncludes = api.ReadApi.CollectAutoIncludes(nodes);
                var json = ReadApiJson.NodesToJsonBudgeted(nodes, autoIncludes, fields, maxTokens);
                Output.WriteSuccess(json);
                return 0;
            }
            catch (ReadApiException ex)
            {
                Output.WriteError(ex.Code, path: null, ex.Message, ex.Hint);
                return 1;
            }
        });
    }

    public static int GetByPath(string storagePath, string path, string? tree)
    {
        return WithApi(storagePath, api =>
        {
            try
            {
                var subtree = api.ReadApi.GetByPath(path, tree);
                var autoIncludes = api.ReadApi.CollectAutoIncludes(subtree);
                var json = ReadApiJson.SubtreeToJsonWithAutoIncludes(
                    subtree, fields: null, autoIncludes);
                Output.WriteSuccess(json);
                return 0;
            }
            catch (ReadApiException ex)
            {
                Output.WriteError(ex.Code, path: null, ex.Message, ex.Hint);
                return 1;
            }
        });
    }

    public static int GetTree(
        string storagePath,
        int id,
        string? tree,
        int? depth,
        IReadOnlyCollection<string>? fields,
        int maxTokens)
    {
        var scope = string.IsNullOrEmpty(tree) ? Node.PathRefName : tree;
        return WithApi(storagePath, api =>
        {
            try
            {
                var subtree = api.ReadApi.GetTree(id, scope, depth);
                var autoIncludes = api.ReadApi.CollectAutoIncludes(subtree);
                var json = ReadApiJson.SubtreeToJson(subtree, scope, fields, autoIncludes, maxTokens);
                Output.WriteSuccess(json);
                return 0;
            }
            catch (ReadApiException ex)
            {
                Output.WriteError(ex.Code, path: null, ex.Message, ex.Hint);
                return 1;
            }
        });
    }

    public static int GetAncestors(string storagePath, int id, string? tree)
    {
        var scope = string.IsNullOrEmpty(tree) ? Node.PathRefName : tree;
        return WithApi(storagePath, api =>
        {
            try
            {
                var ancestors = api.ReadApi.GetAncestors(id, scope);
                Output.WriteSuccess(ReadApiJson.AncestorsToJson(ancestors, scope));
                return 0;
            }
            catch (ReadApiException ex)
            {
                Output.WriteError(ex.Code, path: null, ex.Message, ex.Hint);
                return 1;
            }
        });
    }

    public static int GetRefs(string storagePath, int id, string? name)
    {
        return WithApi(storagePath, api =>
        {
            try
            {
                var set = api.ReadApi.GetRefs(id, name);
                Output.WriteSuccess(ReadApiJson.RefSetToJson(set));
                return 0;
            }
            catch (ReadApiException ex)
            {
                Output.WriteError(ex.Code, path: null, ex.Message, ex.Hint);
                return 1;
            }
        });
    }

    public static int GetInRefs(string storagePath, int id, string? name)
    {
        return WithApi(storagePath, api =>
        {
            try
            {
                var map = api.ReadApi.GetInRefs(id, name);
                Output.WriteSuccess(ReadApiJson.RefMapToJson(map));
                return 0;
            }
            catch (ReadApiException ex)
            {
                Output.WriteError(ex.Code, path: null, ex.Message, ex.Hint);
                return 1;
            }
        });
    }

    public static int CheckIntegrity(string storagePath)
    {
        var schemaPath = Path.Combine(storagePath, "Схема.yml");
        var metaSchemaPath = Path.Combine(storagePath, ".docswalker", "meta-schema.yml");
        var sequencePath = Path.Combine(storagePath, ".docswalker", "sequence.txt");

        try
        {
            var meta = SchemaLoader.LoadMetaSchema(metaSchemaPath);
            var schema = SchemaLoader.LoadSchema(schemaPath);
            var loaded = DocumentLoader.Load(storagePath, schema);
            int? sequence = File.Exists(sequencePath) ? new SequenceCounter(sequencePath).Read() : null;
            var api = new ReadApi(loaded.Graph, schema);
            var result = api.CheckIntegrity(meta, schema, sequence);
            Output.WriteSuccess(ReadApiJson.ValidationResultToJson(result));
            return 0;
        }
        catch (SchemaLoadException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }
        catch (GraphLoadException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }
        catch (SequenceCounterException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }
    }

    public static int Search(
        string storagePath,
        string query,
        SearchInMode inMode,
        string? typeFilter,
        string? tree,
        int? under,
        bool regex,
        int? limit,
        bool compact)
    {
        return WithApi(storagePath, api =>
        {
            try
            {
                var hits = api.ReadApi.Search(
                    query,
                    inMode,
                    typeFilter,
                    tree,
                    under,
                    regex,
                    limit ?? 20,
                    compact);
                Output.WriteSuccess(ReadApiJson.SearchToJson(hits));
                return 0;
            }
            catch (ReadApiException ex)
            {
                Output.WriteError(ex.Code, path: null, ex.Message, ex.Hint);
                return 1;
            }
        });
    }

    public static int GetOverview(string storagePath)
    {
        return WithApi(storagePath, api =>
        {
            try
            {
                var overview = api.ReadApi.GetOverview();
                Output.WriteSuccess(ReadApiJson.OverviewToJson(overview));
                return 0;
            }
            catch (ReadApiException ex)
            {
                Output.WriteError(ex.Code, path: null, ex.Message, ex.Hint);
                return 1;
            }
        });
    }

    private sealed record LoadedApi(GraphModel Graph, ReadApi ReadApi);

    /// <summary>
    /// Загружает Схему и граф один раз, отдаёт обёртку c <see cref="ReadApi"/>.
    /// Ошибки загрузки превращаются в structured CLI-ошибку с exit-кодом 1.
    /// </summary>
    private static int WithApi(string storagePath, Func<LoadedApi, int> action)
    {
        var schemaPath = Path.Combine(storagePath, "Схема.yml");

        SchemaDocument schema;
        try
        {
            schema = SchemaLoader.LoadSchema(schemaPath);
        }
        catch (SchemaLoadException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }

        DocumentLoadResult loaded;
        try
        {
            loaded = DocumentLoader.Load(storagePath, schema);
        }
        catch (GraphLoadException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }

        var api = new ReadApi(loaded.Graph, schema);
        return action(new LoadedApi(loaded.Graph, api));
    }

    private static List<int> ParseIds(string raw)
    {
        var parts = raw.Split(',');
        var ids = new List<int>(parts.Length);
        foreach (var p in parts)
        {
            ids.Add(int.Parse(p.Trim(), System.Globalization.CultureInfo.InvariantCulture));
        }
        return ids;
    }
}
