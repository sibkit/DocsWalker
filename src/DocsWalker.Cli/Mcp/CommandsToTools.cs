using System.Text.Json.Nodes;
using DocsWalker.Cli.Cli;
using DocsWalker.Core.Mcp;
using DocsWalker.Core.Schema;

namespace DocsWalker.Cli.Mcp;

/// <summary>
/// Конвертирует <see cref="Commands.All"/> CLI-команд в манифест MCP-tools.
/// Команда <c>run</c> исключается — это серверная сама-по-себе команда, через
/// MCP её вызывать не предполагается. Все остальные read+write команды доступны
/// LLM как MCP-tools 1:1 с CLI ((#364) docs/DocsWalker.yml).
/// <para>
/// Для tool'ов с динамическими параметрами (см. <see cref="CommandSpec.DynamicParams"/>) —
/// сейчас это только <c>create-node</c> — при наличии загруженной проектной Схемы
/// собирается inputSchema с перечислением всех известных полей и таблицей
/// required-наборов по типам в description (см. <see cref="BuildCreateNodeInputSchema"/>
/// и docs/DocsWalker.yml/«(#377)»). Если Схема не передана (null) — descriptor
/// отдаётся с базовой схемой, сгенерированной из статических <see cref="CommandSpec.Params"/>.
/// </para>
/// </summary>
internal static class CommandsToTools
{
    public static IReadOnlyList<McpToolDescriptor> Build(SchemaDocument? schema = null)
    {
        var list = new List<McpToolDescriptor>();
        foreach (var spec in Commands.All)
        {
            if (spec.KebabName == "run") continue;
            // mcp-server тоже исключаем — сама себя как tool регистрировать незачем.
            if (spec.KebabName == "mcp-server") continue;

            var parameters = new List<McpToolParam>(spec.Params.Count);
            foreach (var p in spec.Params)
            {
                var (jsonType, itemsType) = MapParamType(p.Type);
                parameters.Add(new McpToolParam(
                    Name: p.KebabName,
                    JsonType: jsonType,
                    Required: p.Required,
                    Description: p.Description,
                    ItemsJsonType: itemsType));
            }

            // Универсальный --dry-run для write-команд (на уровне Dispatcher,
            // не часть CommandSpec.Params). --root убран в stg-0010 step-06:
            // клиент про FS не знает, kernel инжектит storage-path сам.
            if (spec.Kind == CommandKind.Write)
            {
                parameters.Add(new McpToolParam(
                    Name: "dry-run",
                    JsonType: "boolean",
                    Required: false,
                    Description: "true → не записывать на FS, вернуть applied=false. По умолчанию false."));
            }

            var description = spec.Description ?? $"CLI-команда {spec.KebabName}.";
            if (spec.Examples is { Count: > 0 })
            {
                description += "\n\nПримеры CLI:\n" + string.Join("\n", spec.Examples);
            }

            JsonObject? rawSchema = null;
            if (spec.KebabName == "create-node" && schema is not null)
            {
                rawSchema = BuildCreateNodeInputSchema(schema);
            }

            list.Add(new McpToolDescriptor(
                Name: spec.KebabName,
                Description: description,
                Params: parameters,
                RawInputSchema: rawSchema));
        }
        return list;
    }

    private static (string JsonType, string? ItemsType) MapParamType(ParamType type) => type switch
    {
        ParamType.String    => ("string", null),
        ParamType.Integer   => ("integer", null),
        ParamType.IdList    => ("array", "integer"),
        ParamType.Json      => ("object", null),
        // Array of object: MCP-клиент шлёт arguments.<name>=[...]
        // напрямую (а не через escape-string). Конвертер McpServer.JsonValueToCliString
        // распознаёт пару (array, object) и передаёт raw JSON со скобками в CLI.
        ParamType.JsonArray => ("array", "object"),
        _ => ("string", null),
    };

    /// <summary>
    /// Строит inputSchema для <c>create-node</c> по проектной Схеме.
    /// Контракт — docs/DocsWalker.yml/«(#377) inputSchema динамических tool»:
    /// единый JSON-Schema object, в котором <c>properties</c> перечисляет все
    /// известные поля (type-enum, title, text, все имена связей всех типов как
    /// optional с корректным JSON-типом по cardinality), плюс универсальный
    /// dry-run. <c>required</c> на верхнем уровне — статически <c>[type, title]</c>;
    /// per-type required (text при text_required=true, path-ref, прочие required
    /// out_refs) транслируются в текстовую таблицу внутри <c>description</c>
    /// корневой схемы и поля <c>type</c> — это обходной путь, т.к. Anthropic
    /// API запрещает <c>oneOf</c>/<c>allOf</c>/<c>anyOf</c> на верхнем уровне
    /// inputSchema, а <c>if/then/else</c> и <c>dependentRequired</c> не
    /// поддерживает. Источник истины для required по типу — серверный
    /// валидатор create-node в ядре. Тип <c>root</c> в enum не включается —
    /// он синтезируется ядром, через create-node не создаётся (#376).
    /// </summary>
    internal static JsonObject BuildCreateNodeInputSchema(SchemaDocument schema)
    {
        // Все типы кроме root (root — синтезируется ядром).
        var creatableTypes = schema.Types
            .Where(t => !string.Equals(t.Name, "root", StringComparison.Ordinal))
            .ToArray();

        // Сбор всех уникальных имён связей по всем типам с их cardinality
        // (для определения JSON-типа в properties: integer vs array+items=integer).
        // Источник description — первая встреченная RefDef с этим именем.
        var refsByName = new SortedDictionary<string, RefDef>(StringComparer.Ordinal);
        foreach (var t in schema.Types)
        {
            foreach (var rd in t.OutRefs)
            {
                if (!refsByName.ContainsKey(rd.Name))
                {
                    refsByName[rd.Name] = rd;
                }
            }
        }

        // Per-type required-наборы — строятся один раз и используются и в
        // description корневой схемы (таблица), и в description поля type.
        var requiredByType = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var t in creatableTypes)
        {
            var requiredForType = new List<string> { "type", "title" };
            if (t.TextRequired) requiredForType.Add("text");
            foreach (var rd in t.OutRefs)
            {
                if (rd.Required && !requiredForType.Contains(rd.Name))
                {
                    requiredForType.Add(rd.Name);
                }
            }
            requiredByType[t.Name] = requiredForType;
        }

        var requiredTable = string.Join(
            "\n",
            creatableTypes.Select(t => $"  - {t.Name}: [{string.Join(", ", requiredByType[t.Name])}]"));

        // Базовые properties: type-enum, title, text, далее все ref-имена,
        // плюс универсальный dry-run.
        var typeEnum = new JsonArray();
        foreach (var t in creatableTypes) typeEnum.Add((JsonNode?)t.Name);

        var properties = new JsonObject
        {
            ["type"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = typeEnum,
                ["description"] = "Имя типа узла из проектной Схемы. Required-набор полей по типу:\n" + requiredTable,
            },
            ["title"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Title узла — для типов с title_source=filename/dirname кладётся в FS-имя файла/каталога; для inline_key — ключ в YAML родителя.",
            },
            ["text"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Текст узла. Обязателен для типов с text_required=true (см. таблицу required по типу в описании поля type).",
            },
        };

        foreach (var (name, rd) in refsByName)
        {
            JsonObject prop;
            if (rd.Cardinality == Cardinality.Many)
            {
                prop = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "integer" },
                    ["description"] = rd.Description ?? $"id-list для связи '{name}' (cardinality=many).",
                };
            }
            else
            {
                prop = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = rd.Description ?? $"id для связи '{name}' (cardinality=one).",
                };
            }
            properties[name] = prop;
        }

        properties["dry-run"] = new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = "true → не записывать на FS, вернуть applied=false. По умолчанию false.",
        };

        // Top-level required — статически [type, title]. Per-type required
        // фиксируется текстом в description и серверной валидацией.
        var rootRequired = new JsonArray { (JsonNode?)"type", (JsonNode?)"title" };

        var description =
            "Создать узел. Required-набор полей зависит от type — серверный валидатор отвергнет создание при недостаче. " +
            "Required по типу:\n" + requiredTable +
            "\nСм. (#377) docs/DocsWalker.yml.";

        return new JsonObject
        {
            ["type"] = "object",
            ["description"] = description,
            ["properties"] = properties,
            ["required"] = rootRequired,
        };
    }
}
