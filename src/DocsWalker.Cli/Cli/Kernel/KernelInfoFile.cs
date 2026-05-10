using System.Text;
using System.Text.Json;
using DocsWalker.Core.Store;

namespace DocsWalker.Cli.Cli.Kernel;

/// <summary>
/// Чтение и атомарная запись <c>kernel.json</c> — discovery-файла per-user ядра.
/// <para>
/// Запись через <see cref="AtomicWriter"/>: tmp-файл рядом + <c>File.Move</c> с
/// <c>overwrite=true</c>. Гарантирует, что клиент-читатель никогда не увидит частично
/// записанный JSON. POSIX <c>chmod 600</c> ставится после записи через
/// <see cref="KernelDiscovery.SetOwnerOnly"/>.
/// </para>
/// </summary>
internal static class KernelInfoFile
{
    /// <summary>
    /// Читает <c>kernel.json</c> или возвращает null при отсутствии / битом JSON.
    /// Не бросает — клиент должен сам решать, как реагировать (обычно: считать stale,
    /// перейти к spawn-race).
    /// </summary>
    public static KernelInfo? TryRead()
    {
        var path = KernelDiscovery.GetKernelInfoPath();
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize(text, KernelJsonContext.Default.KernelInfo);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Атомарно пишет <c>kernel.json</c>. Каталог создаёт при отсутствии; на POSIX
    /// выставляет owner-only mode (<c>0600</c>) после записи.
    /// </summary>
    public static void Write(KernelInfo info)
    {
        KernelDiscovery.EnsureDirExists();
        var path = KernelDiscovery.GetKernelInfoPath();
        var json = JsonSerializer.Serialize(info, KernelJsonContext.Default.KernelInfo);
        AtomicWriter.WriteAll(new[] { new AtomicWriteTarget(path, json) });
        KernelDiscovery.SetOwnerOnly(path);
    }

    /// <summary>
    /// Best-effort удаление <c>kernel.json</c> на graceful shutdown ядра.
    /// </summary>
    public static void DeleteIfExists()
    {
        try
        {
            var path = KernelDiscovery.GetKernelInfoPath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best-effort */ }
    }
}
