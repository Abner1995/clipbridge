using Avalonia.Controls;

namespace CreatFlow.NewApp.Views;

public partial class LoginWindow : Window
{
    /// <summary>设计期预览用（运行时一律走 <see cref="LoginWindow(LoginViewModel)"/> 由 DI 注入）</summary>
    public LoginWindow() : this(new ViewModels.LoginViewModel())
    {
    }

    public LoginWindow(ViewModels.LoginViewModel vm)
    {
        InitializeComponent();
        Vm = vm;
        DataContext = vm;
        LoadWindowIcon(this);
    }

    /// <summary>窗口标题栏图标（Assets/app-icon.ico，与老 App 同一图标）</summary>
    internal static void LoadWindowIcon(Window w)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.ico");
            if (File.Exists(path)) w.Icon = new WindowIcon(path);
        }
        catch { /* 图标加载失败不阻塞 */ }
    }

    public ViewModels.LoginViewModel Vm { get; }

    private bool _revealPassword;

    /// <summary>眼睛按钮：切换明文/密文 + 图标互换（Semi TextBox 无内置 Reveal，自绘）</summary>
    private void RevealButton_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        _revealPassword = !_revealPassword;
        if (PasswordInput != null)
        {
            // PasswordChar 置 '\0' = 明文显示
            PasswordInput.PasswordChar = _revealPassword ? '\0' : '●';
        }
        if (EyeIcon != null) EyeIcon.IsVisible = !_revealPassword;
        if (EyeOffIcon != null) EyeOffIcon.IsVisible = _revealPassword;
    }

    /// <summary>回车键触发登录（密码框聚焦时）</summary>
    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter
            && !Vm.IsLoggingIn && !Vm.IsAutoChecking
            && Vm.LoginCommand.CanExecute(null))
        {
            Vm.LoginCommand.Execute(null);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
