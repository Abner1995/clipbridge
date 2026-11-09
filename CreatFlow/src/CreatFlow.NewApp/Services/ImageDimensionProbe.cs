namespace CreatFlow.NewApp.Services;

/// <summary>
/// 图片文件头尺寸探测 —— 只读文件头、不解码，供封面相关字段使用：
/// 抖音 <c>cover.custom_cover_image_width/height</c> 是「裁剪结果尺寸」，
/// 用户没在别处裁剪时页面取的就是图片实际像素（2026-11-05 抓包 1051×788 =
/// 页面状态里的 <c>customCoverImageWidth/Height</c>，取自裁剪框）。
///
/// 支持 PNG / JPEG / GIF / BMP / WebP（VP8X、有损 VP8、无损 VP8L）。
/// 不认识的格式、损坏或截断的文件一律返回 null —— **永不抛异常**：
/// 封面尺寸只是锦上添花，读不到就不下发，不能让发布挂掉。
/// </summary>
public static class ImageDimensionProbe
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>从图片头读像素尺寸；读不出 → null。</summary>
    public static (int Width, int Height)? TryRead(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length >= 24 && data[..8].SequenceEqual(PngSignature))
                return ReadPng(data);
            if (data.Length >= 10 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8')
                return ReadGif(data);
            if (data.Length >= 26 && data[0] == 'B' && data[1] == 'M')
                return ReadBmp(data);
            if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
                return ReadJpeg(data);
            if (data.Length >= 25 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
                && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
                return ReadWebp(data);
        }
        catch
        {
            // 截断/越界 —— 当读不出，交给调用方回退
        }
        return null;
    }

    /// <summary>
    /// 从文件头读像素尺寸（最多读 256KB —— JPEG 的 SOF 段可能排在较大的 EXIF 之后）；
    /// 文件不存在 / 不是图片 / 读失败 → null。
    /// </summary>
    public static (int Width, int Height)? TryReadFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var len = (int)Math.Min(fs.Length, 256 * 1024);
            if (len <= 0) return null;
            var buf = new byte[len];
            var read = 0;
            while (read < len)
            {
                var n = fs.Read(buf, read, len - read);
                if (n <= 0) break;
                read += n;
            }
            return TryRead(buf.AsSpan(0, read));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 抖音 <c>cover.custom_cover_image_*</c> 的取值：用户填了就用用户的，否则用封面图实际像素；
    /// 都没有 → null（不下发）。
    /// </summary>
    public static (int? Width, int? Height) ResolveCrop(
        int? userWidth, int? userHeight, (int Width, int Height)? coverSize)
    {
        var w = userWidth is > 0 ? userWidth : coverSize?.Width;
        var h = userHeight is > 0 ? userHeight : coverSize?.Height;
        return (w is > 0 ? w : null, h is > 0 ? h : null);
    }

    // ── PNG：签名(8) + IHDR 长度(4) + "IHDR"(4) + 宽(4) + 高(4)，大端 ──
    private static (int Width, int Height)? ReadPng(ReadOnlySpan<byte> d)
    {
        if (d[12] != 'I' || d[13] != 'H' || d[14] != 'D' || d[15] != 'R') return null;
        var w = (d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19];
        var h = (d[20] << 24) | (d[21] << 16) | (d[22] << 8) | d[23];
        return w > 0 && h > 0 ? (w, h) : null;
    }

    // ── GIF："GIF87a"/"GIF89a" + 逻辑屏幕宽(2, 小端) + 高(2, 小端) ──
    private static (int Width, int Height)? ReadGif(ReadOnlySpan<byte> d)
    {
        var w = d[6] | (d[7] << 8);
        var h = d[8] | (d[9] << 8);
        return w > 0 && h > 0 ? (w, h) : null;
    }

    // ── BMP：文件头 14 字节后是 DIB 头（大小在 14..18）；40+ 时宽高为 int32（高可负） ──
    private static (int Width, int Height)? ReadBmp(ReadOnlySpan<byte> d)
    {
        var headerSize = d[14] | (d[15] << 8) | (d[16] << 16) | (d[17] << 24);
        int w, h;
        if (headerSize == 12 && d.Length >= 22)
        {
            // BITMAPCOREHEADER：宽高是 16 位
            w = d[18] | (d[19] << 8);
            h = d[20] | (d[21] << 8);
        }
        else if (headerSize >= 40 && d.Length >= 26)
        {
            w = d[18] | (d[19] << 8) | (d[20] << 16) | (d[21] << 24);
            h = d[22] | (d[23] << 8) | (d[24] << 16) | (d[25] << 24);
            if (h < 0) h = -h;   // 负高 = 自上而下存储
        }
        else
        {
            return null;
        }
        return w > 0 && h > 0 ? (w, h) : null;
    }

    // ── JPEG：扫到 SOF 段取高/宽（段内：精度(1) 高(2) 宽(2)，大端） ──
    private static (int Width, int Height)? ReadJpeg(ReadOnlySpan<byte> d)
    {
        var i = 2;
        while (i + 9 <= d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            var marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }                 // 填充字节
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9))
            {
                i += 2;                                            // 无长度字段的标记
                continue;
            }
            if (marker == 0xDA) break;                             // 已到 SOS 还没见 SOF —— 放弃
            var segLen = (d[i + 2] << 8) | d[i + 3];
            if (segLen < 2) break;

            // SOF0/1/2/3/5/6/7/9/10/11/13/14/15（C4/C8/CC 是 DHT/JPG/DAC，不是 SOF）
            var isSof = marker >= 0xC0 && marker <= 0xCF
                && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isSof)
            {
                var h = (d[i + 5] << 8) | d[i + 6];
                var w = (d[i + 7] << 8) | d[i + 8];
                return w > 0 && h > 0 ? (w, h) : null;
            }
            i += 2 + segLen;
        }
        return null;
    }

    // ── WebP：RIFF....WEBP + 首个 chunk 的 fourcc 决定布局 ──
    private static (int Width, int Height)? ReadWebp(ReadOnlySpan<byte> d)
    {
        var fourcc = d[12..16];
        if (fourcc.SequenceEqual("VP8X"u8))
        {
            if (d.Length < 30) return null;
            // 扩展格式：canvas 宽高减一，24 位小端（flags(1) + 保留(3) 之后的 24..30）
            var w = (d[24] | (d[25] << 8) | (d[26] << 16)) + 1;
            var h = (d[27] | (d[28] << 8) | (d[29] << 16)) + 1;
            return (w, h);
        }
        if (fourcc.SequenceEqual("VP8 "u8))
        {
            if (d.Length < 30) return null;
            // 有损：帧标签(3) + 起始码 9D 01 2A + 宽(2, 14 位) + 高(2, 14 位)
            if (d[23] != 0x9D || d[24] != 0x01 || d[25] != 0x2A) return null;
            var w = (d[26] | (d[27] << 8)) & 0x3FFF;
            var h = (d[28] | (d[29] << 8)) & 0x3FFF;
            return w > 0 && h > 0 ? (w, h) : null;
        }
        if (fourcc.SequenceEqual("VP8L"u8))
        {
            // 无损：签名 0x2F + 14 位宽减一 + 14 位高减一
            if (d[20] != 0x2F) return null;
            var bits = (uint)(d[21] | (d[22] << 8) | (d[23] << 16) | (d[24] << 24));
            var w = (int)(bits & 0x3FFF) + 1;
            var h = (int)((bits >> 14) & 0x3FFF) + 1;
            return (w, h);
        }
        return null;
    }
}
