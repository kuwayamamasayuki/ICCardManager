using System;
using System.Data.SQLite;
using System.IO;
using FluentAssertions;
using ICCardManager.Common.Exceptions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1110: LendingServiceのエラーメッセージ変換テスト
/// SQLiteの技術的エラーをユーザー向けメッセージに変換する機能を検証する。
/// </summary>
public class LendingServiceErrorMessageTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void GetUserFriendlyErrorMessage_SQLiteBusy_競合メッセージを返すこと()
    {
        var ex = new SQLiteException(SQLiteErrorCode.Busy, "database is locked");

        var message = LendingService.GetUserFriendlyErrorMessage(ex, "貸出");

        message.Should().Contain("他のPC");
        message.Should().Contain("競合");
        message.Should().Contain("貸出");
        message.Should().NotContain("database is locked");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetUserFriendlyErrorMessage_SQLiteLocked_ロックメッセージを返すこと()
    {
        var ex = new SQLiteException(SQLiteErrorCode.Locked, "database table is locked");

        var message = LendingService.GetUserFriendlyErrorMessage(ex, "返却");

        message.Should().Contain("ロック");
        message.Should().Contain("返却");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetUserFriendlyErrorMessage_SQLiteIoErr_ネットワークメッセージを返すこと()
    {
        var ex = new SQLiteException(SQLiteErrorCode.IoErr, "disk I/O error");

        var message = LendingService.GetUserFriendlyErrorMessage(ex, "貸出");

        message.Should().Contain("ネットワーク");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetUserFriendlyErrorMessage_IOException_ネットワークメッセージを返すこと()
    {
        var ex = new IOException("The specified network name is no longer available");

        var message = LendingService.GetUserFriendlyErrorMessage(ex, "返却");

        message.Should().Contain("ネットワーク");
    }

    /// <summary>
    /// Issue #1817: 既定分岐（SQLite / IO 以外）で生の <c>ex.Message</c> を返さないこと。
    /// </summary>
    /// <remarks>
    /// 修正前は <c>$"{operationName}処理でエラーが発生しました: {ex.Message}"</c> を返しており、
    /// .NET／SQLite の英語文言がそのままトースト・ステータスへ出ていた（Issue #1614 違反）。
    /// 本テストは #1817 以前は「元メッセージを含むこと」として現挙動をピン留めしていたもので、
    /// 規約に合わせて<b>反転</b>させている。技術的詳細は呼び出し元
    /// （<c>LendAsync</c> / <c>ReturnAsync</c>）の <c>LogError</c> がログへ残す。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void GetUserFriendlyErrorMessage_その他の例外_生の例外メッセージを含まないこと()
    {
        var ex = new InvalidOperationException("unexpected error");

        var message = LendingService.GetUserFriendlyErrorMessage(ex, "貸出");

        message.Should().NotContain("unexpected error",
            "生の ex.Message は英語・技術用語を含みうるため UI へ出さない（Issue #1614）");
        message.Should().Contain("貸出処理に失敗しました",
            "何が: 失敗した操作を職員の言葉で示す");
        message.Should().MatchRegex("してください。?$",
            "どうすれば: 行動指示で終わる");
    }

    /// <summary>
    /// Issue #1817: <see cref="AppException"/> 派生は整備済みの
    /// <c>UserFriendlyMessage</c> がそのまま使われること（既定分岐へ落ちないこと）。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void GetUserFriendlyErrorMessage_AppException_整備済み文言を返すこと()
    {
        var ex = DatabaseException.QueryFailed("SELECT", new InvalidOperationException("raw detail"));

        var message = LendingService.GetUserFriendlyErrorMessage(ex, "返却");

        message.Should().Be(ex.UserFriendlyMessage);
        message.Should().NotContain("raw detail");
    }
}
