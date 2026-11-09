namespace CreatFlow.NewApp.Services;

/// <summary>
/// 合成单元模板文件夹 —— 「合成队列」页「新建模板」落盘的示例单元
/// （2026-10-11 用户要求：添加按钮像发布页那样先生成文件夹，把「要准备哪些素材」
/// 变成看得见的文件，用户自己替换后整批导入）。
///
/// 四个文件：3 张示例图（ffmpeg 现画：纯色底 + 「请替换」提示字）+ 资料.txt（示例文案 + 注释）
/// + 说明.txt（目录约定与四步用法）。
/// 已存在的文件**一律不动**（用户改过的模板比模板值钱）—— 重复点「新建模板」幂等。
/// 产物必须能被 <see cref="ComposeFolderScanner"/> 直接扫出一条合法单元（--selftest 钉死往返）。
/// </summary>
internal static class ComposeUnitTemplate
{
    public const string FolderName = "合成单元模板";
    public const string ReadmeFileName = "说明.txt";
    public const int SampleImageCount = 3;

    /// <summary>示例文案（资料.txt 里唯一生效的那行 —— 用户没替换就合成，读出的是它，人话不是说明文）</summary>
    internal const string SampleCopy = "示例文案：君临天下三期，八十九平三居，南北通透，总价两百万起。";

    /// <summary>资料.txt：注释行教怎么改，生效行是示例文案（扫描层会丢掉 # 行）</summary>
    internal const string SampleTextFile = $"""
        # 这个文件拿去配音、做字幕 —— 把下面这行替换成你的文案。
        # 行首 # 是注释（不会读出来）；空行自动忽略；整段直接写、一行一句都行，
        # 句号 / 问号 / 感叹号处会断句。
        {SampleCopy}
        """;

    /// <summary>说明.txt：目录长什么样 + 四步用法 + 什么跟随页面偏好</summary>
    internal const string ReadmeText = """
        目录约定 —— 一个文件夹 = 一条待合成视频
        ────────────────────────────────────────
          房源A/                    文件夹名 = 发布标题（发布单元的文件夹名也是它）
            1.jpg  2.jpg  3.jpg     素材：图片或视频，按文件名顺序出场（1、2、…、10 自然排）
            资料.txt                文案：软件拿它配音（用你选好的音色）并生成字幕 —— 就在这里写
            夏日.flac               配乐：有 资料.txt 时，文件夹里的音频都当背景音乐（bgm 开头的优先）
            配音.mp3                （没有 资料.txt 时）第一个音频当配音用 —— 这种情况没有字幕

        怎么用（四步）
          1. 把「合成单元模板」复制一份，改成你的标题（要几条就复制几份）
          2. 替换里面的示例图片（张数随意；全删、只放视频也行）
          3. 打开 资料.txt 换成你的文案（行首 # 是注释，不会读出来）
          4. 回软件「合成队列」页点「从文件夹批量添加」，选放这些文件夹的总目录

        说明
          有 资料.txt 就一定按文案配音 + 出字幕；这时音频文件只当配乐（不想配乐就删掉音频）
          音色 / 语速 / 画幅 / 输出目录 / BGM 音量跟随「合成视频」页里的偏好
          画面动效（推拉摇移）与转场随机分配：同一条视频里不重样、每次合成重新随机
          资料.txt 要 UTF-8 编码（Windows 记事本默认就是）；编码不对会读出乱码并被跳过
          图片每张约 3 秒，最后一张自动补到配音结束；视频素材按完整时长出场
        """;

    /// <summary>
    /// 在 <paramref name="parent"/> 下建「合成单元模板」并补齐文件（已存在的保持原样）。
    /// 示例图单张失败不拦整体（少一张示例图不影响文件夹本身可用）。
    /// </summary>
    internal static async Task<TemplateResult> WriteAsync(string parent, CancellationToken ct = default)
    {
        var folder = Path.Combine(parent, FolderName);
        var written = new List<string>();
        var kept = new List<string>();
        try
        {
            Directory.CreateDirectory(folder);

            (string Name, string Text)[] texts =
                [(ComposeFolderScanner.TextFileName, SampleTextFile), (ReadmeFileName, ReadmeText)];
            foreach (var (name, text) in texts)
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path)) { kept.Add(name); continue; }
                await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
                written.Add(name);
            }

            // 示例图：ffmpeg 现场画（纯色底 + 提示字）——用户一眼看懂「这几张要换成我的照片」
            (string Name, string Color)[] samples =
                [("1.jpg", "E8F0FE"), ("2.jpg", "E6F4EA"), ("3.jpg", "FDF2E3")];
            for (var i = 0; i < samples.Length; i++)
            {
                var (name, color) = samples[i];
                var path = Path.Combine(folder, name);
                if (File.Exists(path)) { kept.Add(name); continue; }
                try
                {
                    await FfmpegVideoComposer.GenerateSampleImageAsync(
                        path, 1080, 1920, $"示例图片 {i + 1} · 请替换成你的素材", color, ct).ConfigureAwait(false);
                    written.Add(name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warning(ex, "[Compose] 模板示例图生成失败 {Path}（不影响模板用法）", path);
                }
            }
            return new TemplateResult(folder, written, kept);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Compose] 生成合成模板文件夹失败 {Folder}", folder);
            return new TemplateResult(folder, written, kept, ex.Message);
        }
    }
}
