using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #1614: <see cref="ExceptionMessageFormatter"/> が、生の <c>ex.Message</c> を
/// ユーザーへ露出させず「何が／なぜ／どうすれば」3要素を満たすユーザー向け文言へ
/// 変換することを検証する。
/// </summary>
/// <remarks>
/// Issue #1275（<c>.claude/rules/error-messages.md</c>）の品質基準を、例外起因の
/// エラーメッセージにも適用する。品質基準の検証ロジックは
/// <c>ValidationServiceErrorMessageQualityTests</c> /
/// <c>PathValidatorErrorMessageQualityTests</c> と同じ
/// <see cref="AssertQualityCriteria"/> を踏襲する。
/// </remarks>
public class ExceptionMessageFormatterTests
{
    /// <summary>
    /// エラーメッセージの最小品質基準: 一定の長さがあり、句点を含み、
    /// 最後に「してください」相当の行動指示を持つ。
    /// </summary>
    private static void AssertQualityCriteria(string message)
    {
        message.Should().NotBeNullOrWhiteSpace("エラーメッセージは空であってはならない");
        message.Length.Should().BeGreaterThanOrEqualTo(20,
            "エラーメッセージは十分な説明を含むべき（最低20文字）");
        message.Should().Contain("。",
            "メッセージは句点で複数の要素を分離すべき");
        message.Should().MatchRegex(
            "してください。?$|入力してください。?$|選択してください。?$|設定してください。?$|連絡してください。?$",
            "メッセージは行動指示（～してください）で終わるべき");
    }

    public static TheoryData<Exception> CommonExceptions => new()
    {
        new UnauthorizedAccessException("Access to the path 'C:\\db' is denied."),
        new IOException("The process cannot access the file because it is being used by another process."),
        new TimeoutException("The operation has timed out."),
        new InvalidOperationException("Collection was modified."),
        new ArgumentException("Value does not fall within the expected range."),
        new ArgumentNullException("param"),
        new NotSupportedException("Specified method is not supported."),
        new Exception("Object reference not set to an instance of an object."),
    };

    [Theory]
    [MemberData(nameof(CommonExceptions))]
    public void ToUserMessage_AnyException_MeetsQualityCriteria(Exception exception)
    {
        var message = ExceptionMessageFormatter.ToUserMessage(exception, "台帳の保存");

        AssertQualityCriteria(message);
    }

    /// <summary>
    /// 例外型ごとの「なぜ」「どうすれば」をリテラルで固定する（Issue #2106）。
    /// </summary>
    /// <remarks>
    /// 上の品質基準（20 文字以上・行動指示で終わる）はどの分岐の文言も満たすため、
    /// 分岐どうしで理由と行動指示を入れ替えても、全部を default 分岐へ寄せても緑だった。
    /// 期待値は本体を呼ばずにリテラルで書く。
    /// </remarks>
    public static TheoryData<Exception, string> ExpectedMessagesByExceptionType => new()
    {
        {
            new System.Data.SQLite.SQLiteException(System.Data.SQLite.SQLiteErrorCode.Busy, "database is locked"),
            "台帳の保存に失敗しました。データベースの読み書きができませんでした。" +
            "ほかのパソコンや別の操作で同じデータを使用している可能性があります。しばらく待ってから再度実行してください。"
        },
        {
            new UnauthorizedAccessException("Access to the path 'C:\\db' is denied."),
            "台帳の保存に失敗しました。ファイルへのアクセス権限がありません。" +
            "保存先フォルダーの書き込み権限を確認するか、管理者に連絡してください。"
        },
        {
            new IOException("The process cannot access the file."),
            "台帳の保存に失敗しました。ファイルの読み書き中に問題が発生しました。" +
            "対象のファイルが他のプログラムで開かれていないか確認し、しばらく待ってから再度実行してください。"
        },
        {
            new TimeoutException("The operation has timed out."),
            "台帳の保存に失敗しました。処理に時間がかかり、中断されました。しばらく待ってから再度実行してください。"
        },
        {
            new InvalidOperationException("Collection was modified."),
            "台帳の保存に失敗しました。現在の状態ではこの操作を実行できません。" +
            "画面を最新の状態に更新してから再度実行してください。"
        },
        {
            new ArgumentException("Value does not fall within the expected range."),
            "台帳の保存に失敗しました。入力された値に問題があります。入力内容を確認してから再度実行してください。"
        },
        {
            // ArgumentNullException は ArgumentException の派生なので同じ分岐に入る
            new ArgumentNullException("param"),
            "台帳の保存に失敗しました。入力された値に問題があります。入力内容を確認してから再度実行してください。"
        },
        {
            new NotSupportedException("Specified method is not supported."),
            "台帳の保存に失敗しました。この操作は現在サポートされていません。" +
            "操作内容を確認し、必要であれば管理者に連絡してください。"
        },
        {
            new Exception("Object reference not set to an instance of an object."),
            "台帳の保存に失敗しました。予期しない問題が発生しました。" +
            "しばらく待ってから再度実行してください。解決しない場合は管理者に連絡してください。"
        },
    };

    [Theory]
    [MemberData(nameof(ExpectedMessagesByExceptionType))]
    public void ToUserMessage_例外型ごとに固有の理由と行動指示を返すこと(Exception exception, string expected)
    {
        ExceptionMessageFormatter.ToUserMessage(exception, "台帳の保存").Should().Be(expected);
    }

    /// <summary>
    /// 分岐ごとの文言が互いに異なること（Issue #2106）。
    /// 上のリテラル固定は期待値の側を書き換えれば通ってしまうため、
    /// 「どの分岐も default と同じ文言に寄せない」ことを独立に表明する。
    /// </summary>
    [Fact]
    public void ToUserMessage_分岐ごとの理由と文言が互いに異なること()
    {
        // 分岐ごとの代表（ArgumentNullException は ArgumentException と同じ分岐なので含めない）
        var representatives = new Exception[]
        {
            new System.Data.SQLite.SQLiteException(System.Data.SQLite.SQLiteErrorCode.Busy, "database is locked"),
            new UnauthorizedAccessException(),
            new IOException(),
            new TimeoutException(),
            new InvalidOperationException(),
            new ArgumentException(),
            new NotSupportedException(),
            new Exception(),
        };

        representatives.Select(e => ExceptionMessageFormatter.ToUserMessage(e, "台帳の保存"))
            .Should().OnlyHaveUniqueItems("例外型ごとに取れる行動が違うため、同じ文言へ畳まない");
        representatives.Select(ExceptionMessageFormatter.ToReason)
            .Should().OnlyHaveUniqueItems("「なぜ」も例外型ごとに異なる");
    }

    [Theory]
    [MemberData(nameof(CommonExceptions))]
    public void ToUserMessage_AnyException_DoesNotLeakRawExceptionMessage(Exception exception)
    {
        var message = ExceptionMessageFormatter.ToUserMessage(exception, "台帳の保存");

        // 生の例外メッセージ（英語・技術用語）がユーザー向け文言に混入していないこと。
        message.Should().NotContain(exception.Message,
            "技術的詳細はログにのみ記録し、UIへ出してはならない");
    }

    [Fact]
    public void ToUserMessage_IncludesOperationName_AsTheWhat()
    {
        var message = ExceptionMessageFormatter.ToUserMessage(
            new IOException("disk full"), "エクスポート");

        message.Should().StartWith("エクスポートに失敗しました",
            "「何が」: ユーザー視点の操作名で始まるべき");
    }

    [Fact]
    public void ToUserMessage_UnauthorizedAccess_ExplainsPermissionAndAction()
    {
        var message = ExceptionMessageFormatter.ToUserMessage(
            new UnauthorizedAccessException(), "リストア");

        AssertQualityCriteria(message);
        message.Should().Contain("権限", "なぜ: アクセス権限の問題であることを示す");
    }

    [Fact]
    public void ToUserMessage_AppException_UsesItsUserFriendlyMessage()
    {
        var appException = new TestAppException(
            technical: "SQLite error: database is locked",
            userFriendly: "データベースが使用中です。ほかのPCでの操作が完了するまで待ってから再度実行してください。",
            errorCode: "DB001");

        var message = ExceptionMessageFormatter.ToUserMessage(appException, "保存");

        message.Should().Be(appException.UserFriendlyMessage,
            "AppException は整備済みのユーザー向け文言を持つため、それを尊重する");
        message.Should().NotContain("SQLite", "技術的詳細は露出させない");
    }

    [Fact]
    public void ToUserMessage_NullException_ReturnsGenericQualityMessage()
    {
        var message = ExceptionMessageFormatter.ToUserMessage(null, "処理");

        AssertQualityCriteria(message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToUserMessage_BlankOperation_StillMeetsQuality(string operation)
    {
        var message = ExceptionMessageFormatter.ToUserMessage(
            new Exception("boom"), operation);

        AssertQualityCriteria(message);
    }

    // ---- ToReason（他の文へ埋め込むための「なぜ」だけ。Issue #1991） ----

    /// <summary>
    /// 埋め込み用は「何が」（〇〇に失敗しました）と「どうすれば」を含まないこと。
    /// 含むと、埋め込み先が既に述べている「何が」と矛盾し（「明細は置き換えました」の直後に
    /// 「明細の取り込みに失敗しました」）、両立しない行動指示が並ぶ。
    /// </summary>
    [Fact]
    public void ToReason_操作名と行動指示を含まないこと()
    {
        var reason = ExceptionMessageFormatter.ToReason(new InvalidOperationException("boom"));

        reason.Should().NotContain("に失敗しました");
        reason.Should().NotContain("してください");
        reason.Should().NotContain("boom", "生の ex.Message を出さない（#1614）");
    }

    /// <summary>
    /// 対の表明。空文字や無内容へ退化していないこと（「なぜ」は残る）。
    /// これが無いと、常に空文字を返す実装でも上のテストは緑になる。
    /// Issue #2106: 旧版は期待値を同じ <c>ToUserMessage</c> の出力から作っており、
    /// 両者が揃って変わる退行（理由の入れ替え・default への集約）を検出できなかった。期待値はリテラルで書く。
    /// </summary>
    [Fact]
    public void ToReason_理由そのものは残ること()
    {
        ExceptionMessageFormatter.ToReason(new InvalidOperationException("boom"))
            .Should().Be("現在の状態ではこの操作を実行できません。");

        ExceptionMessageFormatter.ToReason(new System.Data.SQLite.SQLiteException(
                System.Data.SQLite.SQLiteErrorCode.Busy, "database is locked"))
            .Should().Be("データベースの読み書きができませんでした。",
                "原因を名指しできる分岐は名指しすること（#1986）");
    }

    /// <summary>
    /// <see cref="AppException"/> は 3 要素が 1 文に畳まれており理由だけを取り出せないため、
    /// 整備済みの <c>UserFriendlyMessage</c> をそのまま返す（是正前から埋め込んでいた挙動と同じ）。
    /// </summary>
    [Fact]
    public void ToReason_AppExceptionは整備済み文言をそのまま返すこと()
    {
        var ex = new TestAppException("technical detail", "残額が不足しています。チャージしてください。", "TEST001");

        ExceptionMessageFormatter.ToReason(ex).Should().Be("残額が不足しています。チャージしてください。");
        ExceptionMessageFormatter.ToReason(ex).Should().NotContain("technical detail");
    }

    /// <summary>
    /// テスト用の <see cref="AppException"/> 具象クラス（基底は abstract のため）。
    /// </summary>
    private sealed class TestAppException : AppException
    {
        public TestAppException(string technical, string userFriendly, string errorCode)
            : base(technical, userFriendly, errorCode)
        {
        }
    }
}
