namespace CreatFlow.NewApp;

/// <summary>
/// ViewModel → View 命名约定路由（自老 App 拷贝）：VM 类型名去 "ViewModel" 后缀换成 "View"。
/// 挂在 Application.DataTemplates，ContentControl 绑定 VM 时自动解析对应页面。
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
