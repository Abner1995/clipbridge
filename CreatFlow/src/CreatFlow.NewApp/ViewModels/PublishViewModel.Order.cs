namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 发布页 · 账号排序（视图四）—— <see cref="PublishViewModel"/> 的 partial。
///
/// 为什么是**两级**移动：顺序页的序列是扁平的（存储、应用、跨页面一致都最简单），
/// 但「一个平台好多号」时，把整个快手组挪到抖音前面若只能按行级 ↑ 一行行挪，
/// 十几次点击就为了一件事。所以：
///   · 整组 ↑↓ —— 该平台全部账号作为一个块，越过相邻的另一个平台组；
///   · 行级 ↑↓ —— 只在同平台块内换位，**到块边界停住**（跨组请用整组移动），
///     这样"分组永远不散"，用户排出来的列表不会因为手滑变成平台互相穿插。
///
/// 顺序的**唯一真源**是 <see cref="AccountOrderStore"/>（本地 JSON）；本页只是它的编辑器，
/// 每次移动立即落盘并同步到账号芯片 —— 不设"保存"按钮，因为顺序是所见即所得的东西，
/// 让用户记着保存反而容易丢。
/// </summary>
public sealed partial class PublishViewModel
{
    // DI 单例注入（主 partial 的 ctor 赋值）—— 会员级存档，与平台页/账号坞/芯片装配同一实例
    private readonly AccountOrderStore _orderStore;

    /// <summary>用户自定义顺序（key 列表）。空列表 = 从未排过 → 走默认序。</summary>
    private List<string> _userOrder = [];

    public ObservableCollection<AccountOrderRowViewModel> OrderRows { get; } = [];

    [RelayCommand]
    private void ShowOrderView()
    {
        BuildOrderRows();
        ViewMode = PublishViewMode.Order;
    }

    /// <summary>
    /// 从账号芯片重建排序行 —— <see cref="GlobalTargets"/> 已经是有序的（它由
    /// <see cref="AccountOrder.Apply"/> 排过），所以这里不需要再算一遍顺序。
    /// </summary>
    private void BuildOrderRows()
    {
        OrderRows.Clear();
        foreach (var chip in GlobalTargets)
        {
            OrderRows.Add(new AccountOrderRowViewModel
            {
                SessionKey = AccountOrder.KeyOf(chip.PlatformKey, chip.SessionId),
                SessionId = chip.SessionId,
                PlatformKey = chip.PlatformKey,
                PlatformName = chip.PlatformName,
                AccountLabel = chip.AccountLabel,
                BrandHex = chip.BrandHex,
                IsPrimary = chip.IsPrimary,
            });
        }
        RefreshOrderBoundaries();
    }

    /// <summary>
    /// 重算四个边界态。**统一在这里算**：每加一种移动方式都要重算，
    /// 散在 XAML 转换器里迟早会出现"某个按钮该灰没灰"。
    /// </summary>
    private void RefreshOrderBoundaries()
    {
        for (var i = 0; i < OrderRows.Count; i++)
        {
            var row = OrderRows[i];
            var sameAsPrev = i > 0
                && OrderRows[i - 1].PlatformKey == row.PlatformKey;
            var sameAsNext = i + 1 < OrderRows.Count
                && OrderRows[i + 1].PlatformKey == row.PlatformKey;

            row.IsGroupFirst = !sameAsPrev;   // 组首 → 它上方画组头；同时是行级 ↑ 的禁用条件
            row.IsLastInGroup = !sameAsNext;  // 组尾 → 行级 ↓ 禁用
            row.IsFirstGroup = i == 0;        // 整组 ↑ 只在第一个组禁用
            row.IsLastGroup = i == OrderRows.Count - 1;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  移动
    // ═══════════════════════════════════════════════════════════════

    /// <summary>组内上移 —— 越不过组边界（越界静默不动，好过挪到别的平台组里）</summary>
    [RelayCommand]
    private Task MoveAccountUpAsync(AccountOrderRowViewModel? row)
    {
        if (row is null) return Task.CompletedTask;
        var i = OrderRows.IndexOf(row);
        if (i <= 0 || OrderRows[i - 1].PlatformKey != row.PlatformKey) return Task.CompletedTask;

        OrderRows.Move(i, i - 1);
        return AfterOrderChangedAsync();
    }

    /// <summary>组内下移 —— 同上，到组边界停住</summary>
    [RelayCommand]
    private Task MoveAccountDownAsync(AccountOrderRowViewModel? row)
    {
        if (row is null) return Task.CompletedTask;
        var i = OrderRows.IndexOf(row);
        if (i < 0 || i + 1 >= OrderRows.Count || OrderRows[i + 1].PlatformKey != row.PlatformKey)
            return Task.CompletedTask;

        OrderRows.Move(i, i + 1);
        return AfterOrderChangedAsync();
    }

    /// <summary>整组上移 —— 该平台的全部行整体越过上一个平台组</summary>
    [RelayCommand]
    private Task MoveGroupUpAsync(AccountOrderRowViewModel? row)
    {
        if (row is null) return Task.CompletedTask;

        var start = GroupStartOf(row.PlatformKey);
        if (start <= 0) return Task.CompletedTask;

        var len = GroupLengthAt(start);

        // 上一个组的起点（越过的是**整组**，不是相邻一行）
        var prevStart = start - 1;
        while (prevStart > 0 && OrderRows[prevStart - 1].PlatformKey == OrderRows[start - 1].PlatformKey)
            prevStart--;

        // 整块上移用**正序** Move：每移一行，后面的成员正好落到下一个目标槽位
        for (var k = 0; k < len; k++) OrderRows.Move(start + k, prevStart + k);
        return AfterOrderChangedAsync();
    }

    /// <summary>整组下移 —— 该平台的全部行整体越过下一个平台组</summary>
    [RelayCommand]
    private Task MoveGroupDownAsync(AccountOrderRowViewModel? row)
    {
        if (row is null) return Task.CompletedTask;

        var start = GroupStartOf(row.PlatformKey);
        if (start < 0) return Task.CompletedTask;

        var len = GroupLengthAt(start);
        var nextStart = start + len;
        if (nextStart >= OrderRows.Count) return Task.CompletedTask;

        var nextLen = GroupLengthAt(nextStart);

        // 整块下移必须**倒序** Move：正序时被移走的槽位会把还没处理的成员往前带，错位
        for (var k = len - 1; k >= 0; k--)
            OrderRows.Move(start + k, nextStart + nextLen - len + k);

        return AfterOrderChangedAsync();
    }

    /// <summary>恢复默认顺序 —— 清掉本地覆盖（不是"排成默认序"），下次进来仍走默认</summary>
    [RelayCommand]
    private async Task ResetAccountOrderAsync()
    {
        try
        {
            _userOrder = [];
            await _orderStore.SaveAsync(_userOrder);
            // 重拉一次账号：默认序的来源（平台配置序 + 主号 + 登录时间）在服务端数据里，
            // 本地重算不如直接复用进页时那条已验证的路径
            await LoadAccountsAsync();
            BuildOrderRows();
            SubmitMessage = "已恢复默认顺序";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Publish] 恢复默认顺序失败");
            SubmitMessage = "恢复默认顺序失败";
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  落地
    // ═══════════════════════════════════════════════════════════════

    private async Task AfterOrderChangedAsync()
    {
        RefreshOrderBoundaries();
        SyncGlobalTargetsOrder();
        SortAllDraftTargets();
        await SaveOrderAsync();
    }

    /// <summary>
    /// 把账号芯片重排成排序行的次序（**原地 Move**，不重建集合）。
    /// 重建的代价是丢掉勾选态与芯片上的通知订阅 —— 用户排个顺序不该把勾好的账号全清空。
    /// </summary>
    private void SyncGlobalTargetsOrder()
    {
        for (var i = 0; i < OrderRows.Count; i++)
        {
            var want = OrderRows[i].SessionId;
            for (var j = i; j < GlobalTargets.Count; j++)
            {
                if (GlobalTargets[j].SessionId != want) continue;
                if (j != i) GlobalTargets.Move(j, i);
                break;
            }
        }
    }

    private async Task SaveOrderAsync()
    {
        try
        {
            _userOrder = [.. OrderRows.Select(r => r.SessionKey)];
            await _orderStore.SaveAsync(_userOrder);
        }
        catch (Exception ex) { Log.Warning(ex, "[Publish] 账号顺序保存失败"); }
    }

    /// <summary>
    /// 按当前账号顺序重排每条草稿的目标。
    /// 目标集合的**成员一个不动**（禁用态、平台参数覆盖都是按 key 存的，不受次序影响），
    /// 只调次序 —— 所以这个操作不会让任何已配置的内容丢失。
    /// </summary>
    private void SortAllDraftTargets()
    {
        if (GlobalTargets.Count == 0) return;

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < GlobalTargets.Count; i++)
            rank[AccountOrder.KeyOf(GlobalTargets[i].PlatformKey, GlobalTargets[i].SessionId)] = i;

        foreach (var draft in Drafts) SortTargets(draft, rank);
    }

    private static void SortTargets(PublishDraft draft, IReadOnlyDictionary<string, int> rank)
    {
        if (draft.Targets.Count < 2) return;

        // 已解绑账号的目标排在最后（预检会报出来），不影响在册账号之间的次序
        var ordered = draft.Targets
            .OrderBy(t => rank.GetValueOrDefault(AccountOrder.KeyOf(t.PlatformKey, t.SessionId), int.MaxValue))
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var at = draft.Targets.IndexOf(ordered[i]);
            if (at != i) draft.Targets.Move(at, i);
        }
    }

    /// <summary>账号解绑后清掉顺序表里的残留 key（与预设裁剪同批调用）</summary>
    private async Task PruneOrderAsync()
    {
        try
        {
            var alive = GlobalTargets.Select(t => t.SessionId).ToList();
            if (await _orderStore.PruneAsync(alive) > 0)
                _userOrder = await _orderStore.LoadAsync();
        }
        catch (Exception ex) { Log.Warning(ex, "[Publish] 账号顺序清理失败"); }
    }

    // ═══ 组定位（排序页内部用） ═══

    private int GroupStartOf(string platformKey)
    {
        for (var i = 0; i < OrderRows.Count; i++)
            if (OrderRows[i].PlatformKey == platformKey) return i;
        return -1;
    }

    private int GroupLengthAt(int start)
    {
        var len = 1;
        while (start + len < OrderRows.Count
               && OrderRows[start + len].PlatformKey == OrderRows[start].PlatformKey)
            len++;
        return len;
    }
}
