using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace X4Calculator.UI.Services;

/// <summary>从程序资源中的 gzip/DDS(DXT5)加载原版星图空间站图标。</summary>
public static class StationIconResources
{
    // X4 colors.xml: holomap_player 与 faction_player 均映射到 green_bright_weak_glow (77, 255, 77).
    private static readonly Color PlayerFactionColor = Color.FromRgb(77, 255, 77);
    private static readonly HashSet<string> SupportedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "mapob_constructionsite", "mapob_playerhq", "mapob_piratestation", "mapob_shipyard",
        "mapob_wharf", "mapob_equipmentdock", "mapob_factory", "mapob_tradestation",
        "mapob_defensestation", "mapob_shiptech", "mapob_hightech", "mapob_refined",
        "mapob_pharmaceutical", "mapob_food", "mapob_agricultural", "mapob_water", "mapob_energy",
        "mapob_hive", "mapob_weaponplatform", "mapob_vault_closed"
    };
    private static readonly Dictionary<(string IconKey, Color Tint), BitmapSource> Cache = new();

    public static BitmapSource? Get(string iconKey) => Get(iconKey, PlayerFactionColor);

    /// <summary>按指定的 X4 星图颜色为原版图标纹理着色。</summary>
    public static BitmapSource? Get(string iconKey, Color tint)
    {
        if (string.IsNullOrWhiteSpace(iconKey)) return null;
        var cacheKey = (iconKey.ToLowerInvariant(), tint);
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;
        if (!SupportedKeys.Contains(iconKey)) return null;

        System.Windows.Resources.StreamResourceInfo? resource;
        try
        {
            resource = Application.GetResourceStream(
                new Uri($"/X4Calculator;component/Assets/{iconKey}.gz", UriKind.Relative));
        }
        catch (IOException)
        {
            // 公开源码不附带 X4 的专有星图纹理。
            return null;
        }
        if (resource == null) return null;

        using (resource.Stream)
        using (var gzip = new GZipStream(resource.Stream, CompressionMode.Decompress))
        using (var memory = new MemoryStream())
        {
            gzip.CopyTo(memory);
            var bitmap = MultiplyTextureColor(DecodeDxt5(memory.ToArray()), tint);
            bitmap.Freeze();
            Cache[cacheKey] = bitmap;
            return bitmap;
        }
    }

    /// <summary>
    /// 复现 X4 xu_ui_unlit 的纹理着色：输出 RGB = 纹理 RGB × 势力色，Alpha 保持纹理值。
    /// mapob 图标不是纯 Alpha 蒙版；若丢弃纹理 RGB，会把六边形底、边框和内部符号涂成均匀色块。
    /// </summary>
    private static BitmapSource MultiplyTextureColor(BitmapSource source, Color color)
    {
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            // DecodeDxt5 返回 Pbgra32，RGB 已预乘 Alpha；逐通道乘颜色后仍满足预乘约束。
            pixels[offset] = (byte)(pixels[offset] * color.B / 255);
            pixels[offset + 1] = (byte)(pixels[offset + 1] * color.G / 255);
            pixels[offset + 2] = (byte)(pixels[offset + 2] * color.R / 255);
        }
        return BitmapSource.Create(source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY,
            PixelFormats.Pbgra32, null, pixels, source.PixelWidth * 4);
    }

    private static BitmapSource DecodeDxt5(byte[] dds)
    {
        if (dds.Length < 128 || dds[0] != (byte)'D' || dds[1] != (byte)'D' ||
            dds[2] != (byte)'S' || dds[3] != (byte)' ' ||
            System.Text.Encoding.ASCII.GetString(dds, 84, 4) != "DXT5")
            throw new InvalidDataException("空间站图标不是受支持的 DXT5 DDS。 ");

        var height = BitConverter.ToInt32(dds, 12);
        var width = BitConverter.ToInt32(dds, 16);
        var pixels = new byte[width * height * 4];
        var offset = 128;

        for (var blockY = 0; blockY < (height + 3) / 4; blockY++)
        for (var blockX = 0; blockX < (width + 3) / 4; blockX++)
        {
            if (offset + 16 > dds.Length) throw new InvalidDataException("DXT5 图标数据不完整。");
            DecodeBlock(dds, offset, pixels, width, height, blockX * 4, blockY * 4);
            offset += 16;
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
    }

    private static void DecodeBlock(
        byte[] source, int offset, byte[] target, int width, int height, int startX, int startY)
    {
        var alpha = new byte[8];
        alpha[0] = source[offset];
        alpha[1] = source[offset + 1];
        if (alpha[0] > alpha[1])
        {
            for (var i = 1; i <= 6; i++) alpha[i + 1] = (byte)(((7 - i) * alpha[0] + i * alpha[1]) / 7);
        }
        else
        {
            for (var i = 1; i <= 4; i++) alpha[i + 1] = (byte)(((5 - i) * alpha[0] + i * alpha[1]) / 5);
            alpha[6] = 0;
            alpha[7] = 255;
        }

        ulong alphaBits = 0;
        for (var i = 0; i < 6; i++) alphaBits |= (ulong)source[offset + 2 + i] << (8 * i);

        var color0 = BitConverter.ToUInt16(source, offset + 8);
        var color1 = BitConverter.ToUInt16(source, offset + 10);
        var colors = new (byte R, byte G, byte B)[4];
        colors[0] = Rgb565(color0);
        colors[1] = Rgb565(color1);
        colors[2] = ((byte)((2 * colors[0].R + colors[1].R) / 3),
                     (byte)((2 * colors[0].G + colors[1].G) / 3),
                     (byte)((2 * colors[0].B + colors[1].B) / 3));
        colors[3] = ((byte)((colors[0].R + 2 * colors[1].R) / 3),
                     (byte)((colors[0].G + 2 * colors[1].G) / 3),
                     (byte)((colors[0].B + 2 * colors[1].B) / 3));
        var colorBits = BitConverter.ToUInt32(source, offset + 12);

        for (var pixel = 0; pixel < 16; pixel++)
        {
            var x = startX + pixel % 4;
            var y = startY + pixel / 4;
            if (x >= width || y >= height) continue;

            var a = alpha[(int)((alphaBits >> (3 * pixel)) & 7)];
            var c = colors[(int)((colorBits >> (2 * pixel)) & 3)];
            var targetOffset = (y * width + x) * 4;
            target[targetOffset] = (byte)(c.B * a / 255);
            target[targetOffset + 1] = (byte)(c.G * a / 255);
            target[targetOffset + 2] = (byte)(c.R * a / 255);
            target[targetOffset + 3] = a;
        }
    }

    private static (byte R, byte G, byte B) Rgb565(ushort value) =>
        ((byte)(((value >> 11) & 31) * 255 / 31),
         (byte)(((value >> 5) & 63) * 255 / 63),
         (byte)((value & 31) * 255 / 31));
}
