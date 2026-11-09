using CreatFlow.Media.Llm;
using CreatFlow.Media.Video;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// AI 设置页底座自检（`--selftest` 会一并跑）—— 不启动 UI、不起 llama-server、不碰真模型文件。
///
/// 这一块的风险全在**规则**上：老版 llm.json 的引擎反推（升上来的人配置不能丢）、
/// 档位解析顺序（用户下载的档必须盖过捆绑档 —— 与 Kokoro 相反，是刻意的）、
/// 截断文件不算就绪（半截下载不能当可用模型）、输出路径必须在 runtimes\ 下（否则 VS 的
/// Avalonia 预览器会把 llama 原生 dll 当托管程序集预加载 → GGML abort 崩掉整个预览器，
/// 2026-10-10 实录）。几条都是"错了不会崩、只会静默用错东西"（最后一条是崩别人），必须钉死。
/// 角色库「文案优化」prompt 的关键措辞同规格守卫（[6]，2026-10-11 追加）。
///
/// 结果写 %TEMP%/newapp_llm_selftest.txt。
/// </summary>
public static class LlmSelfTest
{
    /// <summary>测试档位：1000 字节小文件，避免造 2.5GB 假模型</summary>
    private static LlmModelTier TestTier(string key, string fileName) =>
        new(key, $"测试档 {key}", fileName, 1000, 1, "http://example.invalid/a", "http://example.invalid/b");

    public static int Run()
    {
        var sb = new StringBuilder();
        var failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        sb.AppendLine("═══ AI 设置页自检（本地 LLM）═══");

        // ── 1. 老版 llm.json 引擎反推（升级用户配置兼容） ──
        sb.AppendLine("\n[1] 老格式配置反推（无 engine 字段）");
        Check("老版 Ollama 配置（11434）→ 本地（Ollama 已下线，不让用户卡在装过的旧路径）",
            LlmConfigStore.MapFromFile(null, null, "k", "qwen2.5:7b", "http://127.0.0.1:11434/v1").Engine
            == LlmEngineResolver.LocalEngine);
        Check("老版 Ollama（用户改过端口）→ 本地",
            LlmConfigStore.MapFromFile(null, null, "k", "m", "http://192.168.1.5:11434/v1").Engine
            == LlmEngineResolver.LocalEngine);
        Check("DeepSeek baseUrl → DeepSeek",
            LlmConfigStore.MapFromFile(null, null, "k", "deepseek-v4-flash", "https://api.deepseek.com").Engine
            == "DeepSeek");
        Check("无 baseUrl → 本地（零 key 开箱默认）",
            LlmConfigStore.MapFromFile(null, null, null, null, null).Engine == LlmEngineResolver.LocalEngine);
        Check("未知 baseUrl → 本地（不假装认识）",
            LlmConfigStore.MapFromFile(null, null, "k", "m", "https://unknown.example.com/v1").Engine
            == LlmEngineResolver.LocalEngine);
        Check("新格式显式 local 原样保留",
            LlmConfigStore.MapFromFile("local", "qwen3-8b", null, null, null) is
                { Engine: "local", LocalTierKey: "qwen3-8b" });

        // ── 2. 档位解析顺序（用户档优先于捆绑档 —— 与 Kokoro 相反） ──
        sb.AppendLine("\n[2] 档位解析顺序 + 完整性容差");
        var root = Path.Combine(Path.GetTempPath(), "creatflow-llm-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        var localDir = Path.Combine(root, "user");
        var bundledDir = Path.Combine(root, "bundled");
        Directory.CreateDirectory(localDir);
        Directory.CreateDirectory(bundledDir);
        try
        {
            var tier = TestTier("t1", "t1.gguf");

            Check("两处都无 → null", LlmModelStore.ResolvePath(tier, localDir, bundledDir) == null);

            File.WriteAllBytes(Path.Combine(bundledDir, "t1.gguf"), new byte[1000]);
            var onlyBundled = LlmModelStore.ResolvePath(tier, localDir, bundledDir);
            Check("只有捆绑 → 用捆绑", onlyBundled == Path.Combine(bundledDir, "t1.gguf"),
                onlyBundled ?? "(null)");

            File.WriteAllBytes(Path.Combine(localDir, "t1.gguf"), new byte[1000]);
            var both = LlmModelStore.ResolvePath(tier, localDir, bundledDir);
            Check("两处都有 → **用户档优先**（下载的 8B 必须盖过捆绑 4B）",
                both == Path.Combine(localDir, "t1.gguf"), both ?? "(null)");

            File.WriteAllBytes(Path.Combine(localDir, "t1.gguf"), new byte[900]);   // 90% → 截断
            var truncated = LlmModelStore.ResolvePath(tier, localDir, bundledDir);
            Check("用户档截断（90%）→ 回落捆绑档，不把半截文件当可用",
                truncated == Path.Combine(bundledDir, "t1.gguf"), truncated ?? "(null)");

            // ── 3. ResolveAnyPath 回落（档位表可注入，用测试档验规则） ──
            sb.AppendLine("\n[3] 偏好档回落（偏好档没就绪 → 按表序回落；全不就绪 → null）");
            var tierA = TestTier("a", "a.gguf");
            var tierB = TestTier("b", "b.gguf");
            var table = new[] { tierA, tierB };

            Check("表内无就绪档 → null",
                LlmModelStore.PickTier(table, "a", localDir, bundledDir) == null);

            File.WriteAllBytes(Path.Combine(bundledDir, "b.gguf"), new byte[1000]); // 只备 b 档
            Check("偏好档未就绪、表内另一档就绪 → 回落它",
                LlmModelStore.PickTier(table, "a", bundledDir, bundledDir) is { Key: "b" },
                LlmModelStore.PickTier(table, "a", bundledDir, bundledDir)?.Key ?? "(null)");
            Check("偏好档就绪 → 用它（不回落）",
                LlmModelStore.PickTier(table, "b", bundledDir, bundledDir) is { Key: "b" });
            Check("偏好档 Key 无效（旧配置残留）→ 按表序回落第一个就绪档",
                LlmModelStore.PickTier(table, "已删除的档", bundledDir, bundledDir) is { Key: "b" });
            Check("真档位表：两目录都空 → null",
                LlmModelStore.ResolveAnyPath("qwen3-8b", localDir, bundledDir) == null);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        // ── 4. 档位表不变量 ──
        sb.AppendLine("\n[4] 档位表不变量");
        Check("Key 唯一", LlmModelStore.Tiers.Select(t => t.Key).Distinct().Count() == LlmModelStore.Tiers.Count);
        Check("默认档 = 第一个 = qwen3-4b（捆绑默认）", LlmModelStore.Default.Key == "qwen3-4b",
            LlmModelStore.Default.Key);
        Check("Key 与 FileName 稳定（配置持久化依赖，改名=用户选择全丢）",
            LlmModelStore.Tiers.Any(t => t.Key == "qwen3-4b" && t.FileName == "Qwen3-4B-Q4_K_M.gguf")
            && LlmModelStore.Tiers.Any(t => t.Key == "qwen3-8b" && t.FileName == "Qwen3-8B-Q4_K_M.gguf"));
        Check("下载 URL 双源（ModelScope 优先 + hf-mirror 兜底）",
            LlmModelStore.Tiers.All(t => t.DownloadUrl.Contains("modelscope.cn") && t.MirrorUrl.Contains("hf-mirror.com")));
        // 输出路径守卫：VS 的 Avalonia 预览器把 OutDir 下非 runtimes\ 子树的未知 .dll 全当托管程序集
        // 预加载（LoadFromAssemblyPath），llama 原生 dll 会 GGML_ASSERT abort 整崩预览器（2026-10-10）。
        // 三处联动：csproj 的 Link、LlamaServerHost.EngineDir、LlmModelStore.BundledDir —— 改回 runtime 必炸。
        var engineDir = LlamaServerHost.EngineDir.Replace('/', '\\');
        Check("引擎/模型输出路径在 runtimes\\llm 下（预览器崩溃守卫，勿改回 runtime\\llm）",
            engineDir.EndsWith(@"runtimes\llm", StringComparison.OrdinalIgnoreCase)
            && LlmModelStore.BundledDir.Replace('/', '\\')
                .StartsWith(engineDir, StringComparison.OrdinalIgnoreCase),
            engineDir);

        // ── 5. 文案 prompt 关键措辞（2026-10-10 A/B 实测结论的守卫） ──
        // 逐条钉死验证过的原句：改一个词就可能退回"信息丢一半 + 图片镜超字数"的旧行为，
        // 改前先重跑对照实验（脚本与数据记在 LlmCopywriter.BuildPrompt 的注释里）。
        sb.AppendLine("\n[5] 文案 prompt 关键措辞（A/B 实测过的原句）");
        var prompt = LlmCopywriter.BuildPrompt("测试资料", new[]
        {
            new ComposeMediaItem("a.jpg", MediaKind.Image),
            new ComposeMediaItem("b.mp4", MediaKind.Video),
            new ComposeMediaItem("c.jpg", MediaKind.Image),
            new ComposeMediaItem("d.jpg", MediaKind.Image),
            new ComposeMediaItem("e.mp4", MediaKind.Video),
        });
        Check("图片镜放宽条款（8~12 为好、保信息放宽到 20）",
            prompt.Contains("2. 图片镜：每句 8~12 个字为好，**为保住关键信息最多可放宽到 20 字**（图片会随配音延长停留，不影响画面）"));
        Check("视频镜硬上限 + 挪图镜条款",
            prompt.Contains("3. 视频镜：每句 **12~20 个字是硬上限**（超出会画面定格，很难看）；信息放不下就挪到相邻的图片镜里"));
        Check("逐字保全 + 拆句条款",
            prompt.Contains("5. 【硬信息保全，最高优先级】资料中的名称、地址、价格/费用、证件/编号、开发商、联系方式等关键信息必须完整保留、逐字出现，不得省略或改写丢失；一句里放不下就拆到相邻句子里分别念出（遵守上面的镜头字数规则）。写之前先逐一核对资料里每条信息都已落句"));
        Check("句数 = 素材数（5 镜）", prompt.Contains("恰好生成 5 句"));
        Check("素材镜型清单齐全（图/视混排）",
            prompt.Contains("第1镜: 图片") && prompt.Contains("第2镜: 视频素材") && prompt.Contains("第5镜: 视频素材"));

        // ── 6. 文案优化 prompt 关键措辞（角色库；「先优化裸文案、再套模板」的顺序语义靠它） ──
        // 与 [5] 同规矩：逐字钉死；改词 = 改行为，改前先跑真机对照（见 LlmTextOptimizer 头注释）。
        sb.AppendLine("\n[6] 文案优化 prompt 关键措辞（角色库）");
        var optPrompt = LlmTextOptimizer.BuildPrompt("你是房淘惠，一名房产销售主播", "君临天下三期，89 平三居",
            "君临天下三期，八十九平三居\n南北通透\n总价两百万起");
        Check("人设原样进 prompt", optPrompt.Contains("你是房淘惠，一名房产销售主播"));
        Check("只输出文案本身条款",
            optPrompt.Contains("1. 只输出改写后的文案本身：不要解释、不要点评、不要加标题或编号、不要用引号或代码块包起来，不要输出任何多余文字。"));
        Check("改写范例（4B 实测：没有范例会照抄资料）",
            optPrompt.Contains("改写：幸福里这套120平的四居室，南北通透，走几步就是地铁口，还带学区，550万就能拿下。"));
        Check("不许加信息条款（4B 实测：不写会编出开发商/交付时间）",
            optPrompt.Contains("2. 【不许加信息】只允许使用「待优化文案」和「角色常用资料」里出现过的信息：原文没提的开发商、交付时间、优惠活动、联系方式等事实一律不许自己编；语气可以更热情，但事实不能加。"));
        Check("信息不能丢条款（数字不能改）",
            optPrompt.Contains("3. 【信息不能丢】楼盘/小区名称、地址、面积、户型、价格/费用、电话/微信号、证件编号、时间等一个都不能少；数字不能改（不要换成汉字、不要换算或四舍五入）。"));
        Check("必须成句条款（词组罗列要写成完整句子）",
            optPrompt.Contains("4. 【必须成句】把词组罗列改写成完整顺口的句子（不写「89平 三居」，要写「这套89平的三居」）；加自然的衔接，句子短、有节奏；不要书面语、不要空喊口号。"));
        Check("不抢模板的活（开场白/结尾号召语归模板；4B 实测：不写会自己加「私信咨询」结尾）",
            optPrompt.Contains("6. 不要自己加开场白、自我介绍、结尾的总结或号召语（包括「关注我」「私信咨询」这类——这些由角色台词模板统一负责）：把资料讲完就结束。"));
        Check("分行规则数据驱动（3 行）",
            optPrompt.Contains("原文共 3 行") && optPrompt.Contains("仍输出 3 行"));
        Check("输入与知识库原样进 prompt",
            optPrompt.Contains("君临天下三期，八十九平三居") && optPrompt.Contains("【角色常用资料】"));
        Check("占位符不泄漏（模板是本地概念，绝不进 LLM）", !optPrompt.Contains("{内容}"));

        var optSingle = LlmTextOptimizer.BuildPrompt("人设", null, "单行资料");
        Check("单行输入 → 不要换行；知识库为空 → 整节省略",
            !optSingle.Contains("原文共") && optSingle.Contains("不要换行")
            && !optSingle.Contains("【角色常用资料】"));

        Check("Cleanup：去整体包裹的引号", LlmTextOptimizer.Cleanup("  “优化后。”\n") == "优化后。",
            LlmTextOptimizer.Cleanup("  “优化后。”\n"));
        Check("Cleanup：去代码围栏 + 行首标签",
            LlmTextOptimizer.Cleanup("```\n优化后的文案：正常文本。\n```") == "正常文本。",
            LlmTextOptimizer.Cleanup("```\n优化后的文案：正常文本。\n```"));
        Check("Cleanup：正常文本原样（「1.2 米」这类数字不被剥）",
            LlmTextOptimizer.Cleanup("主卧 1.2 米进深，阳台很大。") == "主卧 1.2 米进深，阳台很大。");

        var text = sb.ToString();
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "newapp_llm_selftest.txt"), text); } catch { }
        Console.Write(text);
        Console.WriteLine(failures == 0 ? "LLM 自检全部通过" : $"LLM 自检失败 {failures} 项");
        return failures;
    }
}
