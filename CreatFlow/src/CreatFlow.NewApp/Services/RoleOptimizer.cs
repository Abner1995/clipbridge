using CreatFlow.Media.Llm;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 角色 AI 优化的薄服务（DI 单例）——「取配置 → 解析引擎（本地引擎自动懒启动 llama-server）→ 优化」
/// 一条龙，合成页按钮与批量「用 AI 优化每条」共用同一入口（别在 VM 里再抄一遍三段式）。
/// 只读配置（<see cref="LlmConfigStore.LoadAsync"/>），永不 Save；
/// 引擎缺模型 / 缺 key 的异常消息本身就是给用户看的（见 LlmEngineResolver 注释）。
/// </summary>
public sealed class RoleOptimizer(LlmConfigStore config, LlamaServerHost host)
{
    public async Task<LlmOptions> ResolveAsync(CancellationToken ct = default)
        => await LlmEngineResolver.ResolveAsync(await config.LoadAsync(ct).ConfigureAwait(false), host, ct)
            .ConfigureAwait(false);

    public async Task<string> OptimizeAsync(string persona, string knowledge, string input, CancellationToken ct = default)
        => await LlmTextOptimizer.OptimizeAsync(await ResolveAsync(ct).ConfigureAwait(false),
            persona, knowledge, input, ct: ct).ConfigureAwait(false);
}
