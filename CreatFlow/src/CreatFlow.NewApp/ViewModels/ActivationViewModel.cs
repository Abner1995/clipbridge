using CreatFlow.NewApp.Services;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 激活页 ViewModel（已登录 + 未激活/过期用户的核心页面）。
/// 输入激活码 → POST /api/auth/activate（JWT + ClientSecret 签名）→ 成功后更新本地会话
/// 并 <see cref="SessionContext.Refresh"/> —— 壳监听 MembershipChanged 自动解锁导航并跳回首页。
/// 逻辑自老 App 拷贝；无 emoji（🎉/🔑 移除）；主按钮走 CtaButtonTheme。
/// </summary>
public partial class ActivationViewModel : ViewModelBase
{
    private readonly SessionContext _session;

    public ActivationViewModel(SessionContext session)
    {
        _session = session;
    }

    [ObservableProperty]
    private string _activationCode = "";

    [ObservableProperty]
    private bool _isCodeValid;

    [ObservableProperty]
    private bool _isCodeValidating;

    [ObservableProperty]
    private string _codeMessage = "";

    [ObservableProperty]
    private bool _isCodeError;

    /// <summary>激活成功后显示的信息</summary>
    [ObservableProperty]
    private string _successDuration = "";

    [ObservableProperty]
    private string _successNewExpire = "";

    [ObservableProperty]
    private bool _hasSuccess;

    [RelayCommand]
    private void Reset()
    {
        IsCodeValid = false;
        HasSuccess = false;
        ActivationCode = "";
        CodeMessage = "";
        SuccessDuration = "";
        SuccessNewExpire = "";
    }

    // ═══ 计算属性 ═══

    public bool IsValidateButtonVisible => !IsCodeValid;
    public bool IsCodeInputEnabled => !IsCodeValidating && !IsCodeValid;

    // ═══ 依赖通知 ═══

    partial void OnIsCodeValidatingChanged(bool value) =>
        OnPropertyChanged(nameof(IsCodeInputEnabled));

    partial void OnIsCodeValidChanged(bool value) =>
        OnPropertyChanged(nameof(IsValidateButtonVisible));

    partial void OnActivationCodeChanged(string value) =>
        ValidateCodeCommand.NotifyCanExecuteChanged();

    // ═══ 命令: 验证激活码 ═══

    private bool CanValidateCode() =>
        !string.IsNullOrWhiteSpace(ActivationCode)
        && !IsCodeValidating
        && !IsCodeValid;

    [RelayCommand(CanExecute = nameof(CanValidateCode))]
    private async Task ValidateCode()
    {
        IsCodeValidating = true;
        CodeMessage = "正在验证…";
        IsCodeError = false;

        try
        {
            var response = await ApiClient.PostAsync<ActivateResponse>(
                "/api/auth/activate",
                new ActivateRequest(ActivationCode, MachineId: null),
                authenticated: true); // 需要 JWT

            if (response == null)
            {
                CodeMessage = "服务器无响应，请稍后重试";
                IsCodeError = true;
                return;
            }

            if (!response.Success)
            {
                CodeMessage = response.Error ?? "激活失败";
                IsCodeError = true;
                return;
            }

            // 激活成功 — 更新本地存储的会员信息（token/secret 不变，只换到期时间）
            if (response.Member != null)
            {
                var session = TokenStorage.Load();
                if (session != null)
                {
                    TokenStorage.Save(
                        token: session.Token,
                        username: response.Member.Username,
                        expireDate: response.Member.ExpireDate,
                        clientSecret: response.ClientSecret ?? session.ClientSecret,
                        memberId: response.Member.Id,
                        lastLoginIp: response.Member.LastLoginIp ?? session.LastLoginIp,
                        lastLoginAt: response.Member.LastLoginAt ?? session.LastLoginAt);
                }
            }

            IsCodeValid = true;
            HasSuccess = true;
            SuccessDuration = $"+{response.DurationDays} 天";
            SuccessNewExpire = response.Member?.ExpireDate.ToString("yyyy-MM-dd") ?? "";
            CodeMessage = "";
            IsCodeError = false;

            // 广播会员变更 → 壳解锁全部导航 + 过期用户自动跳回首页
            _session.Refresh();
        }
        catch (ApiException ex)
        {
            CodeMessage = ex.StatusCode switch
            {
                400 => ex.Message,
                401 => "登录已过期，请重新登录后再试",
                _ => ex.Message
            };
            IsCodeError = true;
        }
        catch (HttpRequestException)
        {
            CodeMessage = "无法连接服务器，请检查网络";
            IsCodeError = true;
        }
        catch (TaskCanceledException)
        {
            CodeMessage = "请求超时，请检查网络后重试";
            IsCodeError = true;
        }
        catch (Exception)
        {
            CodeMessage = "网络错误，请检查连接后重试";
            IsCodeError = true;
        }
        finally
        {
            IsCodeValidating = false;
        }
    }
}
