namespace CreatFlow.NewApp.Services;

/// <summary>生成模板文件夹的结果（发布单元模板 / 合成单元模板共用）。
/// <paramref name="Error"/> 非空 = 没建成（已记日志）。</summary>
internal sealed record TemplateResult(
    string Folder,
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Kept,
    string? Error = null)
{
    /// <summary>文件都已经在（用户之前建过模板）—— 提示语要说「已存在」，别说「已生成」</summary>
    public bool AllKept => Error is null && Written.Count == 0;
}

/// <summary>
/// 发布单元模板文件夹 —— 用户点「新建模板」时落盘的那两个文件（设计文档 §5.1）。
///
/// 存在意义：目录约定光靠页面上的说明文字，用户看完还是不知道从哪下手；
/// 给一份**可以直接复制改名**的实体文件夹，「一个文件夹 = 一条视频」这件事就不用解释了。
///
/// ⚠️ <see cref="ParamsText"/> 必须满足 <c>PublishParamsFile.Parse(ParamsText).Problems</c> 为空
/// —— 模板自己要是带警告，等于教用户写错。这条由 <c>--selftest</c> 钉死。
/// 所以可选的键一律**注释掉**，只留默认值行是生效的。
/// </summary>
internal static class PublishUnitTemplate
{
    public const string FolderName = "发布单元模板";
    public const string ReadmeFileName = "说明.txt";

    /// <summary>params.txt 样板：写满「可写什么」，但生效的只有默认值那几行。</summary>
    internal const string ParamsText = """
        # 发布单元参数
        # 每行「字段名: 值」；中文冒号或英文冒号都行。
        # 行首 # 是注释；值后面跟的说明（如「可见性: 公开   # 公开 / 仅自己」）也算注释。
        # 这个文件不是必须的 —— 不写就用「文件夹名 = 标题」，简介留空，封面自动抽首帧。
        # 字段名写错不会让导入失败，只是那一行不生效（导入时会在页面上提示是第几行）。

        # 标题: 我的视频
        # 简介: 记录夏天的第一场海

        # 话题: 夏日
        # 话题: 海边
        # 话题一行一个，写多行就行；也可以写成  话题: 夏日, 海边, 旅行
        # 行首的 # 会被自动去掉，所以「话题: #夏日」和「话题: 夏日」是一样的
        # ⚠️ #话题 只写在这里，别写进「简介」：发布时软件会把话题带 # 追加到正文末尾，
        #    简介里再写一个对不上话题的 #，抖音会整条拒收（status_code 35「视频描述参数不合法」）

        # 话题ID: 抖音:家居=1658571727441931
        # 话题ID: 快手:家居=140808
        # 写法是「平台:话题名=平台话题ID」，只对写出来的平台生效。
        # 一般不用写：在软件里点「获取平台推荐」就会带上 ID。这里用于脚本固定话题，
        # 导入时不再联网解析；话题名照样显示在条目上。不写 ID 的平台按纯文本发。

        # 官方活动: 抖音:7702034615116174390
        # 官方活动: B站:我在B站搞装修=4070872
        # 写法是「平台:活动ID」或「平台:活动名=活动ID」，只对写出来的平台生效。
        # 选了活动会自动带上它关联的话题；活动 ID 从软件的活动列表里复制最稳。

        # 位置: 苏州姑苏
        # 位置走联邦解析：一次搜索，四个平台各拿自己库里的那个 POI（抖音/快手/小红书/视频号）
        # 搜不到的平台会标成「该平台不带位置」，不会瞎填

        # 合集: 苏州房产
        # 写合集的**名字**（在软件的「合集」页建好、并同步到账号）。
        # 合集是软件里的概念：一个合集在各平台各有自己的 id，导入时按名字查已同步的映射铺上去。
        # 名字对不上 / 某个账号没同步过 → 那一条不带合集，页面上会写清楚原因

        可见性: 公开
        # 可写：公开 / 仅自己 / 仅互关好友可见 / 只给谁看 / 不给谁看
        # 「只给谁看 / 不给谁看」还要在软件里补一份用户名单

        允许下载: 是
        # 可写：是 / 否

        发布时间: 立即
        # 可写：立即，或 2026-09-20 20:00 这样的时间

        内容声明: 不声明
        # 可写：不声明 / 内容由 AI 生成 / 虚构演绎 / 内容为个人观点 / 含营销推广
        #      / 内容为转载 / 内容为自行拍摄
        # 「内容为转载 / 内容为自行拍摄」还有补充信息（来源、拍摄时间与地点），要到软件里填

        # 原创: 是
        # 对小红书 / B站 / 头条生效（其余平台没有这个字段）。
        # 不写 = 跟随账号预设，不会覆盖你在「发布预设」里配的东西

        # B站分区: 21
        # B站二级分区: 1015
        # B站投稿的分区 ID；默认 21 / 1015 = 家装房产（产品定位），只对 B站目标生效。
        # 一般不用写：不知道分区 ID 时保持默认最稳（写错平台会拒稿）。

        # 头条赞赏: 是
        # 可写：是 / 否（仅头条目标；默认否）
        # 头条模板: 模板名
        # 头条滤镜: 滤镜名
        # 模板 / 滤镜仅头条目标生效，一般不用写。
        # 小红书要求视频时长大于 10 秒 —— 不足 10 秒这条声明用不了（提交前预检会拦下）

        # ── 下面这些不在这里填（按账号记的，「发布预设」里配一次就够）──
        #   快手「同城可见 / 允许同框」· 抖音「热点词」
        """;

    /// <summary>说明.txt：目录长什么样 + 四步用法 + 一次 10 条 + 什么不在这里填。</summary>
    internal const string ReadmeText = """
        目录约定 —— 一个文件夹 = 一条待发布视频
        ────────────────────────────────────────
          我的视频/               文件夹名 = 默认标题（可被 params.txt 的「标题:」覆盖）
            我的视频.mp4          视频，必需。目录里有多个视频时只取第一个
            params.txt            参数，可选，就是旁边那份模板
            cover.jpg             封面，可选。jpg / png / webp 都行；没有就自动抽视频首帧

        怎么用（四步）
          1. 把这个模板文件夹复制一份，改成你要的标题
          2. 把视频拖进去（一个文件夹只放一个视频）
          3. 想指定封面就丢一张 cover.jpg 进去；不丢就由软件抽首帧
          4. 回软件点「导入文件夹」，一次最多选 10 个文件夹

        一次最多导入 10 条
          选中的文件夹展开后超过 10 条时，多出来的会留在「待导入」里，
          点「继续导入」接着导，不用重新选一遍。

        params.txt 能写什么
          标题 · 简介 · 话题 · 话题ID · 官方活动 · 位置 · 合集 · 可见性 · 允许下载 · 发布时间 · 内容声明
          · 原创（小红书 / B站 / 头条；小红书视频要大于 10 秒）
          格式是「字段名: 值」，行首 # 是注释，值后面的说明也算注释。
          字段名写错只是那一行不生效，不会让导入失败 —— 但导入时页面上会写明是第几行。
          话题写在「话题:」行里，不要写进简介 —— 发布时软件会把话题追加到正文末尾；
            简介里写一个没对上话题的 #，抖音会整条拒收。
          话题ID / 官方活动 是高级写法（如「抖音:家居=1658…」），必须写平台前缀；
            一般不写，在软件里点「获取平台推荐」就会带 ID。
          位置写的是关键词（如「苏州姑苏」），导入时软件去各平台搜一次，各拿各自的 POI。
          合集写的是名字（如「苏州房产」），导入时按名字查你在「合集」页同步下来的映射。

        这些不在这里填（按账号记的，在「发布预设」里配一次就够）
          快手「同城可见 / 允许同框」· 抖音「热点词」
          可见性的用户名单（只给谁看 / 不给谁看）· 声明的补充信息（转载来源、拍摄时间与地点）
          —— 这些都到软件的条目编辑器里按账号调。
        """;

    /// <summary>
    /// 在 <paramref name="parent"/> 下建「发布单元模板」并补齐两个文件。
    ///
    /// 已存在的文件**一律不动**（用户改过的模板比模板本身值钱），只补缺失的 —— 所以
    /// 重复点「新建模板」是幂等的，也不会在磁盘上堆出「发布单元模板-2」「-3」。
    /// </summary>
    internal static TemplateResult Write(string parent)
    {
        var folder = Path.Combine(parent, FolderName);
        var written = new List<string>();
        var kept = new List<string>();
        try
        {
            Directory.CreateDirectory(folder);

            (string Name, string Text)[] files =
                [(PublishParamsFile.FileName, ParamsText), (ReadmeFileName, ReadmeText)];
            foreach (var (name, text) in files)
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path)) { kept.Add(name); continue; }
                File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                written.Add(name);
            }
            return new TemplateResult(folder, written, kept);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 生成模板文件夹失败 {Folder}", folder);
            return new TemplateResult(folder, written, kept, ex.Message);
        }
    }
}
