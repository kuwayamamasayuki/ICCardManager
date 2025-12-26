using System.IO;
using FluentAssertions;
using ICCardManager.Common.Exceptions;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Common.Exceptions;

/// <summary>
/// カスタム例外クラスの単体テスト
/// </summary>
public class AppExceptionTests
{
    #region CardReaderException Tests

    [Fact]
    public void CardReaderException_NotConnected_ReturnsCorrectInfo()
    {
        // Act
        var exception = CardReaderException.NotConnected();

        // Assert
        exception.UserFriendlyMessage.Should().Be("カードリーダーが接続されていません。接続を確認してください。");
        exception.ErrorCode.Should().Be("CR001");
        exception.Message.Should().Contain("not connected");
    }

    [Fact]
    public void CardReaderException_NotConnected_WithInnerException_PreservesInnerException()
    {
        // Arrange
        var innerException = new InvalidOperationException("Test inner exception");

        // Act
        var exception = CardReaderException.NotConnected(innerException);

        // Assert
        exception.InnerException.Should().BeSameAs(innerException);
        exception.ErrorCode.Should().Be("CR001");
    }

    [Fact]
    public void CardReaderException_ReadFailed_ReturnsCorrectInfo()
    {
        // Act
        var exception = CardReaderException.ReadFailed("Test detail");

        // Assert
        exception.UserFriendlyMessage.Should().Be("カードの読み取りに失敗しました。カードをリーダーに置き直してください。");
        exception.ErrorCode.Should().Be("CR002");
        exception.Message.Should().Contain("Test detail");
    }

    [Fact]
    public void CardReaderException_HistoryReadFailed_ReturnsCorrectInfo()
    {
        // Act
        var exception = CardReaderException.HistoryReadFailed();

        // Assert
        exception.UserFriendlyMessage.Should().Be("カードの利用履歴を読み取れませんでした。");
        exception.ErrorCode.Should().Be("CR003");
    }

    [Fact]
    public void CardReaderException_BalanceReadFailed_ReturnsCorrectInfo()
    {
        // Act
        var exception = CardReaderException.BalanceReadFailed();

        // Assert
        exception.UserFriendlyMessage.Should().Be("カードの残高を読み取れませんでした。");
        exception.ErrorCode.Should().Be("CR004");
    }

    [Fact]
    public void CardReaderException_Timeout_ReturnsCorrectInfo()
    {
        // Act
        var exception = CardReaderException.Timeout();

        // Assert
        exception.UserFriendlyMessage.Should().Be("カードリーダーとの通信がタイムアウトしました。再度お試しください。");
        exception.ErrorCode.Should().Be("CR005");
    }

    #endregion

    #region DatabaseException Tests

    [Fact]
    public void DatabaseException_ConnectionFailed_ReturnsCorrectInfo()
    {
        // Act
        var exception = DatabaseException.ConnectionFailed();

        // Assert
        exception.UserFriendlyMessage.Should().Be("データベースへの接続に失敗しました。管理者に連絡してください。");
        exception.ErrorCode.Should().Be("DB001");
    }

    [Fact]
    public void DatabaseException_QueryFailed_WithOperation_IncludesOperationInMessage()
    {
        // Act
        var exception = DatabaseException.QueryFailed("INSERT staff");

        // Assert
        exception.Message.Should().Contain("INSERT staff");
        exception.ErrorCode.Should().Be("DB002");
        exception.UserFriendlyMessage.Should().Contain("エラーが発生しました");
    }

    [Fact]
    public void DatabaseException_NotFound_ReturnsCorrectEntityName()
    {
        // Act - Staff
        var staffException = DatabaseException.NotFound("Staff", "12345");

        // Assert
        staffException.UserFriendlyMessage.Should().Contain("職員");
        staffException.ErrorCode.Should().Be("DB003");

        // Act - Card
        var cardException = DatabaseException.NotFound("IcCard", "ABCDEF");

        // Assert
        cardException.UserFriendlyMessage.Should().Contain("ICカード");
    }

    [Fact]
    public void DatabaseException_DuplicateEntry_ReturnsCorrectInfo()
    {
        // Act
        var exception = DatabaseException.DuplicateEntry("Staff", "12345");

        // Assert
        exception.UserFriendlyMessage.Should().Contain("既に登録されています");
        exception.ErrorCode.Should().Be("DB004");
    }

    [Fact]
    public void DatabaseException_ForeignKeyViolation_ReturnsCorrectInfo()
    {
        // Act
        var exception = DatabaseException.ForeignKeyViolation();

        // Assert
        exception.UserFriendlyMessage.Should().Contain("関連するデータ");
        exception.ErrorCode.Should().Be("DB005");
    }

    [Fact]
    public void DatabaseException_TransactionFailed_ReturnsCorrectInfo()
    {
        // Act
        var exception = DatabaseException.TransactionFailed();

        // Assert
        exception.UserFriendlyMessage.Should().Contain("更新処理に失敗");
        exception.ErrorCode.Should().Be("DB006");
    }

    [Fact]
    public void DatabaseException_FileAccessDenied_ReturnsCorrectInfo()
    {
        // Act
        var exception = DatabaseException.FileAccessDenied("/path/to/db");

        // Assert
        exception.UserFriendlyMessage.Should().Contain("アクセス権限");
        exception.ErrorCode.Should().Be("DB007");
        exception.Message.Should().Contain("/path/to/db");
    }

    #endregion

    #region ValidationException Tests

    [Fact]
    public void ValidationException_Required_ReturnsCorrectInfo()
    {
        // Act
        var exception = ValidationException.Required("name", "氏名");

        // Assert
        exception.UserFriendlyMessage.Should().Be("氏名を入力してください。");
        exception.ErrorCode.Should().Be("VAL001");
        exception.ValidationErrors.Should().ContainKey("name");
    }

    [Fact]
    public void ValidationException_OutOfRange_ReturnsCorrectInfo()
    {
        // Act
        var exception = ValidationException.OutOfRange("warningBalance", "残高警告閾値", 1000, 100000);

        // Assert
        exception.UserFriendlyMessage.Should().Contain("1000").And.Contain("100000");
        exception.ErrorCode.Should().Be("VAL002");
    }

    [Fact]
    public void ValidationException_InvalidFormat_ReturnsCorrectInfo()
    {
        // Act
        var exception = ValidationException.InvalidFormat("email", "メールアドレス", "example@domain.com");

        // Assert
        exception.UserFriendlyMessage.Should().Contain("形式");
        exception.ErrorCode.Should().Be("VAL003");
    }

    [Fact]
    public void ValidationException_InvalidIdm_ReturnsCorrectInfo()
    {
        // Act
        var exception = ValidationException.InvalidIdm("cardIdm");

        // Assert
        exception.UserFriendlyMessage.Should().Contain("16桁");
        exception.ErrorCode.Should().Be("VAL004");
    }

    [Fact]
    public void ValidationException_TooLong_ReturnsCorrectInfo()
    {
        // Act
        var exception = ValidationException.TooLong("note", "備考", 100);

        // Assert
        exception.UserFriendlyMessage.Should().Contain("100文字以内");
        exception.ErrorCode.Should().Be("VAL005");
    }

    [Fact]
    public void ValidationException_Multiple_ReturnsAllErrors()
    {
        // Arrange
        var errors = new Dictionary<string, string>
        {
            { "name", "氏名を入力してください。" },
            { "email", "メールアドレスの形式が正しくありません。" }
        };

        // Act
        var exception = ValidationException.Multiple(errors);

        // Assert
        exception.ValidationErrors.Should().HaveCount(2);
        exception.ValidationErrors.Should().ContainKey("name");
        exception.ValidationErrors.Should().ContainKey("email");
        exception.ErrorCode.Should().Be("VAL006");
    }

    [Fact]
    public void ValidationException_InvalidDate_ReturnsCorrectInfo()
    {
        // Act
        var exception = ValidationException.InvalidDate("startDate", "開始日");

        // Assert
        exception.UserFriendlyMessage.Should().Contain("日付形式");
        exception.ErrorCode.Should().Be("VAL007");
    }

    #endregion

    #region BusinessException Tests

    [Fact]
    public void BusinessException_CardAlreadyLent_ReturnsCorrectInfo()
    {
        // Act
        var exception = BusinessException.CardAlreadyLent("CARD123");

        // Assert
        exception.UserFriendlyMessage.Should().Be("このカードは既に貸出中です。");
        exception.ErrorCode.Should().Be("BIZ001");
    }

    [Fact]
    public void BusinessException_CardNotLent_ReturnsCorrectInfo()
    {
        // Act
        var exception = BusinessException.CardNotLent("CARD123");

        // Assert
        exception.UserFriendlyMessage.Should().Be("このカードは貸出されていません。");
        exception.ErrorCode.Should().Be("BIZ002");
    }

    [Fact]
    public void BusinessException_UnregisteredStaff_ReturnsCorrectInfo()
    {
        // Act
        var exception = BusinessException.UnregisteredStaff("STAFF123");

        // Assert
        exception.UserFriendlyMessage.Should().Contain("登録されていません");
        exception.ErrorCode.Should().Be("BIZ003");
    }

    [Fact]
    public void BusinessException_UnregisteredCard_ReturnsCorrectInfo()
    {
        // Act
        var exception = BusinessException.UnregisteredCard("CARD123");

        // Assert
        exception.UserFriendlyMessage.Should().Contain("登録されていません");
        exception.ErrorCode.Should().Be("BIZ004");
    }

    [Fact]
    public void BusinessException_DeletedStaff_ReturnsCorrectInfo()
    {
        // Act
        var exception = BusinessException.DeletedStaff("STAFF123");

        // Assert
        exception.UserFriendlyMessage.Should().Contain("削除されています");
        exception.ErrorCode.Should().Be("BIZ005");
    }

    [Fact]
    public void BusinessException_DeletedCard_ReturnsCorrectInfo()
    {
        // Act
        var exception = BusinessException.DeletedCard("CARD123");

        // Assert
        exception.UserFriendlyMessage.Should().Contain("削除されています");
        exception.ErrorCode.Should().Be("BIZ006");
    }

    [Fact]
    public void BusinessException_LowBalance_ReturnsCorrectInfo()
    {
        // Act
        var exception = BusinessException.LowBalance("H-001", 5000, 10000);

        // Assert
        exception.UserFriendlyMessage.Should().Contain("10,000円").And.Contain("5,000円");
        exception.ErrorCode.Should().Be("BIZ007");
    }

    [Fact]
    public void BusinessException_OperationNotAllowed_ReturnsCorrectInfo()
    {
        // Act
        var exception = BusinessException.OperationNotAllowed("delete");

        // Assert
        exception.UserFriendlyMessage.Should().Contain("権限");
        exception.ErrorCode.Should().Be("BIZ008");
    }

    [Fact]
    public void BusinessException_OperationTimeout_ReturnsCorrectInfo()
    {
        // Act
        var exception = BusinessException.OperationTimeout();

        // Assert
        exception.UserFriendlyMessage.Should().Contain("タイムアウト");
        exception.ErrorCode.Should().Be("BIZ009");
    }

    [Fact]
    public void BusinessException_BackupPathNotConfigured_ReturnsCorrectInfo()
    {
        // Act
        var exception = BusinessException.BackupPathNotConfigured();

        // Assert
        exception.UserFriendlyMessage.Should().Contain("バックアップ先が設定されていません");
        exception.ErrorCode.Should().Be("BIZ010");
    }

    [Fact]
    public void BusinessException_BackupFailed_ReturnsCorrectInfo()
    {
        // Act
        var exception = BusinessException.BackupFailed();

        // Assert
        exception.UserFriendlyMessage.Should().Contain("バックアップに失敗");
        exception.ErrorCode.Should().Be("BIZ011");
    }

    [Fact]
    public void BusinessException_BackupFailed_WithInnerException_PreservesInnerException()
    {
        // Arrange
        var innerException = new IOException("Disk full");

        // Act
        var exception = BusinessException.BackupFailed(innerException);

        // Assert
        exception.InnerException.Should().BeSameAs(innerException);
    }

    [Fact]
    public void BusinessException_RestoreFailed_ReturnsCorrectInfo()
    {
        // Act
        var exception = BusinessException.RestoreFailed();

        // Assert
        exception.UserFriendlyMessage.Should().Contain("復元に失敗");
        exception.ErrorCode.Should().Be("BIZ012");
    }

    [Fact]
    public void BusinessException_ReportGenerationFailed_ReturnsCorrectInfo()
    {
        // Act
        var exception = BusinessException.ReportGenerationFailed();

        // Assert
        exception.UserFriendlyMessage.Should().Contain("帳票の生成に失敗");
        exception.ErrorCode.Should().Be("BIZ013");
    }

    [Fact]
    public void BusinessException_FileWriteAccessDenied_ReturnsCorrectInfo()
    {
        // Act
        var exception = BusinessException.FileWriteAccessDenied("/path/to/file");

        // Assert
        exception.UserFriendlyMessage.Should().Contain("書き込み権限");
        exception.ErrorCode.Should().Be("BIZ014");
        exception.Message.Should().Contain("/path/to/file");
    }

    #endregion

    #region Exception Inheritance Tests

    [Fact]
    public void AllCustomExceptions_InheritFromAppException()
    {
        // Arrange & Act
        var cardReaderException = CardReaderException.NotConnected();
        var databaseException = DatabaseException.ConnectionFailed();
        var validationException = ValidationException.Required("test", "テスト");
        var businessException = BusinessException.CardAlreadyLent("CARD123");

        // Assert
        cardReaderException.Should().BeAssignableTo<AppException>();
        databaseException.Should().BeAssignableTo<AppException>();
        validationException.Should().BeAssignableTo<AppException>();
        businessException.Should().BeAssignableTo<AppException>();
    }

    [Fact]
    public void AllCustomExceptions_AreSerializableAsException()
    {
        // Arrange & Act
        var exceptions = new Exception[]
        {
            CardReaderException.NotConnected(),
            DatabaseException.ConnectionFailed(),
            ValidationException.Required("test", "テスト"),
            BusinessException.CardAlreadyLent("CARD123")
        };

        // Assert - All should be assignable to Exception
        foreach (var exception in exceptions)
        {
            exception.Should().BeAssignableTo<Exception>();
        }
    }

    #endregion

    #region Error Code Uniqueness Tests

    [Fact]
    public void AllErrorCodes_AreUniqueWithinCategory()
    {
        // This test verifies that error codes follow the pattern and don't conflict
        var cardReaderCodes = new[]
        {
            CardReaderException.NotConnected().ErrorCode,
            CardReaderException.ReadFailed().ErrorCode,
            CardReaderException.HistoryReadFailed().ErrorCode,
            CardReaderException.BalanceReadFailed().ErrorCode,
            CardReaderException.Timeout().ErrorCode
        };

        cardReaderCodes.Should().OnlyHaveUniqueItems();
        cardReaderCodes.Should().AllSatisfy(code => code.Should().StartWith("CR"));

        var databaseCodes = new[]
        {
            DatabaseException.ConnectionFailed().ErrorCode,
            DatabaseException.QueryFailed().ErrorCode,
            DatabaseException.NotFound("test", "1").ErrorCode,
            DatabaseException.DuplicateEntry("test", "1").ErrorCode,
            DatabaseException.ForeignKeyViolation().ErrorCode,
            DatabaseException.TransactionFailed().ErrorCode,
            DatabaseException.FileAccessDenied().ErrorCode
        };

        databaseCodes.Should().OnlyHaveUniqueItems();
        databaseCodes.Should().AllSatisfy(code => code.Should().StartWith("DB"));

        var validationCodes = new[]
        {
            ValidationException.Required("f", "F").ErrorCode,
            ValidationException.OutOfRange("f", "F", 0, 1).ErrorCode,
            ValidationException.InvalidFormat("f", "F", "x").ErrorCode,
            ValidationException.InvalidIdm("f").ErrorCode,
            ValidationException.TooLong("f", "F", 1).ErrorCode,
            ValidationException.Multiple(new Dictionary<string, string>()).ErrorCode,
            ValidationException.InvalidDate("f", "F").ErrorCode
        };

        validationCodes.Should().OnlyHaveUniqueItems();
        validationCodes.Should().AllSatisfy(code => code.Should().StartWith("VAL"));
    }

    #endregion
}
