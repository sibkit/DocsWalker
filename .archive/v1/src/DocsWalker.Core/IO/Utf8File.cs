using System.Text;

namespace DocsWalker.Core.IO;

internal static class Utf8File
{
    private static readonly UTF8Encoding StrictUtf8NoBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string ReadAllTextStrict(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var offset = HasUtf8Bom(bytes) ? 3 : 0;
        return StrictUtf8NoBom.GetString(bytes, offset, bytes.Length - offset);
    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3
        && bytes[0] == 0xEF
        && bytes[1] == 0xBB
        && bytes[2] == 0xBF;
}
