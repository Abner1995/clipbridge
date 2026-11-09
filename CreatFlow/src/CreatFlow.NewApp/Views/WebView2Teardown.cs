using Avalonia.Controls;

namespace CreatFlow.NewApp.Views;

/// <summary>
/// WebView2 释放助手 —— controller.Close() 走官方通道(UI 线程)的 best-effort 封装。
///
/// 获取路径(包元数据实证):NativeWebView.TryGetPlatformHandle() →
/// IWindowsWebView2PlatformHandle.CoreWebView2Controller → Close()。
/// 反射调用以隔离包版本差异(代码库既有 ReadHeader 等反射先例);
/// 失败静默 —— 标签摘树后环境随宿主回收,渲染进程退出由 AccountProfileCleanup
/// 锁探测 + marker 兜底,Close 永不进解锁等待链。
/// </summary>
public static class WebView2Teardown
{
    public static void TryCloseController(NativeWebView webView)
    {
        if (webView == null) return;
        try
        {
            // 平台句柄:C# 侧 TryGetPlatformHandle(Windows 实现返回 IWindowsWebView2PlatformHandle)
            var handleMethod = webView.GetType().GetMethod("TryGetPlatformHandle");
            var handle = handleMethod?.Invoke(webView, null);
            if (handle == null) return;

            // 句柄接口属性 CoreWebView2Controller(可能以 get_ 前缀暴露)
            var controller = GetProperty(handle, "CoreWebView2Controller")
                ?? GetProperty(handle, "get_CoreWebView2Controller");
            if (controller == null) return;

            // COM/托管 controller 统一走 Close()
            var closeMethod = controller.GetType().GetMethod("Close");
            closeMethod?.Invoke(controller, null);
        }
        catch
        {
            // best effort:摘树后环境随宿主销毁自行回收,失败由锁探测+marker 兜底
        }
    }

    private static object? GetProperty(object target, string name)
    {
        var prop = target.GetType().GetProperty(name);
        if (prop != null) return prop.GetValue(target);
        // 兼容带 get_ 前缀的显式接口成员
        var method = target.GetType().GetMethod("get_" + name);
        return method?.Invoke(target, null);
    }
}
