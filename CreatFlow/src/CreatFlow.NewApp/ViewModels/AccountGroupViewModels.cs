namespace CreatFlow.NewApp.ViewModels;

// ═══════════════════════════════════════════════════════════════════════════
//  账号分组（2026-10-14 用户需求）的 VM：
//   · AccountGroupChoiceViewModel —— 发布页/合成页「组条」上的一格（点 = 一键只勾选该组）
//   · AccountGroupRow —— 平台页「账号分组」卡里的一行（行内双视图：展示 ⇄ 编辑，学角色库）
//  行模型只持 Guid 与展示串，不持 PlatformSession 对象引用（同发布页条目 VM 的约定）
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 组条上的一格。「全部」是第 0 格哨兵（<see cref="AccountGroups.AllGroupsId"/>，
/// 不落盘、不是真分组）；空组（0 个在册成员）整格禁用。
/// 用 Button 不用 ToggleButton：语义是「单选应用一组」，没有「再点一下取消」。
/// </summary>
public sealed partial class AccountGroupChoiceViewModel : ObservableObject
{
    /// <summary>哨兵或组 Id</summary>
    public required string Id { get; init; }

    /// <summary>「全部」/ 组名</summary>
    public required string Name { get; init; }

    /// <summary>可见成员数（在册 ∩ 组；「全部」= 全体芯片数）</summary>
    [ObservableProperty] private int _count;

    /// <summary>当前勾选恰好等于这一格（页面重算，见 RefreshActiveGroup）</summary>
    [ObservableProperty] private bool _isActive;

    public string Text => Id == AccountGroups.AllGroupsId
        ? "全部"
        : Count > 0 ? $"{Name} {Count}" : Name;

    /// <summary>空组：禁用（灰、不响应点击）—— 点了会「全不勾」，像出了 bug</summary>
    public bool IsEmpty => Id != AccountGroups.AllGroupsId && Count == 0;

    partial void OnCountChanged(int value)
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>
/// 平台页「账号分组」卡里的一行。展示态 = 名称 + 成员摘要 + [编辑][删除]；
/// 编辑态 = 名称输入 + 按平台的成员复选清单（<see cref="Blocks"/>）+ [保存][取消]。
/// 编辑是**草稿**：保存才落盘；取消 = 丢弃重进（BeginEdit 会从 Committed 重建）。
/// </summary>
public sealed partial class AccountGroupRow : ObservableObject
{
    public string Id { get; private set; } = "";
    public bool IsNew { get; private set; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isConfirmingDelete;
    [ObservableProperty] private string _error = "";

    public bool HasError => !string.IsNullOrEmpty(Error);
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    /// <summary>最近一次「已提交」的成员集合（取消/重进编辑的基准）</summary>
    internal HashSet<Guid> Committed { get; private set; } = [];

    /// <summary>编辑态草稿：按平台分块的复选清单</summary>
    public ObservableCollection<AccountGroupPlatformBlock> Blocks { get; } = [];

    /// <summary>编辑态里有没有可勾的账号（没有 → 提示先去登录；纯展示，BeginEdit 时重算）</summary>
    public bool HasBlocks => Blocks.Count > 0;

    public static AccountGroupRow FromGroup(AccountGroup g, IReadOnlyList<PlatformGroup> platforms)
    {
        var row = new AccountGroupRow
        {
            Id = g.Id,
            Name = g.Name,
            IsNew = false,
            Committed = [.. g.Sessions],
        };
        row.RefreshSummary(platforms);
        return row;
    }

    public static AccountGroupRow NewDraft() => new() { Id = "", IsNew = true, Summary = "还没有账号" };

    /// <summary>进入编辑态：从「已提交」重建草稿块（上一轮没保存的改动不残留）</summary>
    public void BeginEdit(IReadOnlyList<PlatformGroup> platforms)
    {
        Blocks.Clear();
        foreach (var pg in platforms)
        {
            if (pg.Sessions.Count == 0) continue;
            var block = new AccountGroupPlatformBlock
            {
                PlatformName = pg.DisplayName,
                BrandHex = pg.BrandColorHex,
            };
            foreach (var s in pg.Sessions)
            {
                block.Options.Add(new AccountGroupMemberOption
                {
                    SessionId = s.Id,
                    Label = s.DisplayLabel,
                    IsPrimary = s.IsPrimary,
                    IsChecked = Committed.Contains(s.Id),
                });
            }
            Blocks.Add(block);
        }
        OnPropertyChanged(nameof(HasBlocks));
        Error = "";
        IsEditing = true;
    }

    public void CancelEdit()
    {
        IsEditing = false;
        IsConfirmingDelete = false;
        Error = "";
    }

    /// <summary>保存前的校验 + 生成组对象；校验不过 → 错误落行内（红字），返回 false。</summary>
    public bool TryCommit(IReadOnlyList<AccountGroup> existing, out AccountGroup group)
    {
        group = new AccountGroup();
        var err = AccountGroups.ValidateName(Name, existing, IsNew ? null : Id);
        if (err is not null) { Error = err; return false; }

        group = new AccountGroup
        {
            Id = IsNew ? Guid.NewGuid().ToString("N") : Id,
            Name = Name.Trim(),
            Sessions = [.. Blocks.SelectMany(b => b.Options).Where(o => o.IsChecked).Select(o => o.SessionId)],
        };
        return true;
    }

    /// <summary>落盘成功后的回填：接管新 Id / 名称 / 成员，刷新摘要，退出编辑态</summary>
    public void Commit(AccountGroup g, IReadOnlyList<PlatformGroup> platforms)
    {
        Id = g.Id;
        Name = g.Name;
        Committed = [.. g.Sessions];
        IsNew = false;
        RefreshSummary(platforms);
        CancelEdit();
    }

    internal void RefreshSummary(IReadOnlyList<PlatformGroup> platforms)
    {
        var labels = new List<string>();
        foreach (var pg in platforms)
            foreach (var s in pg.Sessions)
                if (Committed.Contains(s.Id)) labels.Add($"{pg.DisplayName}·{s.DisplayLabel}");

        Summary = labels.Count switch
        {
            0 => "还没有账号",
            <= 3 => $"{labels.Count} 个账号：{string.Join("、", labels)}",
            _ => $"{labels.Count} 个账号：{string.Join("、", labels.Take(3))} 等",
        };
    }
}

/// <summary>编辑态里「一个平台一块」：块头（色点 + 平台名）+ 该平台的账号复选清单</summary>
public sealed partial class AccountGroupPlatformBlock : ObservableObject
{
    public required string PlatformName { get; init; }
    public required string BrandHex { get; init; }
    public ObservableCollection<AccountGroupMemberOption> Options { get; } = [];
}

/// <summary>编辑态清单里的一行（一个账号的勾选格）。CheckBox 双向绑 IsChecked，
/// 不走命令 —— 避开嵌套 ItemsControl 里 $parent 的坑。</summary>
public sealed partial class AccountGroupMemberOption : ObservableObject
{
    public required Guid SessionId { get; init; }
    public required string Label { get; init; }
    public required bool IsPrimary { get; init; }

    [ObservableProperty] private bool _isChecked;
}
