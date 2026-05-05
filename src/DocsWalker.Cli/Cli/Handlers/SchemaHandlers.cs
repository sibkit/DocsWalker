using DocsWalker.Core.Schema;

namespace DocsWalker.Cli.Cli.Handlers;

internal static class SchemaHandlers
{
    public static int GetMetaSchema(string root)
    {
        var path = Path.Combine(root, "docs", ".docswalker", "meta-schema.yml");
        try
        {
            var ms = SchemaLoader.LoadMetaSchema(path);
            Output.WriteSuccess(SchemaJson.ToJson(ms));
            return 0;
        }
        catch (SchemaLoadException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }
    }

    public static int GetSchema(string root)
    {
        var path = Path.Combine(root, "docs", "Схема.yml");
        try
        {
            var schema = SchemaLoader.LoadSchema(path);
            Output.WriteSuccess(SchemaJson.ToJson(schema));
            return 0;
        }
        catch (SchemaLoadException ex)
        {
            Output.WriteError(ex.Code, ex.FilePath, ex.Message);
            return 1;
        }
    }
}
