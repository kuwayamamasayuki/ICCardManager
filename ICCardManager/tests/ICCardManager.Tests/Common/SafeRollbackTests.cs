using System;
using FluentAssertions;
using ICCardManager.Common;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// <see cref="SafeRollback"/> の単体テスト（Issue #1831）
/// </summary>
public class SafeRollbackTests
{
    [Fact]
    public void TryRollback_正常時はロールバックが1回だけ実行されること()
    {
        var invocations = 0;

        SafeRollback.TryRollback(() => invocations++, NullLoggerStub(), "テスト操作");

        invocations.Should().Be(1);
    }

    /// <summary>
    /// ロールバック自体が失敗しても、二次例外を呼び出し元へ漏らさないこと
    /// </summary>
    /// <remarks>
    /// これが本 Issue の中核。二次例外が抜けると、本来の失敗要因（<c>SQLiteException(Busy)</c> 等）が
    /// 置き換わり、上位の型別分岐（リトライ・文言変換）がすべて外れる。
    /// </remarks>
    [Fact]
    public void TryRollback_ロールバックが失敗しても例外を漏らさないこと()
    {
        Action act = () => SafeRollback.TryRollback(
            () => throw new InvalidOperationException("No transaction is active on this connection"),
            NullLoggerStub(),
            "テスト操作");

        act.Should().NotThrow("二次例外は本来の失敗要因を置き換えて上位の分岐を外す");
    }

    /// <summary>
    /// ロールバックの失敗は Warning で痕跡を残すこと
    /// </summary>
    /// <remarks>
    /// <c>LogDebug</c> は <c>appsettings.json</c> の <c>Logging:LogLevel:Default = "Information"</c> により
    /// 本番のファイルへ出力されない（development-conventions.md #1716 / #1730）。握りつぶす以上、
    /// 障害調査の手掛かりは Warning 以上で残す必要がある。
    /// </remarks>
    [Fact]
    public void TryRollback_ロールバック失敗をWarningで記録すること()
    {
        var loggerMock = new Mock<ILogger>();
        var rollbackException = new InvalidOperationException("No transaction is active");

        SafeRollback.TryRollback(() => throw rollbackException, loggerMock.Object, "返却の記録");

        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString().Contains("返却の記録")),
                rollbackException,
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once,
            "調査を先に進める値（操作名）を載せた Warning を 1 回だけ出すこと");
    }

    /// <summary>
    /// 成功時はログを出さないこと（後から「常に出す」実装へ緩めても通らないようにする）
    /// </summary>
    [Fact]
    public void TryRollback_成功時はログを出さないこと()
    {
        var loggerMock = new Mock<ILogger>();

        SafeRollback.TryRollback(() => { }, loggerMock.Object, "返却の記録");

        loggerMock.Verify(
            l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Never);
    }

    /// <summary>
    /// <c>ILogger</c> を持たない層（リポジトリ・マイグレーション）からも安全に呼べること
    /// </summary>
    /// <remarks>
    /// logger が null の場合は <c>ErrorDialogHelper.LogException</c>（既存のファイルログ機構）へ
    /// 委譲する。error-messages.md #1817「ILogger を持たない層では ErrorDialogHelper.LogException」。
    /// </remarks>
    [Fact]
    public void TryRollback_ロガーがnullでも例外を漏らさないこと()
    {
        Action act = () => SafeRollback.TryRollback(
            () => throw new InvalidOperationException("No transaction is active"),
            logger: null,
            "台帳の削除");

        act.Should().NotThrow();
    }

    [Fact]
    public void TryRollback_ロールバック処理がnullなら引数例外になること()
    {
        Action act = () => SafeRollback.TryRollback(null, NullLoggerStub(), "テスト操作");

        act.Should().Throw<ArgumentNullException>();
    }

    private static ILogger NullLoggerStub() => new Mock<ILogger>().Object;
}
