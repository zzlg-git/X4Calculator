using System.IO.Compression;

namespace X4Calculator.Core.Parsing;

/// <summary>统一打开 X4 的原始 XML 或 gzip 压缩存档。</summary>
internal static class SavegameFile
{
    public static Stream OpenRead(string path)
    {
        var file = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024, useAsync: true);

        try
        {
            Span<byte> signature = stackalloc byte[2];
            var bytesRead = file.Read(signature);
            file.Position = 0;
            return bytesRead == 2 && signature[0] == 0x1f && signature[1] == 0x8b
                ? new GZipStream(file, CompressionMode.Decompress)
                : file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }
}
