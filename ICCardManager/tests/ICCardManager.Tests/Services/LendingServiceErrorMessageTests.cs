using System;
using System.Data.SQLite;
using System.IO;
using FluentAssertions;
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

    [Fact]
    [Trait("Category", "Unit")]
    public void GetUserFriendlyErrorMessage_その他の例外_元メッセージを含むこと()
    {
        var ex = new InvalidOperationException("unexpected error");

        var message = LendingService.GetUserFriendlyErrorMessage(ex, "貸出");

        message.Should().Contain("貸出処理でエラーが発生しました");
        message.Should().Contain("unexpected error");
    }
}
