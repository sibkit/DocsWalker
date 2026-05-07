using System.Text.Json.Nodes;
using DocsWalker.Core.Api;
using DocsWalker.Core.Schema;

namespace DocsWalker.Cli.Cli;

/// <summary>
/// Хелпер для встраивания ответа <c>describe-type</c> в ошибки CLI: при missing/invalid
/// параметрах write-команды LLM получает не только сообщение «параметр такой-то обязателен»,
/// но и FS-агностичный контракт типа — список required out_refs с target_types и cardinality.
///
/// Архитектурный выбор: не вводим отдельную сущность «контракт» — переиспользуем готовый
/// шейп <see cref="ReadApi.DescribeType"/> + <see cref="ReadApiJson.TypeDescriptionToJson"/>.
/// Один источник истины описания типа; LLM узнаёт знакомый формат без дополнительной
/// документации.
///
/// Загрузка Схемы — ленивая: вызывается только когда есть смысл (write-команда + известный
/// тип). Если Схема не загружается или тип не найден — возвращаем <c>null</c>, ошибка
/// идёт без поля <c>describe_type</c>.
/// </summary>
internal static class ErrorEnrichment
{
    /// <summary>
    /// Пытается вернуть JSON-описание типа из Схемы проекта <paramref name="root"/>.
    /// При заданном <paramref name="focusRefName"/> массив <c>out_refs</c> отфильтрован
    /// до записей с этим именем (одна, если связь объявлена в типе; пустой массив, если
    /// LLM передала несуществующее имя — это и сигнал «такой связи у типа нет»). Шапка
    /// типа (<c>name</c>, <c>description</c>, <c>text_required</c>) сохраняется. Trim
    /// нужен ref-локализованным ошибкам (<c>missing_required_ref</c>, <c>invalid_ref_value</c>
    /// и аналоги): LLM получает контракт ровно проблемной связи без шума остальных.
    /// </summary>
    /// <param name="root">Каталог проекта (то, что после <see cref="Dispatcher"/>'а
    /// разрешилось через <c>--root</c>).</param>
    /// <param name="typeName">Имя типа (как передал пользователь в <c>--type=</c>).
    /// Может быть null/пустым — тогда возвращаем null.</param>
    /// <param name="focusRefName">Имя проблемной связи; null — полный describe_type.</param>
    public static JsonNode? TryDescribeType(string root, string? typeName, string? focusRefName = null)
    {
        if (string.IsNullOrEmpty(typeName)) return null;
        try
        {
            var schemaPath = System.IO.Path.Combine(root, "docs", "Схема.yml");
            var schema = SchemaLoader.LoadSchema(schemaPath);
            var dto = ReadApi.DescribeType(schema, typeName);
            var json = ReadApiJson.TypeDescriptionToJson(dto);
            if (focusRefName is not null)
                TrimOutRefsToFocus(json, focusRefName);
            return json;
        }
        catch
        {
            // Любая ошибка — Схема не загружается, тип не найден, FS-проблема — поглощаем.
            // Это enrichment, он не должен подавлять основную ошибку, к которой прицеплен.
            return null;
        }
    }

    /// <summary>
    /// Оставляет в <c>out_refs</c> результата describe-type только записи с именем
    /// <paramref name="focusRefName"/>. Если такой записи нет — массив становится пустым
    /// (это и есть сигнал «такой связи у типа нет», достаточный по контракту LLM-guide).
    /// </summary>
    private static void TrimOutRefsToFocus(JsonNode describeType, string focusRefName)
    {
        if (describeType is not JsonObject obj) return;
        if (obj["out_refs"] is not JsonArray refs) return;

        var kept = new JsonArray();
        foreach (var item in refs)
        {
            if (item is JsonObject r && (string?)r["name"] == focusRefName)
                kept.Add(r.DeepClone());
        }
        obj["out_refs"] = kept;
    }
}
