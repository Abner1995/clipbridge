namespace CreatFlow.NewApp.Services;

/// <summary>配音下拉里的一个音色（配音页 / 合成页 Voices 的项类型：由引擎自带的 Provider.Voices 转换而来）。
/// 带 ProviderName：配音页每条配音独立选音色，试听 / 提交要按所选项定位引擎（不再由页面统一指定引擎）。
/// public：XAML 的 DataTemplate 编译绑定要引用它。
///
/// 本文件原名 TtsVoiceCatalog —— 它维护的 Edge 静态音色清单纯随 Edge 免费引擎下架（2026-10-10
/// 产品决策）一并删除，只剩下列表项类型本身；将来若引入无自带音色表的引擎，再补对应清单。</summary>
public sealed record TtsVoiceOption(string ProviderName, string ShortName, string DisplayText);
