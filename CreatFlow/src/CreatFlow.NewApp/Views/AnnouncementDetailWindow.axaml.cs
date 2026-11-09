using Avalonia.Controls;
using Avalonia.Media;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Views;

public partial class AnnouncementDetailWindow : Window
{
    /// <summary>公共无参构造 — Avalonia XAML 加载需要</summary>
    public AnnouncementDetailWindow()
    {
        InitializeComponent();
        LoginWindow.LoadWindowIcon(this);
    }

    public AnnouncementDetailWindow(AnnouncementRow row) : this()
    {
        Title = "公告详情 · CreatFlow";
        TypeText.Text = row.TypeText;
        TypeBadge.Background = new SolidColorBrush(Color.Parse(row.BadgeBg));
        TypeText.Foreground = new SolidColorBrush(Color.Parse(row.BadgeFg));
        TitleText.Text = row.Title;
        MetaText.Text = $"发布于 {row.TimeText}";
        ContentText.Text = row.Content;
    }
}
