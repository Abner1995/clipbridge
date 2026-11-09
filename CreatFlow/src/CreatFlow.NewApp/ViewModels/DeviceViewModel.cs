using Material.Icons;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 设备管理页 ViewModel（业务逻辑自老 App 拷贝，无 emoji/页头走模板）。
/// POST /api/devices/list 拉当前会员设备 → 本地标出「当前设备」→ 支持踢出（非当前、活跃设备）。
/// 进入页面即刷新（INavigationAware）：别处踢出后回来状态新鲜。
/// </summary>
public partial class DeviceViewModel : ViewModelBase, INavigationAware
{
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string? _success;

    [ObservableProperty]
    private List<DeviceItem> _devices = [];

    /// <summary>页头图标（矢量，与导航一致）</summary>
    public MaterialIconKind HeaderIcon => MaterialIconKind.Devices;

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasSuccess => !string.IsNullOrEmpty(Success);
    public bool IsEmpty => !IsLoading && Devices.Count == 0;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnSuccessChanged(string? value) => OnPropertyChanged(nameof(HasSuccess));
    partial void OnDevicesChanged(List<DeviceItem> value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    public void OnNavigatedTo()
    {
        if (!IsLoading) LoadDevicesCommand.Execute(null);
    }

    [RelayCommand]
    private async Task LoadDevices()
    {
        IsLoading = true;
        Error = null;
        Success = null;

        try
        {
            var response = await ApiClient.PostAsync<List<DeviceItem>>(
                "/api/devices/list", body: null, authenticated: true);
            if (response != null)
            {
                var currentMachineId = GetCurrentMachineId();
                foreach (var d in response)
                    d.IsCurrentDevice = d.MachineId == currentMachineId;
                Devices = response;
            }
        }
        catch (ApiException ex)
        {
            Error = ex.StatusCode switch
            {
                401 => "登录已过期，请重新登录",
                _ => ex.Message
            };
        }
        catch (Exception)
        {
            Error = "网络错误，请检查连接后重试";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task KickDevice(DeviceItem? device)
    {
        if (device is null) return;
        if (device.IsCurrentDevice)
        {
            Error = "不能踢出当前设备";
            return;
        }
        if (!device.IsActive)
        {
            Error = "该设备已被踢出";
            return;
        }

        Error = null;
        Success = null;
        try
        {
            await ApiClient.PostAsync<object>("/api/devices/kick",
                new { device_id = device.Id }, authenticated: true);
            Success = $"已踢出设备「{device.DeviceName}」";
            await LoadDevices();
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
        }
        catch (Exception)
        {
            Error = "网络错误，请检查连接后重试";
        }
    }

    private static string GetCurrentMachineId()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Cryptography");
                return key?.GetValue("MachineGuid")?.ToString() ?? "";
            }
        }
        catch { }
        return "";
    }
}

/// <summary>设备信息（与 API 返回 DeviceDto 对应，自老 App 拷贝）</summary>
public class DeviceItem
{
    public Guid Id { get; set; }
    public Guid MemberId { get; set; }
    public string MemberUsername { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string DeviceType { get; set; } = "";
    public string? OsVersion { get; set; }
    public string? AppVersion { get; set; }
    public string? LastIp { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime FirstLoginAt { get; set; }
    public bool IsActive { get; set; }
    public DateTime? KickedAt { get; set; }
    public string? KickedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>是否当前运行设备（客户端本地比对 MachineGuid）</summary>
    public bool IsCurrentDevice { get; set; }

    /// <summary>是否已被踢出</summary>
    public bool IsKicked => !IsActive;

    /// <summary>是否可踢（活跃且非当前）</summary>
    public bool CanKick => IsActive && !IsCurrentDevice;

    /// <summary>系统行：类型 · OS · IP（非空段拼接）</summary>
    public string InfoText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(DeviceType)) parts.Add(DeviceType);
            if (!string.IsNullOrWhiteSpace(OsVersion)) parts.Add(OsVersion);
            if (!string.IsNullOrWhiteSpace(LastIp)) parts.Add(LastIp);
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>最近登录文本</summary>
    public string LastLoginText => LastLoginAt?.ToString("yyyy-MM-dd HH:mm") ?? "未知";
}
