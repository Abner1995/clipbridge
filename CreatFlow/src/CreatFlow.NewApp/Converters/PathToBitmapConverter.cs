using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace CreatFlow.NewApp.Converters;

/// <summary>
/// 本地图片路径 → Bitmap（缩略图 / 封面预览用）。
///
/// 放在转换器而不是 VM 属性里：Bitmap 是 Avalonia 视图类型，塞进 <see cref="Models.PublishDraft"/>
/// 会让模型层依赖渲染栈；而且草稿是持久化对象，缓存住位图对序列化毫无意义。
///
/// 用 <c>File.OpenRead</c> 读进 <see cref="MemoryStream"/> 再解码 —— Bitmap 不等流，
/// 直接传 FileStream 会在位图真正解码前就被释放（实测表现为偶发空白图）。
/// </summary>
public sealed class PathToBitmapConverter : IValueConverter
{
    public static readonly PathToBitmapConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch (Exception ex)
        {
            // 解码失败（非图片 / 文件被占用）不该让整页崩 —— 静默退回占位
            Log.Debug(ex, "[Publish] 图片解码失败 {Path}", path);
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
