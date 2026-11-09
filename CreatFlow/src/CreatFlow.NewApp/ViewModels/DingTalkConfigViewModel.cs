using Material.Icons;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 钉钉通知配置页 ViewModel（业务逻辑自老 App 拷贝，UI 防呆增强）。
/// 发布任务完成后经群机器人推消息到钉钉群。
/// GET(/api/dingtalk/config) → 保存/更新(/config/save) → 测试(/config/test) → 删除(/config/delete)。
/// Secret 只写不读：已配置时显示掩码「••••••••」，仅当用户重新输入完整值才允许保存覆盖
/// （老版直接回传掩码会把 Secret 覆盖成无效值，此处修正为防呆提示）。
/// </summary>
public partial class DingTalkConfigViewModel : ViewModelBase, INavigationAware
{
    /// <summary>Secret 不回显的掩码占位（与旧版显示一致）</summary>
    private const string SecretMask = "••••••••";

    [ObservableProperty]
    private string _webhookKey = "";

    [ObservableProperty]
    private string _secret = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isConfigured;

    /// <summary>是否存在状态文案（有无都需要：无 → 两个状态框都隐藏）</summary>
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    private Guid? _configId;
    private bool _loaded;
    private bool _secretEdited;

    /// <summary>页头图标（矢量）</summary>
    public MaterialIconKind HeaderIcon => MaterialIconKind.Bell;

    /// <summary>教程标题（appsettings DingTalkGuide:Title）</summary>
    public string GuideTitle => AppConfig.DingTalkGuideTitle;

    /// <summary>教程步骤（编号圆点渲染用）</summary>
    public IReadOnlyList<GuideStepCard> GuideCards { get; }

    /// <summary>Secret 占位提示（已配置 = 掩码态，须完整重输才能覆盖）</summary>
    public bool HasSecretMask => IsConfigured && Secret == SecretMask;

    partial void OnWebhookKeyChanged(string value) => SaveConfigCommand.NotifyCanExecuteChanged();
    partial void OnSecretChanged(string value)
    {
        _secretEdited = value != SecretMask;
        OnPropertyChanged(nameof(HasSecretMask));
        SaveConfigCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsConfiguredChanged(bool value)
    {
        OnPropertyChanged(nameof(HasSecretMask));
        SaveConfigCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsLoadingChanged(bool value) => SaveConfigCommand.NotifyCanExecuteChanged();

    public DingTalkConfigViewModel()
    {
        GuideCards = AppConfig.DingTalkGuideSteps
            .Where(s => s.Step > 0)
            .OrderBy(s => s.Step)
            .Select(s => new GuideStepCard(s.Step, s.Title, s.Detail))
            .ToList();
    }

    /// <summary>进入页面只首拉一次：保留用户未保存的输入不被切页清掉</summary>
    public void OnNavigatedTo()
    {
        if (!_loaded)
        {
            _loaded = true;
            LoadAsync();
        }
    }

    /// <summary>从服务端读取已有配置（Secret 不回显）</summary>
    private async Task LoadAsync()
    {
        IsLoading = true;
        StatusMessage = "";
        try
        {
            var config = await ApiClient.PostAsync<DingTalkConfigResponse?>("/api/dingtalk/config");
            if (config != null)
            {
                _configId = config.Id;
                WebhookKey = config.WebhookKey ?? "";
                Secret = SecretMask;
                _secretEdited = false;
                IsConfigured = true;
            }
            else
            {
                _configId = null;
                IsConfigured = false;
            }
        }
        catch (Exception ex)
        {
            SetError($"加载配置失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanSaveConfig() =>
        !IsLoading
        && !string.IsNullOrWhiteSpace(WebhookKey)
        && (!IsConfigured || _secretEdited || string.IsNullOrWhiteSpace(Secret));

    [RelayCommand(CanExecute = nameof(CanSaveConfig))]
    private async Task SaveConfig()
    {
        if (string.IsNullOrWhiteSpace(WebhookKey))
        {
            SetError("请输入 Webhook Key");
            return;
        }
        if (string.IsNullOrWhiteSpace(Secret))
        {
            SetError("请输入签名 Secret");
            return;
        }
        if (IsConfigured && Secret == SecretMask)
        {
            SetError("Secret 已隐藏不会回传。如需覆盖请完整输入新的 Secret；只想测通无需保存");
            return;
        }

        IsLoading = true;
        StatusMessage = "";
        try
        {
            var result = await ApiClient.PostAsync<SaveConfigResponse>(
                "/api/dingtalk/config/save",
                new { WebhookKey = WebhookKey.Trim(), Secret = Secret.Trim() });

            if (result?.Success == true)
            {
                _configId = result.Config?.Id;
                IsConfigured = true;
                Secret = SecretMask;
                _secretEdited = false;
                SetSuccess("配置已保存");
            }
            else
            {
                SetError(result?.Error ?? "保存失败");
            }
        }
        catch (Exception ex)
        {
            SetError($"保存失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DeleteConfig()
    {
        if (_configId == null)
        {
            SetError("尚未配置，无需删除");
            return;
        }

        IsLoading = true;
        StatusMessage = "";
        try
        {
            var result = await ApiClient.PostAsync<SimpleResult?>(
                "/api/dingtalk/config/delete",
                new { Id = _configId.Value });

            if (result?.Success == true)
            {
                _configId = null;
                WebhookKey = "";
                Secret = "";
                IsConfigured = false;
                SetSuccess("配置已删除");
            }
            else
            {
                SetError(result?.Error ?? "删除失败");
            }
        }
        catch (Exception ex)
        {
            SetError($"删除失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task TestSend()
    {
        if (!IsConfigured)
        {
            SetError("请先保存配置");
            return;
        }

        IsLoading = true;
        StatusMessage = "正在发送测试消息…";
        try
        {
            var result = await ApiClient.PostAsync<TestResult?>("/api/dingtalk/config/test");
            if (result?.Success == true)
                SetSuccess("测试消息已发送，请查看钉钉群");
            else
                SetError(result?.Error ?? "发送失败，请检查配置是否正确");
        }
        catch (Exception ex)
        {
            SetError($"发送失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SetSuccess(string msg)
    {
        StatusMessage = msg;
        IsStatusError = false;
    }

    private void SetError(string msg)
    {
        StatusMessage = msg;
        IsStatusError = true;
    }
}

/// <summary>教程步骤展示模型（AppConfig 元组 → 编译绑定友好）</summary>
public sealed class GuideStepCard(int no, string title, string detail)
{
    public int No { get; } = no;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
}
