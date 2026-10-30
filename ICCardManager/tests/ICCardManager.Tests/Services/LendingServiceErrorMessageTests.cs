using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Common;
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
    public void GetUserFriendlyErrorMessage_SQLiteBusy_共有モードでは他のPCとの競合として案内すること()
    {
        var ex = new SQLiteException(SQLiteErrorCode.Busy, "database is locked");

        var message = LendingService.GetUserFriendlyErrorMessage(ex, "貸出", isSharedMode: true);

        message.Should().Contain("他のPC");
        message.Should().Contain("競合");
        message.Should().Contain("貸出");
        message.Should().NotContain("database is locked");
    }

    /// <summary>
    /// Issue #2328: ローカルモードには「他のPC」が存在しないため、原因として案内しないこと。
    /// </summary>
    /// <remarks>
    /// 単一 PC でも VACUUM・バックアップ・接続ヘルスチェックといった自プロセス内の別接続と競合して
    /// SQLITE_BUSY は起こり得る（db-write-conventions.md「エラー文言で原因を断定する前に…」）。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void GetUserFriendlyErrorMessage_SQLiteBusy_ローカルモードでは他のPCを原因として案内しないこと()
    {
        var ex = new SQLiteException(SQLiteErrorCode.Busy, "database is locked");

        var message = LendingService.GetUserFriendlyErrorMessage(ex, "貸出", isSharedMode: false);

        message.Should().NotContain("他のPC", "ローカルモードに他のPCは存在しない");
        message.Should().Contain("競合", "なぜ: 別の処理と競合したことは伝える");
        message.Should().Contain("貸出");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    public void GetUserFriendlyErrorMessage_SQLiteLocked_ロックメッセージを返すこと(bool isSharedMode)
    {
        var ex = new SQLiteException(SQLiteErrorCode.Locked, "database table is locked");

        var message = LendingService.GetUserFriendlyErrorMessage(ex, "返却", isSharedMode);

        message.Should().Contain("ロック");
        message.Should().Contain("返却");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetUserFriendlyErrorMessage_SQLiteIoErr_共有モードではネットワークの確認を案内すること()
    {
        var ex = new SQLiteException(SQLiteErrorCode.IoErr, "disk I/O error");

        var message = LendingService.GetUserFriendlyErrorMessage(ex, "貸出", isSharedMode: true);

        message.Should().Contain("ネットワーク");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetUserFriendlyErrorMessage_IOException_共有モードではネットワークの確認を案内すること()
    {
        var ex = new IOException("The specified network name is no longer available");

        var message = LendingService.GetUserFriendlyErrorMessage(ex, "返却", isSharedMode: true);

        message.Should().Contain("ネットワーク");
    }

    /// <summary>
    /// Issue #2328: ローカルモードの I/O の失敗を、存在しない共有フォルダ・ネットワークへ帰さないこと。
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    public void GetUserFriendlyErrorMessage_IO系_ローカルモードでは共有フォルダを原因として案内しないこと(bool sqliteIoErr)
    {
        Exception ex = sqliteIoErr
            ? new SQLiteException(SQLiteErrorCode.IoErr, "disk I/O error")
            : new IOException("disk error");

        var message = LendingService.GetUserFriendlyErrorMessage(ex, "返却", isSharedMode: false);

        message.Should().NotContain("共有フォルダ");
        message.Should().NotContain("ネットワーク");
        message.Should().Contain("返却");
    }

    /// <summary>
    /// Issue #2328: どの分岐も、やり直しは「職員証のタッチから」と案内すること（#2141）。
    /// </summary>
    /// <remarks>
    /// 失敗後は MainViewModel が職員証タッチ待ちへ戻すため、旧文言「しばらく待ってから再度○○をお試しください」の
    /// とおり交通系ICカードだけをタッチし直すと履歴表示になる。#2141 は既定分岐だけを直しており、
    /// Busy / Locked / IoErr / IOException とロック取得のタイムアウトが取り残されていた。
    /// 全分岐を走査して表明する（分岐ごとの個別テストだけだと、新しい分岐の追随漏れを検出できない）。
    /// </remarks>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    public void 貸出返却の失敗の文言_すべての分岐で職員証のタッチからやり直すよう案内すること(bool isSharedMode)
    {
        var exceptions = new Exception[]
        {
            new SQLiteException(SQLiteErrorCode.Busy, "database is locked"),
            new SQLiteException(SQLiteErrorCode.Locked, "database table is locked"),
            new SQLiteException(SQLiteErrorCode.IoErr, "disk I/O error"),
            new IOException("network error"),
            new InvalidOperationException("unexpected"),
        };

        var messages = exceptions
            .Select(ex => LendingService.GetUserFriendlyErrorMessage(ex, "貸出", isSharedMode))
            .Append(LendingService.BuildLockTimeoutMessage("貸出"))
            .ToList();

        foreach (var message in messages)
        {
            message.Should().EndWith(OperationRetryGuidance.RestartFromStaffCard,
                "失敗後は職員証タッチ待ちへ戻るので、やり直しは職員証のタッチから（#2141）");
            message.Should().NotContain("お試しください", "交通系ICカードだけの再タッチを促す旧文言へ戻っていないこと");
            message.Should().Contain("貸出", "何が: 失敗した操作を示す");
            message.Length.Should().BeLessThan(56, "トーストで末尾が切れないよう簡潔に保つ");
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildLockTimeoutMessage_他の処理が実行中であることと操作名を示すこと()
    {
        var message = LendingService.BuildLockTimeoutMessage("返却");

        message.Should().Contain("処理が実行中");
        message.Should().Contain("返却");
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

        var message = LendingService.GetUserFriendlyErrorMessage(ex, "貸出", isSharedMode: true);

        message.Should().NotContain("unexpected error",
            "生の ex.Message は英語・技術用語を含みうるため UI へ出さない（Issue #1614）");
        message.Should().Contain("貸出処理に失敗しました",
            "何が: 失敗した操作を職員の言葉で示す");
        message.Should().MatchRegex("してください。?$",
            "どうすれば: 行動指示で終わる");

        // #1817 のコードレビュー指摘: この戻り値はトーストへ渡るため、
        // ExceptionMessageFormatter.ToUserMessage のフル文言（58文字）を返してはならない。
        // Issue #2141: 失敗後は職員証タッチ待ちへ戻るため、交通系ICカードだけのタッチ（旧文言
        // 「もう一度タッチしてください」が促した操作）は履歴表示になる。職員証から始めるよう案内する
        message.Should().Contain("職員証のタッチからやり直してください",
            "error-messages.md はトーストで簡潔な行動指示を優先すると定めている。"
            + "失敗後は職員証タッチ待ちへ戻るので、やり直しは職員証のタッチからと案内する");
        message.Should().NotContain("もう一度タッチ",
            "交通系ICカードだけを再タッチさせる旧文言へ戻っていないこと（Issue #2141）");
        message.Should().NotContain("画面を最新の状態に更新",
            "カードをタッチした職員に実行できない行動指示を出さない（取れる行動が違う経路には専用の文言）");
        message.Length.Should().BeLessThan(40,
            "文字サイズ「大」以上でトーストの末尾が切れるため簡潔に保つ");
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

        var message = LendingService.GetUserFriendlyErrorMessage(ex, "返却", isSharedMode: true);

        message.Should().Be(ex.UserFriendlyMessage);
        message.Should().NotContain("raw detail");
    }
}
