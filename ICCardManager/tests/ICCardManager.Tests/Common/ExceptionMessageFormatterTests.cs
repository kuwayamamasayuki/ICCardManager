using System;
using System.IO;
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
