using System.Text;
using System.Text.Json;
using DocsWalker.Cli.Cli;
using DocsWalker.Cli.Cli.Handlers;
using DocsWalker.Cli.Cli.Kernel;
using DocsWalker.Core.Api;

// JSON-вывод DocsWalker — всегда UTF-8 без BOM. На Windows Console.Out по умолчанию
// использует кодовую страницу консоли (CP866/CP1251), что искажает кириллицу при
// прямом перехвате stdout/stderr (LLM, CI, файловый редирект). Устанавливаем явно.
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// Нет аргументов или первый аргумент — параметр: отдаём Dispatcher'у.
// Это ошибки argv (no_command, --help, --version), не нужен сервер — выдаём сразу.
if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
    return Dispatcher.Run(args);

var cmd = args[0].Replace('_', '-');

// cmd == "repl" → интерактивный HTTP-клиент к ядру: не идёт через одноразовый
// клиент-режим (KernelHttpClient), живёт до выхода REPL, сам читает
// .dw/client.json и держит HttpClient к ядру.
// Команда `kernel` — отдельный exe DocsWalker.Kernel.exe.
// Команда `mcp-server` — отдельный exe DocsWalker.Mcp.exe (вынесена в
// stg-0011 code-mcp-project-split).
if (cmd == "repl")
    return Dispatcher.Run(args);

// Любая другая команда → клиент-режим: читаем .dw/client.json, форвардим
// в /{graph} kernel'а. Auto-spawn убран в stg-0010 step-04 — kernel
// должен быть уже запущен пользователем.
ClientConfig clientCfg;
try { clientCfg = ClientConfig.Resolve(); }
catch (ClientConfigException ex)
{
    Output.WriteError(ex.Code, path: null, ex.Message);
    return 1;
}
return await KernelHttpClient.SendCommandAsync(args, clientCfg);

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

        // storage-path для repl не нужен (wrapper сам читает ClientConfig). Для
        // остальных команд — обязателен; kernel инжектит его через
        // --storage-path=<docs-folder> до вызова Dispatcher.Run. Прямой запуск
        // CLI с этими командами без --storage-path = ошибка контракта.
        // mcp_server вынесен в DocsWalker.Mcp.exe (stg-0011 code-mcp-project-split).
        string storagePath = string.Empty;
        if (spec.SnakeName != "repl")
        {
            if (!TryResolveStoragePath(parsed.Params, out storagePath, out var spError))
            {
                Output.WriteError(spError!.Code, spError.Path, spError.Message);
                return 1;
            }
        }

        if (TryValidateParams(spec, parsed.Params) is { } validationError)
        {
            var enrichment = spec.Kind == CommandKind.Write
                ? ErrorEnrichment.TryDescribeType(storagePath, parsed.Params.GetValueOrDefault("type"))
                : null;
            Output.WriteError(validationError.Code, path: null, validationError.Message, describeType: enrichment);
            return 1;
        }

        if (!TryResolveDryRun(spec, parsed.Params, out var dryRun, out var dryRunError))
        {
            Output.WriteError(dryRunError!.Code, path: null, dryRunError.Message, dryRunError.Hint);
            return 1;
        }

        return spec.SnakeName switch
        {
            "repl"            => ReplHandler.Run(parsed.Params),
            "get_meta_schema" => SchemaHandlers.GetMetaSchema(storagePath),
            "get_schema"      => SchemaHandlers.GetSchema(storagePath),
            "describe_type"   => SchemaHandlers.DescribeType(storagePath, parsed.Params["name"]),
            "get_usage_guide" => SchemaHandlers.GetUsageGuide(
                                    storagePath,
                                    parsed.Params.TryGetValue("command", out var cmdFilter) ? cmdFilter : null,
                                    parsed.Params.TryGetValue("fields", out var fieldsFilter) ? fieldsFilter : null),
            "get_nodes"       => DispatchGetNodes(storagePath, parsed.Params),
            "get_by_path"     => DispatchGetByPath(storagePath, parsed.Params),
            "get_tree"        => DispatchGetTree(storagePath, parsed.Params),
            "get_ancestors"   => ReadHandlers.GetAncestors(
                                    storagePath,
                                    int.Parse(parsed.Params["id"], System.Globalization.CultureInfo.InvariantCulture),
                                    parsed.Params.TryGetValue("tree", out var ta) ? ta : null),
            "get_refs"        => ReadHandlers.GetRefs(
                                    storagePath,
                                    int.Parse(parsed.Params["id"], System.Globalization.CultureInfo.InvariantCulture),
                                    parsed.Params.TryGetValue("name", out var n1) ? n1 : null),
            "get_in_refs"     => ReadHandlers.GetInRefs(
                                    storagePath,
                                    int.Parse(parsed.Params["id"], System.Globalization.CultureInfo.InvariantCulture),
                                    parsed.Params.TryGetValue("name", out var n2) ? n2 : null),
            "search"          => DispatchSearch(storagePath, parsed.Params),
            "find"            => DispatchFind(storagePath, parsed.Params),
            "check_integrity" => ReadHandlers.CheckIntegrity(storagePath),
            "get_overview"    => ReadHandlers.GetOverview(storagePath),
            "create_node"     => WriteHandlers.CreateNode(storagePath, parsed.Params, dryRun),
            "update_node"     => WriteHandlers.UpdateNode(storagePath, parsed.Params, dryRun),
            "delete_nodes"    => WriteHandlers.DeleteNodes(storagePath, parsed.Params, dryRun),
            "move_node"       => WriteHandlers.MoveNode(storagePath, parsed.Params, dryRun),
            "create_ref"      => WriteHandlers.CreateRef(storagePath, parsed.Params, dryRun),
            "delete_ref"      => WriteHandlers.DeleteRef(storagePath, parsed.Params, dryRun),
            "redirect_refs"   => WriteHandlers.RedirectRefs(storagePath, parsed.Params, dryRun),
            "update_schema"   => WriteHandlers.UpdateSchema(storagePath, parsed.Params, dryRun),
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

    private const int DefaultMaxTokens = 50000;

    private static int DispatchGetTree(string storagePath, IReadOnlyDictionary<string, string> args)
    {
        var id = int.Parse(args["id"], System.Globalization.CultureInfo.InvariantCulture);
        var tree = args.TryGetValue("tree", out var ts) ? ts : null;
        int? depth = args.TryGetValue("depth", out var ds)
            ? int.Parse(ds, System.Globalization.CultureInfo.InvariantCulture)
            : (int?)null;

        if (!TryParseBoolOpt(args, "compact", out bool compact, out string? compactErr))
        {
            Output.WriteError("invalid_parameter", path: null, compactErr!);
            return 1;
        }
        var fields = ResolveFields(args, compact);
        if (!TryParseMaxTokens(args, out int maxTokens, out string? mtErr))
        {
            Output.WriteError("invalid_parameter", path: null, mtErr!);
            return 1;
        }

        return ReadHandlers.GetTree(storagePath, id, tree, depth, fields, maxTokens);
    }

    private static int DispatchGetByPath(string storagePath, IReadOnlyDictionary<string, string> args)
    {
        var tree = args.TryGetValue("tree", out var ts) ? ts : null;
        int? depth = args.TryGetValue("depth", out var ds)
            ? int.Parse(ds, System.Globalization.CultureInfo.InvariantCulture)
            : (int?)null;

        if (!TryParseBoolOpt(args, "compact", out bool compact, out string? compactErr))
        {
            Output.WriteError("invalid_parameter", path: null, compactErr!);
            return 1;
        }
        var fields = ResolveFields(args, compact);
        if (!TryParseMaxTokens(args, out int maxTokens, out string? mtErr))
        {
            Output.WriteError("invalid_parameter", path: null, mtErr!);
            return 1;
        }

        return ReadHandlers.GetByPath(storagePath, args["path"], tree, depth, fields, maxTokens);
    }

    private static int DispatchGetNodes(string storagePath, IReadOnlyDictionary<string, string> args)
    {
        if (!TryParseBoolOpt(args, "compact", out bool compact, out string? compactErr))
        {
            Output.WriteError("invalid_parameter", path: null, compactErr!);
            return 1;
        }
        var fields = ResolveFields(args, compact);
        if (!TryParseMaxTokens(args, out int maxTokens, out string? mtErr))
        {
            Output.WriteError("invalid_parameter", path: null, mtErr!);
            return 1;
        }

        return ReadHandlers.GetNodes(storagePath, args["ids"], fields, maxTokens);
    }

    private static IReadOnlyCollection<string>? ResolveFields(
        IReadOnlyDictionary<string, string> args,
        bool compact)
    {
        var explicitFields = ParseFields(args);
        if (explicitFields is not null) return explicitFields;
        if (!compact) return null;
        return new HashSet<string>(StringComparer.Ordinal) { "id", "type", "title" };
    }

    private static bool TryParseMaxTokens(
        IReadOnlyDictionary<string, string> args,
        out int maxTokens,
        out string? error)
    {
        maxTokens = DefaultMaxTokens;
        error = null;
        if (!args.TryGetValue("max-tokens", out var raw)) return true;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"Параметр '--max-tokens': ожидается целое число, получено '{raw}'.";
            return false;
        }
        if (parsed <= 0)
        {
            error = $"Параметр '--max-tokens' должен быть положительным, получено '{raw}'.";
            return false;
        }
        maxTokens = parsed;
        return true;
    }

    private static int DispatchSearch(string storagePath, IReadOnlyDictionary<string, string> args)
    {
        var query = args["query"];

        SearchInMode inMode = SearchInMode.Both;
        if (args.TryGetValue("in", out var inRaw))
        {
            switch (inRaw)
            {
                case "title": inMode = SearchInMode.Title; break;
                case "text":  inMode = SearchInMode.Text;  break;
                case "both":  inMode = SearchInMode.Both;  break;
                default:
                    Output.WriteError(
                        "invalid_parameter",
                        path: null,
                        $"Параметр '--in': ожидается одно из 'title', 'text', 'both'. Получено: '{inRaw}'.");
                    return 1;
            }
        }

        string? typeFilter = args.TryGetValue("type", out var tf) && !string.IsNullOrEmpty(tf) ? tf : null;
        string? tree       = args.TryGetValue("tree", out var tr) && !string.IsNullOrEmpty(tr) ? tr : null;
        int? under = args.TryGetValue("under", out var u)
            ? int.Parse(u, System.Globalization.CultureInfo.InvariantCulture)
            : (int?)null;
        if (!TryParseBoolOpt(args, "regex", out bool regex, out string? regexErr))
        {
            Output.WriteError("invalid_parameter", path: null, regexErr!);
            return 1;
        }
        int? limit = args.TryGetValue("limit", out var l)
            ? int.Parse(l, System.Globalization.CultureInfo.InvariantCulture)
            : (int?)null;
        if (!TryParseBoolOpt(args, "compact", out bool compact, out string? compactErr))
        {
            Output.WriteError("invalid_parameter", path: null, compactErr!);
            return 1;
        }

        List<TreeFilter>? inTree = null;
        if (args.TryGetValue("in-tree", out var inTreeRaw) && !string.IsNullOrEmpty(inTreeRaw))
        {
            if (!TryParseInTree(inTreeRaw, out inTree, out var inTreeError))
            {
                Output.WriteError("invalid_parameter", path: null, inTreeError!);
                return 1;
            }
        }

        return ReadHandlers.Search(storagePath, query, inMode, typeFilter, tree, under, regex, limit, compact, inTree);
    }

    private static int DispatchFind(string storagePath, IReadOnlyDictionary<string, string> args)
    {
        if (!TryParseInTree(args["in-tree"], out var filters, out var parseError))
        {
            Output.WriteError("invalid_parameter", path: null, parseError!);
            return 1;
        }

        string? typeFilter = args.TryGetValue("type", out var tf) && !string.IsNullOrEmpty(tf) ? tf : null;
        int? limit = args.TryGetValue("limit", out var l)
            ? int.Parse(l, System.Globalization.CultureInfo.InvariantCulture)
            : (int?)null;
        if (!TryParseBoolOpt(args, "compact", out bool compact, out string? compactErr))
        {
            Output.WriteError("invalid_parameter", path: null, compactErr!);
            return 1;
        }

        return ReadHandlers.Find(storagePath, filters!, typeFilter, limit, compact);
    }

    /// <summary>
    /// Разбирает значение параметра <c>--in-tree</c> (raw JSON-массив объектов
    /// <c>{name, under}</c>) в список <see cref="TreeFilter"/>. Возвращает false
    /// при синтаксических ошибках; <paramref name="error"/> содержит готовое к
    /// выводу сообщение.
    /// </summary>
    private static bool TryParseInTree(
        string rawJson,
        out List<TreeFilter>? filters,
        out string? error)
    {
        filters = null;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                error = "Параметр --in-tree: ожидается JSON-массив объектов {name, under}.";
                return false;
            }
            var list = new List<TreeFilter>(root.GetArrayLength());
            for (int i = 0; i < root.GetArrayLength(); i++)
            {
                var el = root[i];
                if (el.ValueKind != JsonValueKind.Object)
                {
                    error = $"Параметр --in-tree[{i}]: ожидается объект {{name, under}}.";
                    return false;
                }
                if (!el.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                {
                    error = $"Параметр --in-tree[{i}].name: ожидается строка.";
                    return false;
                }
                if (!el.TryGetProperty("under", out var underProp) || underProp.ValueKind != JsonValueKind.Number)
                {
                    error = $"Параметр --in-tree[{i}].under: ожидается целое число.";
                    return false;
                }
                list.Add(new TreeFilter(nameProp.GetString()!, underProp.GetInt32()));
            }
            filters = list;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Параметр --in-tree: ошибка парсинга JSON — {ex.Message}";
            return false;
        }
    }

    private static bool TryParseBoolOpt(
        IReadOnlyDictionary<string, string> args,
        string key,
        out bool value,
        out string? error)
    {
        value = false;
        error = null;
        if (!args.TryGetValue(key, out var raw) || string.IsNullOrEmpty(raw)) return true;
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || raw == "1")
        {
            value = true;
            return true;
        }
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) || raw == "0")
        {
            value = false;
            return true;
        }
        error = $"Параметр '--{key}': ожидается 'true' или 'false'. Получено: '{raw}'.";
        return false;
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
                // Универсальные общие параметры — обрабатываются вне CommandSpec:
                // --storage-path → TryResolveStoragePath, --dry-run → TryResolveDryRun.
                if (key == "storage-path" || key == "dry-run")
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

    /// <summary>
    /// Разбирает <c>--fields=<csv></c> в whitelist для сериализации узлов. Имя <c>id</c>
    /// добавляем тихо: оно обязательно даже когда не указано — иначе ответ
    /// невозможно сопоставить с запросом. Невалидные имена (не из <see cref="ReadApiJson.AllNodeFields"/>)
    /// игнорируем; валидация формата возложена на ParamType.String на уровне CommandSpec —
    /// здесь работаем после неё.
    /// </summary>
    private static IReadOnlyCollection<string>? ParseFields(IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("fields", out var raw) || string.IsNullOrEmpty(raw)) return null;
        var set = new HashSet<string>(StringComparer.Ordinal) { "id" };
        foreach (var part in raw.Split(','))
        {
            var name = part.Trim();
            if (name.Length == 0) continue;
            set.Add(name);
        }
        return set;
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

            case ParamType.JsonArray:
                try
                {
                    using var doc = JsonDocument.Parse(value);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    {
                        error = $"ожидается JSON-массив, top-level — {doc.RootElement.ValueKind}.";
                        return false;
                    }
                    error = null;
                    return true;
                }
                catch (JsonException ex)
                {
                    error = $"ожидается корректный JSON-массив, ошибка разбора: {ex.Message}";
                    return false;
                }

            case ParamType.Boolean:
                if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1"
                    || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || value == "0")
                {
                    error = null;
                    return true;
                }
                error = $"ожидается 'true' или 'false', получено '{value}'.";
                return false;

            default:
                error = "неизвестный тип параметра.";
                return false;
        }
    }

    /// <summary>
    /// Резолвит storage-path из argv: единственный источник —
    /// <c>--storage-path=&lt;path&gt;</c> (kernel инжектит его перед вызовом
    /// Dispatcher.Run). Никакого upward-search'а от cwd: эта логика
    /// перенесена в <see cref="ClientConfig"/> на CLI top-level.
    /// </summary>
    private static bool TryResolveStoragePath(
        IReadOnlyDictionary<string, string> args,
        out string storagePath,
        out StoragePathError? error)
    {
        if (args.TryGetValue("storage-path", out var sp) && !string.IsNullOrWhiteSpace(sp))
        {
            if (!Directory.Exists(sp))
            {
                storagePath = string.Empty;
                error = new StoragePathError(
                    "storage_path_not_found",
                    sp,
                    $"Папка storage-path '{sp}' не существует.");
                return false;
            }
            storagePath = sp;
            error = null;
            return true;
        }

        storagePath = string.Empty;
        error = new StoragePathError(
            "missing_storage_path",
            Directory.GetCurrentDirectory(),
            "Параметр --storage-path=<path> обязателен; kernel инжектит его автоматически " +
            "при обращении к /<graph>. Прямой вызов CLI с graph-командами без " +
            "--storage-path — ошибка контракта.");
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

        if (spec.Kind != CommandKind.Write)
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

    private sealed record StoragePathError(string Code, string Path, string Message);

    private sealed record DryRunError(string Code, string Message, string? Hint);
}
