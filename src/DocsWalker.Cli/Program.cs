using System.Text.Json;
using DocsWalker.Cli.Cli;
using DocsWalker.Cli.Cli.Handlers;

return Dispatcher.Run(args);

internal static class Dispatcher
{
    public static int Run(string[] argv)
    {
        var (parsed, parseError) = ArgParser.Parse(argv);
        if (parsed is null)
        {
            Output.WriteError(parseError!.Code, path: null, parseError.Message);
            return 1;
        }

        if (!Commands.ByKebab.TryGetValue(parsed.CommandKebab, out var spec))
        {
            Output.WriteError(
                "unknown_command",
                path: null,
                $"Неизвестная команда '{parsed.CommandKebab}'.");
            return 1;
        }

        if (TryValidateParams(spec, parsed.Params) is { } validationError)
        {
            Output.WriteError(validationError.Code, path: null, validationError.Message);
            return 1;
        }

        if (!TryResolveRoot(parsed.Params, out var rootPath, out var rootError))
        {
            Output.WriteError(rootError!.Code, rootError.Path, rootError.Message);
            return 1;
        }

        return spec.SnakeName switch
        {
            "get_meta_schema" => SchemaHandlers.GetMetaSchema(rootPath),
            "get_schema"      => SchemaHandlers.GetSchema(rootPath),
            "list_documents"  => ReadHandlers.ListDocuments(rootPath),
            "get_map"         => ReadHandlers.GetMap(rootPath),
            "get_nodes"       => ReadHandlers.GetNodes(rootPath, parsed.Params["ids"]),
            "get_by_path"     => ReadHandlers.GetByPath(rootPath, parsed.Params["path"]),
            "get_refs"        => ReadHandlers.GetRefs(
                                    rootPath,
                                    int.Parse(parsed.Params["id"], System.Globalization.CultureInfo.InvariantCulture),
                                    parsed.Params.TryGetValue("type", out var t1) ? t1 : null,
                                    parsed.Params.TryGetValue("origin", out var o1) ? o1 : null),
            "get_in_refs"     => ReadHandlers.GetInRefs(
                                    rootPath,
                                    int.Parse(parsed.Params["id"], System.Globalization.CultureInfo.InvariantCulture),
                                    parsed.Params.TryGetValue("type", out var t2) ? t2 : null,
                                    parsed.Params.TryGetValue("origin", out var o2) ? o2 : null),
            "search"          => ReadHandlers.Search(rootPath, parsed.Params["query"]),
            "create_node"     => WriteHandlers.CreateNode(rootPath, parsed.Params),
            "update_node"     => WriteHandlers.UpdateNode(rootPath, parsed.Params),
            "delete_node"     => WriteHandlers.DeleteNode(rootPath, parsed.Params),
            "create_ref"      => WriteHandlers.CreateRef(rootPath, parsed.Params),
            "delete_ref"      => WriteHandlers.DeleteRef(rootPath, parsed.Params),
            "add_ref_type"    => WriteHandlers.AddRefType(rootPath, parsed.Params),
            "transaction"     => WriteHandlers.Transaction(rootPath, parsed.Params),
            _                 => NotImplemented(spec),
        };
    }

    private static int NotImplemented(CommandSpec spec)
    {
        Output.WriteError(
            "not_implemented",
            path: null,
            $"Команда '{spec.KebabName}' пока не реализована.");
        return 1;
    }

    private static ParamValidationError? TryValidateParams(
        CommandSpec spec,
        IReadOnlyDictionary<string, string> provided)
    {
        // Неизвестные параметры (общий --root исключаем).
        foreach (var key in provided.Keys)
        {
            if (key == "root")
                continue;
            if (!HasParam(spec, key))
            {
                return new ParamValidationError(
                    "unknown_parameter",
                    $"Неизвестный параметр '--{key}' для команды '{spec.KebabName}'.");
            }
        }

        // Обязательные параметры и проверка типов значений.
        foreach (var p in spec.Params)
        {
            var hasValue = provided.TryGetValue(p.KebabName, out var value);
            if (p.Required && !hasValue)
            {
                return new ParamValidationError(
                    "missing_parameter",
                    $"Параметр '--{p.KebabName}' обязателен для команды '{spec.KebabName}'.");
            }

            if (hasValue && !ValidateValue(p.Type, value!, out var typeError))
            {
                return new ParamValidationError(
                    "invalid_parameter",
                    $"Параметр '--{p.KebabName}': {typeError}");
            }
        }

        return null;
    }

    private static bool HasParam(CommandSpec spec, string kebabName)
    {
        foreach (var p in spec.Params)
        {
            if (p.KebabName == kebabName)
                return true;
        }
        return false;
    }

    private static bool ValidateValue(ParamType type, string value, out string? error)
    {
        switch (type)
        {
            case ParamType.String:
                error = null;
                return true;

            case ParamType.Integer:
                if (!long.TryParse(value, out _))
                {
                    error = $"ожидается целое число, получено '{value}'.";
                    return false;
                }
                error = null;
                return true;

            case ParamType.IdList:
                if (value.Length == 0)
                {
                    error = "ожидается непустой список целых чисел через запятую.";
                    return false;
                }
                foreach (var part in value.Split(','))
                {
                    if (!long.TryParse(part, out _))
                    {
                        error = $"ожидается список целых чисел через запятую, получено '{value}'.";
                        return false;
                    }
                }
                error = null;
                return true;

            case ParamType.Json:
                try
                {
                    using var _ = JsonDocument.Parse(value);
                    error = null;
                    return true;
                }
                catch (JsonException ex)
                {
                    error = $"ожидается корректный JSON, ошибка разбора: {ex.Message}";
                    return false;
                }

            default:
                error = "неизвестный тип параметра.";
                return false;
        }
    }

    private static bool TryResolveRoot(
        IReadOnlyDictionary<string, string> args,
        out string root,
        out RootError? error)
    {
        if (args.TryGetValue("root", out var explicitRoot))
        {
            var docsPath = Path.Combine(explicitRoot, "docs");
            if (!Directory.Exists(docsPath))
            {
                root = string.Empty;
                error = new RootError(
                    "docs_not_found",
                    explicitRoot,
                    $"В каталоге '{explicitRoot}' нет подкаталога 'docs/'.");
                return false;
            }
            root = explicitRoot;
            error = null;
            return true;
        }

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "docs")))
            {
                root = current.FullName;
                error = null;
                return true;
            }
            current = current.Parent;
        }

        root = string.Empty;
        error = new RootError(
            "docs_not_found",
            Directory.GetCurrentDirectory(),
            "Не найден каталог 'docs/' ни в текущей директории, ни выше по дереву. " +
            "Используйте '--root=<path>' для явного указания корня проекта.");
        return false;
    }

    private sealed record ParamValidationError(string Code, string Message);

    private sealed record RootError(string Code, string Path, string Message);
}
