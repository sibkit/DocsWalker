using System.Text;
using System.Text.Json;
using DocsWalker.Cli.Cli;
using DocsWalker.Cli.Cli.Handlers;

// JSON-вывод DocsWalker — всегда UTF-8 без BOM. На Windows Console.Out по умолчанию
// использует кодовую страницу консоли (CP866/CP1251), что искажает кириллицу при
// прямом перехвате stdout/stderr (LLM, CI, файловый редирект). Устанавливаем явно.
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

return Dispatcher.Run(args);

internal static class Dispatcher
{
    /// <summary>
    /// Имена write-команд: для них валиден общий флаг <c>--dry-run</c>. Read-команды
    /// получают <c>unknown_parameter</c>, если этот флаг им передан, — это явный сигнал
    /// LLM, что dry-run применим только к командам с побочным эффектом.
    /// </summary>
    private static readonly HashSet<string> WriteCommands = new(StringComparer.Ordinal)
    {
        "create_node", "update_node", "delete_nodes", "move_node",
        "create_ref", "delete_ref", "redirect_refs", "transaction",
    };

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

        if (!TryResolveDryRun(spec, parsed.Params, out var dryRun, out var dryRunError))
        {
            Output.WriteError(dryRunError!.Code, path: null, dryRunError.Message, dryRunError.Hint);
            return 1;
        }

        return spec.SnakeName switch
        {
            "get_meta_schema" => SchemaHandlers.GetMetaSchema(rootPath),
            "get_schema"      => SchemaHandlers.GetSchema(rootPath),
            "get_map"         => ReadHandlers.GetMap(rootPath),
            "get_nodes"       => ReadHandlers.GetNodes(rootPath, parsed.Params["ids"]),
            "get_by_path"     => ReadHandlers.GetByPath(rootPath, parsed.Params["path"]),
            "get_subtree"     => ReadHandlers.GetSubtree(
                                    rootPath,
                                    int.Parse(parsed.Params["id"], System.Globalization.CultureInfo.InvariantCulture),
                                    parsed.Params.TryGetValue("tree", out var ts) ? ts : null),
            "get_ancestors"   => ReadHandlers.GetAncestors(
                                    rootPath,
                                    int.Parse(parsed.Params["id"], System.Globalization.CultureInfo.InvariantCulture),
                                    parsed.Params.TryGetValue("tree", out var ta) ? ta : null),
            "get_refs"        => ReadHandlers.GetRefs(
                                    rootPath,
                                    int.Parse(parsed.Params["id"], System.Globalization.CultureInfo.InvariantCulture),
                                    parsed.Params.TryGetValue("name", out var n1) ? n1 : null),
            "get_in_refs"     => ReadHandlers.GetInRefs(
                                    rootPath,
                                    int.Parse(parsed.Params["id"], System.Globalization.CultureInfo.InvariantCulture),
                                    parsed.Params.TryGetValue("name", out var n2) ? n2 : null),
            "search"          => ReadHandlers.Search(rootPath, parsed.Params["query"]),
            "check_integrity" => ReadHandlers.CheckIntegrity(rootPath),
            "create_node"     => WriteHandlers.CreateNode(rootPath, parsed.Params, dryRun),
            "update_node"     => WriteHandlers.UpdateNode(rootPath, parsed.Params, dryRun),
            "delete_nodes"    => WriteHandlers.DeleteNodes(rootPath, parsed.Params, dryRun),
            "move_node"       => WriteHandlers.MoveNode(rootPath, parsed.Params, dryRun),
            "create_ref"      => WriteHandlers.CreateRef(rootPath, parsed.Params, dryRun),
            "delete_ref"      => WriteHandlers.DeleteRef(rootPath, parsed.Params, dryRun),
            "redirect_refs"   => WriteHandlers.RedirectRefs(rootPath, parsed.Params, dryRun),
            "transaction"     => WriteHandlers.Transaction(rootPath, parsed.Params, dryRun),
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
        // Неизвестные параметры (общие --root и --dry-run исключаем).
        // --dry-run проверяется отдельно (TryResolveDryRun) — там же он отвергается,
        // если передан read-команде.
        // Для динамических команд (например, create-node, у которой имена
        // out_refs-параметров берутся из контракта типа в Схеме) проверка
        // unknown-параметра пропускается — handler сам разбирается, что
        // делать с не-фиксированными ключами.
        if (!spec.DynamicParams)
        {
            foreach (var key in provided.Keys)
            {
                if (key == "root" || key == "dry-run")
                    continue;
                if (!HasParam(spec, key))
                {
                    return new ParamValidationError(
                        "unknown_parameter",
                        $"Неизвестный параметр '--{key}' для команды '{spec.KebabName}'.");
                }
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

    /// <summary>
    /// Разбирает общий флаг <c>--dry-run=true|false</c>. Допустимые значения:
    /// <c>true</c>/<c>1</c> и <c>false</c>/<c>0</c> (регистр не важен). Любое другое
    /// значение → <c>invalid_parameter</c>. Если флаг передан read-команде —
    /// <c>unknown_parameter</c> (dry-run применим только к командам с побочным
    /// эффектом). Для write-команд по умолчанию false.
    /// </summary>
    private static bool TryResolveDryRun(
        CommandSpec spec,
        IReadOnlyDictionary<string, string> args,
        out bool dryRun,
        out DryRunError? error)
    {
        dryRun = false;
        if (!args.TryGetValue("dry-run", out var raw))
        {
            error = null;
            return true;
        }

        if (!WriteCommands.Contains(spec.SnakeName))
        {
            error = new DryRunError(
                "unknown_parameter",
                $"Параметр '--dry-run' не применим к команде '{spec.KebabName}' (только для write-команд).",
                "Read-команды не имеют побочного эффекта; dry-run для них не определён.");
            return false;
        }

        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || raw == "1")
        {
            dryRun = true;
            error = null;
            return true;
        }
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) || raw == "0")
        {
            dryRun = false;
            error = null;
            return true;
        }

        error = new DryRunError(
            "invalid_parameter",
            $"Параметр '--dry-run': ожидается 'true' или 'false', получено '{raw}'.",
            null);
        return false;
    }

    private sealed record ParamValidationError(string Code, string Message);

    private sealed record RootError(string Code, string Path, string Message);

    private sealed record DryRunError(string Code, string Message, string? Hint);
}
