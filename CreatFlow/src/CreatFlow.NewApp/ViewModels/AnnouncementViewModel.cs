using Material.Icons;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 公告页 ViewModel（业务逻辑自老 App 拷贝）。
/// GET /api/announcements 拉全量（服务端鉴权），时间倒序；
/// 点击卡片由 View 开独立详情窗（模态，Owner 当前壳窗）。
/// 公告按 type 中文化着色（通知/警告/紧急）—— 纯样式化，不传 emoji。
/// 每次进入页面刷新（INavigationAware）：公告是运营入口，保持新鲜。
/// </summary>
public partial class AnnouncementViewModel : ViewModelBase, INavigationAware
{
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _error = "";

    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>页头图标（矢量）</summary>
    public MaterialIconKind HeaderIcon => MaterialIconKind.Bullhorn;

    public bool IsEmpty => Items.Count == 0;
    public bool HasError => !string.IsNullOrEmpty(Error);

    public ObservableCollection<AnnouncementRow> Items { get; } = [];

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    public void OnNavigatedTo()
    {
        if (!IsLoading) LoadCommand.Execute(null);
    }

    [RelayCommand]
    private async Task Load()
    {
        IsLoading = true;
        Error = "";
        try
        {
            var resp = await ApiClient.GetAsync<AnnouncementListResponse>(
                "/api/announcements", authenticated: true);

            Items.Clear();
            if (resp?.Items != null)
            {
                foreach (var row in resp.Items
                             .OrderByDescending(a => a.CreatedAt)
                             .Select(a => AnnouncementRow.From(a)))
                    Items.Add(row);
            }
            OnPropertyChanged(nameof(IsEmpty));

            StatusMessage = Items.Count > 0
                ? $"共 {Items.Count} 条公告"
                : "暂无公告，有新通知会展示在这里";
        }
        catch (ApiException ex)
        {
            Error = ex.StatusCode switch
            {
                401 => "登录已过期，请重新登录",
                _ => $"加载失败：{ex.Message}"
            };
        }
        catch (Exception ex)
        {
            Error = $"加载异常：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}

/// <summary>公告行展示模型：type → 中文 + 品牌色系（badge 底色/文字色 + 左侧色条）</summary>
public sealed class AnnouncementRow
{
    public string Title { get; init; } = "";
    public string Content { get; init; } = "";
    public string TimeText { get; init; } = "";
    public string TypeText { get; init; } = "通知";
    public string BadgeBg { get; init; } = "#E6F4FF";
    public string BadgeFg { get; init; } = "#1677FF";
    public string BarColor { get; init; } = "#1677FF";

    public static AnnouncementRow From(AnnouncementItem item)
    {
        var (typeText, badgeBg, badgeFg, bar) = item.Type switch
        {
            "warning" => ("警告", "#FFF7E6", "#D46B08", "#FA8C16"),
            "urgent" => ("紧急", "#FFF1F0", "#CF1322", "#F5222D"),
            _ => ("通知", "#E6F4FF", "#1677FF", "#1677FF"),
        };
        return new AnnouncementRow
        {
            Title = item.Title,
            Content = item.Content ?? "",
            TimeText = item.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            TypeText = typeText,
            BadgeBg = badgeBg,
            BadgeFg = badgeFg,
            BarColor = bar
        };
    }
}
