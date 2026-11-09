using Avalonia.Data.Converters;
using System.Globalization;

namespace CreatFlow.NewApp.Converters;

/// <summary>
/// 日期 / 时间字段里显示的那串文字：<see cref="DateTimeOffset"/>? / <see cref="DateTime"/>? /
/// <see cref="TimeSpan"/>? → 单串文本（null → 空串，由字段里的水印接上「选择日期」）。
///
/// 为什么不直接用控件自带的 YearFormat / MonthFormat / DayFormat：那三个格式串除了字段，
/// **还会喂给弹出层的年 / 月 / 日三列**（DatePicker.OnApplyTemplate 里
/// `_presenter[!YearFormatProperty] = this[!YearFormatProperty]`，实测弹出层里变成
/// 「2023-」「07-」这种带尾巴的项）。所以显示串自己格式化，格式串一个都不动 ——
/// 弹出层里该是「十月」还是「十月」。
/// </summary>
public sealed class PickerFieldTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            DateTimeOffset d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeSpan t => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
