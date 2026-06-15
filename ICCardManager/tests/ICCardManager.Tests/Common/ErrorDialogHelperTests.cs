using System;
using System.IO;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// ErrorDialogHelperの単体テスト
/// GetErrorInfoメソッド（internal）の例外→エラー情報マッピングを検証
/// </summary>
public class ErrorDialogHelperTests
{
    #region GetErrorInfo - AppException系テスト

    [Fact]
    public void GetErrorInfo_CardReaderExceptionの場合UserFriendlyMessageとErrorCodeを返すこと()
    {
        // Arrange
        var exception = CardReaderException.NotConnected();

        // Act
        var (message, errorCode) = ErrorDialogHelper.GetErrorInfo(exception);

        // Assert
        message.Should().Be(exception.UserFriendlyMessage);
        errorCode.Should().Be("CR001");
    }

    [Fact]
    public void GetErrorInfo_DatabaseExceptionの場合UserFriendlyMessageとErrorCodeを返すこと()
    {
        // Arrange
        var exception = DatabaseException.ConnectionFailed();

        // Act
        var (message, errorCode) = ErrorDialogHelper.GetErrorInfo(exception);

        // Assert
        message.Should().Be(exception.UserFriendlyMessage);
        errorCode.Should().Be("DB001");
    }

    [Fact]
    public void GetErrorInfo_BusinessExceptionの場合UserFriendlyMessageとErrorCodeを返すこと()
    {
        // Arrange
        var exception = BusinessException.CardAlreadyLent("TEST_IDM");

        // Act
        var (message, errorCode) = ErrorDialogHelper.GetErrorInfo(exception);

        // Assert
        message.Should().Be(exception.UserFriendlyMessage);
        errorCode.Should().Be("BIZ001");
    }

    [Fact]
    public void GetErrorInfo_ValidationExceptionの場合UserFriendlyMessageとErrorCodeを返すこと()
    {
        // Arrange
        var exception = ValidationException.Required("name", "氏名");

        // Act
        var (message, errorCode) = ErrorDialogHelper.GetErrorInfo(exception);

        // Assert
        message.Should().Be(exception.UserFriendlyMessage);
        errorCode.Should().Be("VAL001");
    }

    [Fact]
    public void GetErrorInfo_FileOperationExceptionの場合UserFriendlyMessageとErrorCodeを返すこと()
    {
        // Arrange
        var exception = FileOperationException.FileNotFound("/test/path.txt");

        // Act
        var (message, errorCode) = ErrorDialogHelper.GetErrorInfo(exception);

        // Assert
        message.Should().Be(exception.UserFriendlyMessage);
        errorCode.Should().Be("FILE001");
    }

    #endregion

    #region GetErrorInfo - 一般例外系テスト

    [Fact]
    public void GetErrorInfo_UnauthorizedAccessExceptionの場合SYS001を返すこと()
    {
        // Arrange
        var exception = new UnauthorizedAccessException("access denied");

        // Act
        var (message, errorCode) = ErrorDialogHelper.GetErrorInfo(exception);

        // Assert
        message.Should().Contain("アクセス権限");
        errorCode.Should().Be("SYS001");
    }

    [Fact]
    public void GetErrorInfo_IOExceptionの場合SYS002を返すこと()
    {
        // Arrange
        var exception = new IOException("file read error");

        // Act
        var (message, errorCode) = ErrorDialogHelper.GetErrorInfo(exception);

        // Assert
        message.Should().Contain("読み書き");
        errorCode.Should().Be("SYS002");
    }

    [Fact]
    public void GetErrorInfo_TimeoutExceptionの場合SYS003を返すこと()
    {
        // Arrange
        var exception = new TimeoutException("operation timed out");

        // Act
        var (message, errorCode) = ErrorDialogHelper.GetErrorInfo(exception);

        // Assert
        message.Should().Contain("タイムアウト");
        errorCode.Should().Be("SYS003");
    }

    [Fact]
    public void GetErrorInfo_InvalidOperationExceptionの場合SYS004を返すこと()
    {
        // Arrange
        var exception = new InvalidOperationException("invalid state");

        // Act
        var (message, errorCode) = ErrorDialogHelper.GetErrorInfo(exception);

        // Assert
        message.Should().Contain("実行できません");
        errorCode.Should().Be("SYS004");
    }

    [Fact]
    public void GetErrorInfo_ArgumentExceptionの場合SYS005を返すこと()
    {
        // Arrange
        var exception = new ArgumentException("bad argument");

        // Act
        var (message, errorCode) = ErrorDialogHelper.GetErrorInfo(exception);

        // Assert
        message.Should().Contain("入力値");
        errorCode.Should().Be("SYS005");
    }

    [Fact]
    public void GetErrorInfo_ArgumentNullExceptionの場合SYS005を返すこと()
    {
        // ArgumentNullExceptionはArgumentExceptionのサブクラスだが、
        // switchのOR patternで同じSYS005にマッピングされることを確認
        // Arrange
        var exception = new ArgumentNullException("paramName");

        // Act
        var (message, errorCode) = ErrorDialogHelper.GetErrorInfo(exception);

        // Assert
        message.Should().Contain("入力値");
        errorCode.Should().Be("SYS005");
    }

    [Fact]
    public void GetErrorInfo_NotSupportedExceptionの場合SYS006を返すこと()
    {
        // Arrange
        var exception = new NotSupportedException("not supported");

        // Act
        var (message, errorCode) = ErrorDialogHelper.GetErrorInfo(exception);

        // Assert
        message.Should().Contain("サポートされていません");
        errorCode.Should().Be("SYS006");
    }

    [Fact]
    public void GetErrorInfo_未知の例外の場合SYS999を返すこと()
    {
        // Arrange - 一般的なExceptionはどのパターンにもマッチしない
        var exception = new Exception("unknown error");

        // Act
        var (message, errorCode) = ErrorDialogHelper.GetErrorInfo(exception);

        // Assert
        message.Should().Contain("予期しないエラー");
        errorCode.Should().Be("SYS999");
    }

    [Fact]
    public void GetErrorInfo_カスタム例外の場合SYS999を返すこと()
    {
        // Arrange - switchパターンに含まれないカスタム例外
        var exception = new OperationCanceledException("cancelled");

        // Act
        var (message, errorCode) = ErrorDialogHelper.GetErrorInfo(exception);

        // Assert
        errorCode.Should().Be("SYS999");
    }

    #endregion

    #region エラーコード一貫性テスト

    [Fact]
    public void GetErrorInfo_全てのシステムエラーコードが空でないこと()
    {
        // Arrange
        var exceptions = new Exception[]
        {
            new UnauthorizedAccessException(),
            new IOException(),
            new TimeoutException(),
            new InvalidOperationException(),
            new ArgumentException(),
            new ArgumentNullException(),
            new NotSupportedException(),
            new Exception(),
        };

        // Act & Assert
        foreach (var exception in exceptions)
        {
            var (message, errorCode) = ErrorDialogHelper.GetErrorInfo(exception);
            message.Should().NotBeNullOrEmpty();
            errorCode.Should().NotBeNullOrEmpty();
            errorCode.Should().StartWith("SYS");
        }
    }

    [Fact]
    public void GetErrorInfo_AppException系はサブクラスのErrorCodeをそのまま返すこと()
    {
        // Arrange - 各AppExceptionサブクラス
        var testCases = new AppException[]
        {
            CardReaderException.ReadFailed("detail"),
            DatabaseException.QueryFailed("SELECT"),
            ValidationException.TooLong("field", "フィールド", 50),
            BusinessException.OperationTimeout(),
            FileOperationException.WriteFailed("/path"),
        };

        // Act & Assert
        foreach (var appException in testCases)
        {
            var (message, errorCode) = ErrorDialogHelper.GetErrorInfo(appException);
            errorCode.Should().Be(appException.ErrorCode,
                because: $"{appException.GetType().Name}のErrorCodeがそのまま返されるべき");
            message.Should().Be(appException.UserFriendlyMessage,
                because: $"{appException.GetType().Name}のUserFriendlyMessageがそのまま返されるべき");
        }
    }

    #endregion

    #region LogException（Issue #1614）

    [Fact]
    public void LogException_nullを渡しても例外を投げないこと()
    {
        // ILogger 非保持の呼び出し元が安全に使えるよう、null は no-op であるべき。
        var act = () => ErrorDialogHelper.LogException(null, "テスト");

        act.Should().NotThrow();
    }

    [Fact]
    public void LogException_例外を渡しても呼び出し元へ再スローしないこと()
    {
        // ログ出力の失敗・成功にかかわらず、業務処理の catch 内から安全に呼べる契約。
        var act = () => ErrorDialogHelper.LogException(
            new InvalidOperationException("technical detail for log only"),
            "リストア");

        act.Should().NotThrow();
    }

    #endregion
}
