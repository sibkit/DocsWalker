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
    /// </summary>
    /// <param name="root">Каталог проекта (то, что после <see cref="Dispatcher"/>'а
    /// разрешилось через <c>--root</c>).</param>
    /// <param name="typeName">Имя типа (как передал пользователь в <c>--type=</c>).
    /// Может быть null/пустым — тогда возвращаем null.</param>
    public static JsonNode? TryDescribeType(string root, string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;
        try
        {
            var schemaPath = System.IO.Path.Combine(root, "docs", "Схема.yml");
            var schema = SchemaLoader.LoadSchema(schemaPath);
            var dto = ReadApi.DescribeType(schema, typeName);
            return ReadApiJson.TypeDescriptionToJson(dto);
        }
        catch
        {
            // Любая ошибка — Схема не загружается, тип не найден, FS-проблема — поглощаем.
            // Это enrichment, он не должен подавлять основную ошибку, к которой прицеплен.
            return null;
        }
    }
}
