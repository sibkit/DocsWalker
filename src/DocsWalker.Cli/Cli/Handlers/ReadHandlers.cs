using DocsWalker.Core.Api;
using DocsWalker.Core.Graph;
using DocsWalker.Core.Schema;
using DocsWalker.Core.Store;
using GraphModel = DocsWalker.Core.Graph.Graph;

namespace DocsWalker.Cli.Cli.Handlers;

/// <summary>
/// Обработчики read-команд CLI. Каждая команда:
/// 1. Загружает Схему и граф документов из <paramref name="root"/>/docs.
/// 2. Дёргает <see cref="ReadApi"/>.
/// 3. Печатает результат через <see cref="Output"/>.
/// </summary>
internal static class ReadHandlers
{
    public static int ListDocuments(string root)
    {
        return WithApi(root, api =>
        {
            var docs = api.ReadApi.ListDocuments();
            Output.WriteSuccess(ReadApiJson.ListDocumentsToJson(docs));
            return 0;
        });
    }

    public static int GetMap(string root)
    {
        return WithApi(root, api =>
        {
            var map = api.ReadApi.GetMap();
            Output.WriteSuccess(ReadApiJson.MapToJson(map));
            return 0;
        });
    }

    public static int GetNodes(string root, string idsParam)
    {
        var ids = ParseIds(idsParam);
        return WithApi(root, api =>
        {
            try
            {
                var nodes = api.ReadApi.GetNodes(ids);
                Output.WriteSuccess(ReadApiJson.NodesToJson(nodes));
                return 0;
            }
            catch (ReadApiException ex)
            {
                Output.WriteError(ex.Code, path: null, ex.Message, ex.Hint);
                return 1;
            }
        });
    }

    public static int GetByPath(string root, string path)
    {
        return WithApi(root, api =>
        {
            try
            {
                var subtree = api.ReadApi.GetByPath(path);
                Output.WriteSuccess(ReadApiJson.SubtreeToJson(subtree));
                return 0;
            }
            catch (ReadApiException ex)
            {
                Output.WriteError(ex.Code, path: null, ex.Message, ex.Hint);
                return 1;
            }
        });
    }

    public static int GetRefs(string root, int id, string? name)
    {
        return WithApi(root, api =>
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

    public static int GetInRefs(string root, int id, string? name)
    {
        return WithApi(root, api =>
        {
            try
            {
                var set = api.ReadApi.GetInRefs(id, name);
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

    public static int CheckIntegrity(string root)
    {
        var docsRoot = Path.Combine(root, "docs");
        var schemaPath = Path.Combine(docsRoot, "Схема.yml");
        var metaSchemaPath = Path.Combine(docsRoot, ".docswalker", "meta-schema.yml");
        var sequencePath = Path.Combine(docsRoot, ".docswalker", "sequence.txt");

        try
        {
            var meta = SchemaLoader.LoadMetaSchema(metaSchemaPath);
            var schema = SchemaLoader.LoadSchema(schemaPath);
            var loaded = DocumentLoader.Load(docsRoot, schema);
            int? sequence = File.Exists(sequencePath) ? new SequenceCounter(sequencePath).Read() : null;
            var api = new ReadApi(loaded.Graph);
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

    public static int Search(string root, string query)
    {
        return WithApi(root, api =>
        {
            try
            {
                var hits = api.ReadApi.Search(query);
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

    private sealed record LoadedApi(GraphModel Graph, ReadApi ReadApi);

    /// <summary>
    /// Загружает Схему и граф один раз, отдаёт обёртку c <see cref="ReadApi"/>.
    /// Ошибки загрузки превращаются в structured CLI-ошибку с exit-кодом 1.
    /// </summary>
    private static int WithApi(string root, Func<LoadedApi, int> action)
    {
        var docsRoot = Path.Combine(root, "docs");
        var schemaPath = Path.Combine(docsRoot, "Схема.yml");

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
            loaded = DocumentLoader.Load(docsRoot, schema);
        }
        catch (GraphLoadException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }

        var api = new ReadApi(loaded.Graph);
        return action(new LoadedApi(loaded.Graph, api));
    }

    private static List<int> ParseIds(string raw)
    {
        var parts = raw.Split(',');
        var ids = new List<int>(parts.Length);
        foreach (var p in parts)
        {
            // ArgParser/ParamType.IdList уже проверил формат.
            ids.Add(int.Parse(p.Trim(), System.Globalization.CultureInfo.InvariantCulture));
        }
        return ids;
    }
}
