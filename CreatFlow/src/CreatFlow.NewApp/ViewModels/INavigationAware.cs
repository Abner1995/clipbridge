namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 页面每次被导航进入时收到通知（Shell 缓存页面实例跨切换保留状态）。
/// 实现方按自身语义刷新：设备/公告每次进入重拉（保持新鲜），钉钉只首进拉一次（保留未保存输入）。
/// </summary>
public interface INavigationAware
{
    void OnNavigatedTo();
}
