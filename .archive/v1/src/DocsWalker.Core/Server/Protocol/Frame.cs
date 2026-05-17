using System.Text;

namespace DocsWalker.Core.Server.Protocol;

/// <summary>
/// Newline-delimited UTF-8 JSON frame. Каждый кадр — одна строка JSON + \n.
/// </summary>
public static class Frame
{
    private const int MaxFrameBytes = 16 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, string json, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// Читает один кадр (до \n включительно). Возвращает null при чистом EOF.
    /// </summary>
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct = default)
    {
        var buf = new List<byte>(256);
        var oneByte = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(oneByte, ct);
            if (read == 0) return null;
            if (oneByte[0] == '\n') break;
            buf.Add(oneByte[0]);
            if (buf.Count > MaxFrameBytes)
                throw new InvalidOperationException($"Кадр превышает {MaxFrameBytes} байт.");
        }
        return Encoding.UTF8.GetString(buf.ToArray());
    }
}
