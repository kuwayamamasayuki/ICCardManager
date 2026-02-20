using Xunit;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// UI テストのコレクション定義。
    /// 同一コレクション内のテストクラスは逐次実行される。
    /// これにより、アプリの多重起動や DB ファイルの競合を防ぐ。
    /// </summary>
    [CollectionDefinition("UI")]
    public class UITestCollection
    {
    }
}
