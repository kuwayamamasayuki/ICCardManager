namespace ICCardManager.Tests.Data;

/// <summary>
/// テスト用のDbContextファクトリ
/// インメモリSQLiteを使用して、テスト毎に独立したデータベースを提供
/// </summary>
public static class TestDbContextFactory
{
    /// <summary>
    /// テスト用のDbContextインスタンスを作成
    /// インメモリSQLiteを使用し、初期化済みの状態で返す
    /// </summary>
    public static ICCardManager.Data.DbContext Create()
    {
        // インメモリSQLiteを使用
        var dbContext = new ICCardManager.Data.DbContext(":memory:");
        dbContext.InitializeDatabase();
        return dbContext;
    }
}
