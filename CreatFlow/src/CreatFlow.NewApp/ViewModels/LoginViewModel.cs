namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// QQ 风格登录窗 ViewModel。
/// 流程：本地无有效会话 → 表单；有会话 → <see cref="BeginAutoCheck"/> 自动心跳验证，
/// 成功后由 App 层开主窗；401/禁用/网络异常 → <see cref="ShowForm"/> 回落表单。
/// 「记住我」勾选 → token 落盘（重启免登）；不勾 → 仅内存会话（关窗失效）。
/// </summary>
public partial class LoginViewModel : ViewModelBase
{
    public LoginViewModel()
    {
    }

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private bool _isLoggingIn;

    /// <summary>true = 启动自动验证中（显示转圈，隐藏表单）</summary>
    [ObservableProperty]
    private bool _isAutoChecking;

    /// <summary>记住我（默认勾选 → 登录态落盘，重启免登）</summary>
    [ObservableProperty]
    private bool _rememberLogin = true;

    /// <summary>登录成功后由 App 层设置（切换到主窗口）</summary>
    public Action? OnLoginSucceeded { get; set; }

    /// <summary>管理员联系邮箱（appsettings.json；多个邮箱已按行拆开）</summary>
    public string ContactEmail => AppConfig.ContactEmailDisplay;

    /// <summary>工作时间说明（appsettings.json）</summary>
    public string ContactWorkHours => AppConfig.ContactWorkHours;

    private bool CanLogin() =>
        !IsAutoChecking && !IsLoggingIn
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password);

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task Login()
    {
        IsLoggingIn = true;
        Message = "";
        IsError = false;
        try
        {
            var deviceInfo = GetDeviceInfo();
            var response = await ApiClient.PostAsync<LoginResponse>(
                "/api/auth/login",
                new LoginRequest(Username, Password,
                    deviceInfo.MachineId, deviceInfo.DeviceName,
                    deviceInfo.DeviceType, deviceInfo.OsVersion, deviceInfo.AppVersion),
                authenticated: false); // 登录是公开端点，用 AppSecret 签名

            if (response == null)
            {
                ShowForm("服务器无响应，请稍后重试", isError: true);
                return;
            }
            if (!response.Success)
            {
                ShowForm(response.Error ?? "登录失败", isError: true);
                return;
            }
            if (response.Token == null || response.Member == null)
            {
                ShowForm("服务器返回异常，请稍后重试", isError: true);
                return;
            }

            var session = new StoredSession
            {
                Token = response.Token,
                Username = response.Member.Username,
                ExpireDate = response.Member.ExpireDate,
                ClientSecret = response.ClientSecret ?? "",
                MemberId = response.Member.Id,
                LastLoginIp = response.Member.LastLoginIp,
                LastLoginAt = response.Member.LastLoginAt
            };
            if (RememberLogin)
                TokenStorage.Save(session.Token, session.Username, session.ExpireDate,
                    session.ClientSecret, session.MemberId, session.LastLoginIp, session.LastLoginAt);
            else
                TokenStorage.SaveInMemory(session); // 不落盘：重启后需重新登录

            OnLoginSucceeded?.Invoke();
        }
        catch (ApiException ex)
        {
            ShowForm(ex.StatusCode switch
            {
                401 => "用户名或密码错误",
                _ => ex.Message
            }, isError: true);
        }
        catch (HttpRequestException)
        {
            ShowForm("无法连接服务器，请检查网络", isError: true);
        }
        catch (TaskCanceledException)
        {
            ShowForm("请求超时，请检查网络后重试", isError: true);
        }
        catch (Exception)
        {
            ShowForm("网络错误，请检查连接后重试", isError: true);
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    /// <summary>自动验证开始（启动时本地有会话）</summary>
    public void BeginAutoCheck()
    {
        IsAutoChecking = true;
        Message = "";
        IsError = false;
    }

    /// <summary>自动验证结束 → 回落表单（msg 为空则干净表单）</summary>
    public void EndAutoCheck(string? message = null, bool isError = true)
    {
        IsAutoChecking = false;
        if (!string.IsNullOrEmpty(message)) ShowForm(message, isError);
    }

    public void ShowForm(string message, bool isError)
    {
        Message = message;
        IsError = isError;
    }

    // ═══ 手动通知 CanExecute + 输入时清除错误 ═══

    partial void OnUsernameChanged(string value) { LoginCommand.NotifyCanExecuteChanged(); ClearErrorOnInput(); }
    partial void OnPasswordChanged(string value) { LoginCommand.NotifyCanExecuteChanged(); ClearErrorOnInput(); }
    partial void OnIsLoggingInChanged(bool value) => LoginCommand.NotifyCanExecuteChanged();
    partial void OnIsAutoCheckingChanged(bool value) => LoginCommand.NotifyCanExecuteChanged();

    private void ClearErrorOnInput()
    {
        if (IsError)
        {
            Message = "";
            IsError = false;
        }
    }

    private static (string? MachineId, string? DeviceName, string? DeviceType, string? OsVersion, string? AppVersion) GetDeviceInfo()
    {
        string? machineId = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Cryptography");
                machineId = key?.GetValue("MachineGuid")?.ToString();
            }
        }
        catch { }

        var deviceType = OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsMacOS() ? "macOS"
            : OperatingSystem.IsLinux() ? "Linux"
            : "Unknown";

        var osVersion = Environment.OSVersion.VersionString;
        var deviceName = Environment.MachineName;
        var appVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";

        return (machineId, deviceName, deviceType, osVersion, appVersion);
    }
}
