using Avalonia.Controls;
using Avalonia.Input;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Views;

public partial class AnnouncementView : UserControl
{
    public AnnouncementView()
    {
        InitializeComponent();
    }

    /// <summary>点击公告卡片 → 打开独立详情窗（模态，干净不重叠；老 App 同款交互）</summary>
    private void AnnouncementCard_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: AnnouncementRow row }) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var detail = new AnnouncementDetailWindow(row);
        if (owner != null)
            detail.ShowDialog(owner);
        else
            detail.Show();
    }
}
