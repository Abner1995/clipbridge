using Avalonia.Input;

namespace CreatFlow.NewApp.Views;

/// <summary>
/// NativeWebView 的防崩子类 —— 只包一层 <see cref="NativeWebView.OnGotFocus"/> 的异常兜底。
///
/// 背景（2026-11-04 用户实机两次崩溃，Windows 事件日志 1026 + 两个 dump 堆栈完全一致）：
/// Avalonia.Controls.WebView 11.4.0 的 <c>NativeWebView.OnGotFocus</c> 会调用
/// <c>ICoreWebView2Controller.MoveFocus(...)</c>；当控制器已失效（标签已关闭 / 已被隐藏 /
/// WebView2 进程被回收）时平台返回 <b>E_INVALIDARG</b>（0x80070057，即
/// “Value does not fall within the expected range”），包内不捕获 —— 异常从
/// <c>WindowImpl.WndProc → WindowBase.HandleActivated → FocusManager 恢复焦点</c>
/// 一路冒到 CLR/原生边界，进程被直接终结（0xc000041d，Serilog 一行都来不及写）。
///
/// 同类问题 WebView2 官方仓库有记录（WebView2Feedback #1552：MoveFocus 抛
/// “Value does not fall within the expected range”，官方无修复，建议调用方自己 try/catch）。
/// 焦点没进页面是**可接受的降级**，绝不该带走整个进程，所以这里吞掉并每个实例记一次日志。
/// </summary>
public sealed class SafeNativeWebView : NativeWebView
{
    private int _focusFailureLogged;

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        try
        {
            base.OnGotFocus(e);
        }
        catch (ArgumentException ex)
        {
            // E_INVALIDARG：控制器已失效时的焦点恢复。窗口激活会反复触发同一条
            // 失效 WebView 的焦点恢复，每个实例只记一次防刷屏。
            if (Interlocked.Exchange(ref _focusFailureLogged, 1) == 0)
                Log.Warning(ex, "[WebView] 焦点切换失败（WebView2 控制器已失效，已忽略）");
        }
    }
}
