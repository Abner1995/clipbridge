using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;
using Material.Icons;

namespace CreatFlow.NewApp.Converters;

/// <summary>
/// MaterialIconKind → 原生 Path 几何（Geometry）。
/// 不用 Material.Icons.Avalonia 的 MaterialIcon 控件 —— 它依赖 PathIcon 主题模板，
/// Semi 主题未覆盖时整批不渲染（2026-09-07 实测全不可见）。
/// 用 <see cref="MaterialIconDataProvider.GetData"/> 取路径串 → Geometry.Parse，
/// 原生 Path 自绘无模板依赖，任何主题下必现。
/// </summary>
public sealed class MaterialIconPathConverter : IValueConverter
{
    public static readonly MaterialIconPathConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MaterialIconKind kind) return null;
        try
        {
            var data = MaterialIconDataProvider.GetData(kind);
            return string.IsNullOrEmpty(data) ? null : Geometry.Parse(data);
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
