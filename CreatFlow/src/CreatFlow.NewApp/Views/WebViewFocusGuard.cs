using Avalonia.Input;
using Avalonia.VisualTree;

namespace CreatFlow.NewApp.Views;

/// <summary>
/// WebView2 焦点防线（2026-11-04 崩溃复盘）。
///
/// 崩溃链：窗口激活 → <c>WindowBase.HandleActivated → FocusManager.SetFocusScope</c>
/// 恢复上次聚焦的元素 → <c>NativeWebView.OnGotFocus → ICoreWebView2Controller.MoveFocus</c>；
/// 控制器一旦失效（标签已关闭 / 控件已被隐藏 / WebView2 进程被回收），平台回
/// E_INVALIDARG（0x80070057），异常从 WndProc 冒到 CLR 边界把进程直接终结（0xc000041d），
/// Serilog 一行都来不及写。两个实机 dump 堆栈完全一致；同类问题见 WebView2Feedback #1552。
///
/// 防线一（这里）：**隐藏 / 关闭 / 摘树之前先把焦点收回来**，窗口激活就再也不会去碰
/// 那条已经不可见的 WebView。防线二在 <see cref="SafeNativeWebView"/>（即使漏了也不崩）。
/// </summary>
public static class WebViewFocusGuard
{
    /// <summary>
    /// 若当前焦点落在 <paramref name="root"/> 子树内，就清掉焦点（幂等、绝不抛）。
    /// 页面保活叠层（ShellWindow.SyncPageOverlay）隐藏页面、账号标签切走/关闭时都要先调一次。
    /// </summary>
    public static void ClearFocusInside(Visual root)
    {
        try
        {
            var top = TopLevel.GetTopLevel(root);
            if (top?.FocusManager is not { } fm) return;
            if (fm.GetFocusedElement() is not Visual focused) return;

            for (var cur = focused; cur != null; cur = cur.GetVisualParent())
                if (ReferenceEquals(cur, root))
                {
#pragma warning disable CS0618 // ClearFocus 在 11.x 标了 Obsolete/Unstable：没有等价 API，这里是防崩必需路径
                    fm.ClearFocus();
#pragma warning restore CS0618
                    return;
                }
        }
        catch
        {
            // 清焦点失败不影响隐藏/关闭本身；最坏情况由 SafeNativeWebView 兜底
        }
    }
}
