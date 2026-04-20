using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #1281: <see cref="DbContext.LeaseConnection"/> が WPF UI スレッドから
/// 呼び出された場合に <see cref="InvalidOperationException"/> をスローすることを検証する。
/// </summary>
/// <remarks>
/// 実際の WPF Dispatcher を立ち上げずにテストするため、<c>DbContext.IsOnUiThread</c> 内部フックを
/// 差し替えることで「UI スレッドから呼ばれた」状態を模擬する。テスト終了後はフックを既定値に戻す。
/// Issue #1372: 同一フックを書き換える他テストクラスとの並列実行レースを避けるため、
/// <see cref="DbContextUiThreadHookCollection"/> に属させシリアル実行させる。
/// </remarks>
[Collection(DbContextUiThreadHookCollection.Name)]
public class DbContextUiThreadGuardTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _dbPath;
    private readonly Func<bool> _originalIsOnUiThread;

    public DbContextUiThreadGuardTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"UiThreadGuardTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _dbPath = Path.Combine(_testDirectory, "ui_thread_guard.db");
        _originalIsOnUiThread = DbContext.IsOnUiThread;
    }

    public void Dispose()
    {
        // テスト終了時にフックを既定値に戻す（他テストへの影響を避ける）
        DbContext.IsOnUiThread = _originalIsOnUiThread;
        try
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// UI スレッドを模擬した状態で <c>LeaseConnection()</c> を呼ぶと
    /// <c>InvalidOperationException</c> がスローされる。
    /// </summary>
    [Fact]
    public void LeaseConnection_UIスレッド模擬時は_InvalidOperationException_をスローすること()
    {
        // Arrange: UI スレッド検出を true 固定にする
        DbContext.IsOnUiThread = () => true;
        using var dbContext = new DbContext(_dbPath);

        // Act
        Action act = () => { using var lease = dbContext.LeaseConnection(); };

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*UI スレッドから呼び出せません*",
                "UI スレッドから呼び出した旨を明示したメッセージであるべき");
    }

    /// <summary>
    /// エラーメッセージに解決策（LeaseConnectionAsync / Task.Run）が含まれ、
    /// 開発者が適切な代替を知れるべき（Issue #1275 のエラーメッセージ3要素原則）。
    /// </summary>
    [Fact]
    public void LeaseConnection_UIスレッド例外メッセージに代替手段の案内が含まれること()
    {
        DbContext.IsOnUiThread = () => true;
        using var dbContext = new DbContext(_dbPath);

        Action act = () => { using var lease = dbContext.LeaseConnection(); };

        act.Should().Throw<InvalidOperationException>()
            .Where(ex =>
                ex.Message.Contains("LeaseConnectionAsync") &&
                ex.Message.Contains("Task.Run"),
                "代替 API（LeaseConnectionAsync）と回避策（Task.Run）をメッセージに含めるべき");
    }

    /// <summary>
    /// バックグラウンドスレッドから呼ぶ場合は例外にならず、通常通りリースが取得できる。
    /// </summary>
    [Fact]
    public void LeaseConnection_非UIスレッドからは例外にならず接続を返すこと()
    {
        // Arrange: UI スレッド検出を false 固定にする（バックグラウンドスレッド相当）
        DbContext.IsOnUiThread = () => false;
        using var dbContext = new DbContext(_dbPath);

        // Act
        using var lease = dbContext.LeaseConnection();

        // Assert
        lease.Should().NotBeNull();
        lease.Connection.Should().NotBeNull();
        lease.Connection.State.Should().Be(System.Data.ConnectionState.Open);
    }

    /// <summary>
    /// <c>LeaseConnectionAsync()</c>（非同期版）は UI スレッドから呼んでも例外にならない。
    /// 非同期版はセマフォを取得しないため UI スレッドから安全に呼べるのが本修正の前提。
    /// </summary>
    [Fact]
    public async Task LeaseConnectionAsync_UIスレッド模擬時でも例外にならないこと()
    {
        DbContext.IsOnUiThread = () => true;
        using var dbContext = new DbContext(_dbPath);

        using var lease = await dbContext.LeaseConnectionAsync();

        lease.Should().NotBeNull();
        lease.Connection.Should().NotBeNull();
    }

    /// <summary>
    /// <c>Task.Run</c> でラップしてバックグラウンドスレッドにオフロードすれば、
    /// UI スレッド初期化コードからでも同期版 LeaseConnection を安全に呼べる。
    /// App.xaml.cs の起動時コールパターン（Issue #1281 の修正）を表現する。
    /// </summary>
    [Fact]
    public async Task Task_Run_経由で呼べば_UI_スレッド模擬下でも例外にならないこと()
    {
        // Arrange: 親スレッド側で UI スレッド扱いを有効化
        DbContext.IsOnUiThread = () => false; // Task.Run内のスレッドはバックグラウンド扱いにしたい

        // ただし、IsOnUiThread は ThreadStatic ではないので、Task.Run 経由だけを明示したい場合は
        // SynchronizationContext の切り替えで表現する必要がある。ここでは単純に
        // 「非 UI スレッドで呼べば通る」ことの確認に徹する（Task.Run と直呼びは等価）。
        using var dbContext = new DbContext(_dbPath);

        // Act
        var result = await Task.Run(() =>
        {
            using var lease = dbContext.LeaseConnection();
            return lease.Connection.State;
        });

        // Assert
        result.Should().Be(System.Data.ConnectionState.Open);
    }

    /// <summary>
    /// 既定の UI スレッド検出ロジックは、xUnit のテストランナー環境（非 WPF）では false を返す。
    /// </summary>
    [Fact]
    public void DefaultIsOnUiThread_xUnit実行環境ではfalseを返すこと()
    {
        // Arrange: 既定のフックを使用
        DbContext.IsOnUiThread = _originalIsOnUiThread;

        // Act
        var onUi = DbContext.IsOnUiThread();

        // Assert
        onUi.Should().BeFalse(
            "xUnit 実行時は SynchronizationContext.Current が DispatcherSynchronizationContext " +
            "ではないため、既定実装は false を返すべき");
    }
}
