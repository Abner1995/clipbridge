using Microsoft.EntityFrameworkCore.Design;

namespace CreatFlow.LocalStore;

/// <summary>
/// 设计时工厂 —— 只给 <c>dotnet ef migrations</c> 用（类库作为启动工程时必需）。
/// 迁移命令不会碰用户数据：路径落在 TEMP 下的临时库。
/// </summary>
public sealed class LocalDbContextFactory : IDesignTimeDbContextFactory<LocalDbContext>
{
    public LocalDbContext CreateDbContext(string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), "creatflow-localstore-design", "design.db");
        return LocalStoreInitializer.CreateContext(path);
    }
}
