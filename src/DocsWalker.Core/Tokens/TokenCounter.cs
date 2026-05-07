using System.Text;
using DocsWalker.Core.Graph;
using Microsoft.ML.Tokenizers;

namespace DocsWalker.Core.Tokens;

/// <summary>
/// BPE-счётчик токенов на cl100k_base (модель gpt-4 / Claude совместима по порядку
/// величин). Используется для метрик tokens / subtree_tokens в get_map / get_subtree /
/// get_nodes — LLM прикидывает по ним бюджет ввода.
///
/// Один экземпляр Tokenizer'а — потокобезопасный read-only объект; держим как singleton,
/// чтобы не платить за загрузку vocab-таблицы повторно.
/// </summary>
public static class TokenCounter
{
    private static readonly Tokenizer Shared = TiktokenTokenizer.CreateForModel("gpt-4");

    /// <summary>
    /// Токены произвольной строки. Пустая/null строка → 0.
    /// </summary>
    public static int Count(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Shared.CountTokens(text);
    }

    /// <summary>
    /// Токены одного узла «как его видит LLM в get-nodes»: id+type+title+text+ключи и
    /// id'шники out_refs, склеенные в одну строку. Это аппроксимация шейпа JSON-ответа,
    /// достаточная для бюджет-планирования; точный JSON-сериализат не нужен — метрика
    /// должна быть стабильной к мелким стилевым правкам сериализатора.
    /// </summary>
    public static int CountNode(Node node)
    {
        var sb = new StringBuilder();
        sb.Append(node.Id);
        sb.Append(' ');
        sb.Append(node.TypeName);
        sb.Append(' ');
        sb.Append(node.Title);
        if (!string.IsNullOrEmpty(node.Text))
        {
            sb.Append(' ');
            sb.Append(node.Text);
        }
        foreach (var (name, ids) in node.OutRefs)
        {
            sb.Append(' ');
            sb.Append(name);
            foreach (var id in ids)
            {
                sb.Append(' ');
                sb.Append(id);
            }
        }
        return Shared.CountTokens(sb.ToString());
    }
}
