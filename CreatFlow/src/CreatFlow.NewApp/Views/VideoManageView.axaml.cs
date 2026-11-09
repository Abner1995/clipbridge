using Avalonia.Controls;

namespace CreatFlow.NewApp.Views;

/// <summary>
/// 视频管理页视图 —— 纯绑定，无代码后置逻辑（删除确认、筛选、分页全是 VM 的命令）。
/// 与发布页/队列页同款：页面不持有状态，状态在 VM 里，退出重进还在。
/// </summary>
public partial class VideoManageView : UserControl
{
    public VideoManageView() => InitializeComponent();
}
