using FluentAssertions;
using ICCardManager.Common.Exceptions;
using Xunit;

using System;
using System.IO;
using System.Linq;


namespace ICCardManager.Tests.Common.Exceptions;

/// <summary>
/// BusinessExceptionの各ファクトリメソッドのテスト
/// </summary>
public class BusinessExceptionTests
{
    [Fact]
    public void CardAlreadyLent_正しいエラーコードとメッセージを持つこと()
    {
        var ex = BusinessException.CardAlreadyLent("0123456789ABCDEF");

        ex.ErrorCode.Should().Be("BIZ001");
        ex.UserFriendlyMessage.Should().Be("このカードは既に貸出中です。");
        ex.Message.Should().Contain("0123456789ABCDEF");
    }

    [Fact]
    public void CardNotLent_正しいエラーコードとメッセージを持つこと()
    {
        var ex = BusinessException.CardNotLent("0123456789ABCDEF");

        ex.ErrorCode.Should().Be("BIZ002");
        ex.UserFriendlyMessage.Should().Contain("貸出されていません");
    }

    [Fact]
    public void UnregisteredStaff_正しいエラーコードとメッセージを持つこと()
    {
        var ex = BusinessException.UnregisteredStaff("ABCDEF0123456789");

        ex.ErrorCode.Should().Be("BIZ003");
        ex.UserFriendlyMessage.Should().Contain("登録されていません");
        ex.UserFriendlyMessage.Should().Contain("職員証");
    }

    [Fact]
    public void UnregisteredCard_正しいエラーコードとメッセージを持つこと()
    {
        var ex = BusinessException.UnregisteredCard("ABCDEF0123456789");

        ex.ErrorCode.Should().Be("BIZ004");
        ex.UserFriendlyMessage.Should().Contain("登録されていません");
        ex.UserFriendlyMessage.Should().Contain("カード");
    }

    [Fact]
    public void DeletedStaff_正しいエラーコードとメッセージを持つこと()
    {
        var ex = BusinessException.DeletedStaff("ABCDEF0123456789");

        ex.ErrorCode.Should().Be("BIZ005");
        ex.UserFriendlyMessage.Should().Contain("削除されています");
    }

    [Fact]
    public void DeletedCard_正しいエラーコードとメッセージを持つこと()
    {
        var ex = BusinessException.DeletedCard("ABCDEF0123456789");

        ex.ErrorCode.Should().Be("BIZ006");
        ex.UserFriendlyMessage.Should().Contain("削除されています");
    }

    [Fact]
    public void LowBalance_残高と閾値がメッセージに含まれること()
    {
        var ex = BusinessException.LowBalance("001", 500, 1000);

        ex.ErrorCode.Should().Be("BIZ007");
        ex.UserFriendlyMessage.Should().Contain("1,000円");
        ex.UserFriendlyMessage.Should().Contain("500円");
    }

    [Fact]
    public void OperationNotAllowed_正しいエラーコードを持つこと()
    {
        var ex = BusinessException.OperationNotAllowed("カード削除");

        ex.ErrorCode.Should().Be("BIZ008");
        ex.UserFriendlyMessage.Should().Contain("権限");
        ex.Message.Should().Contain("カード削除");
    }

    [Fact]
    public void OperationTimeout_正しいエラーコードとメッセージを持つこと()
    {
        var ex = BusinessException.OperationTimeout();

        ex.ErrorCode.Should().Be("BIZ009");
        ex.UserFriendlyMessage.Should().Contain("タイムアウト");
    }

    [Fact]
    public void BackupPathNotConfigured_正しいエラーコードを持つこと()
    {
        var ex = BusinessException.BackupPathNotConfigured();

        ex.ErrorCode.Should().Be("BIZ010");
        ex.UserFriendlyMessage.Should().Contain("バックアップ先");
    }

    [Fact]
    public void BackupFailed_InnerExceptionなしで正しいエラーコードを持つこと()
    {
        var ex = BusinessException.BackupFailed();

        ex.ErrorCode.Should().Be("BIZ011");
        ex.InnerException.Should().BeNull();
    }

    [Fact]
    public void BackupFailed_InnerExceptionありで内部例外が保持されること()
    {
        var inner = new IOException("disk full");
        var ex = BusinessException.BackupFailed(inner);

        ex.ErrorCode.Should().Be("BIZ011");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void RestoreFailed_InnerExceptionなしで正しいエラーコードを持つこと()
    {
        var ex = BusinessException.RestoreFailed();

        ex.ErrorCode.Should().Be("BIZ012");
        ex.UserFriendlyMessage.Should().Contain("復元");
    }

    [Fact]
    public void RestoreFailed_InnerExceptionありで内部例外が保持されること()
    {
        var inner = new InvalidOperationException("corrupt");
        var ex = BusinessException.RestoreFailed(inner);

        ex.ErrorCode.Should().Be("BIZ012");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void ReportGenerationFailed_正しいエラーコードを持つこと()
    {
        var ex = BusinessException.ReportGenerationFailed();

        ex.ErrorCode.Should().Be("BIZ013");
        ex.UserFriendlyMessage.Should().Contain("帳票");
    }

    [Fact]
    public void ReportGenerationFailed_InnerExceptionが保持されること()
    {
        var inner = new FileNotFoundException("template not found");
        var ex = BusinessException.ReportGenerationFailed(inner);

        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void FileWriteAccessDenied_パスなしで正しいエラーコードを持つこと()
    {
        var ex = BusinessException.FileWriteAccessDenied();

        ex.ErrorCode.Should().Be("BIZ014");
        ex.UserFriendlyMessage.Should().Contain("書き込み権限");
    }

    [Fact]
    public void FileWriteAccessDenied_パスありでメッセージにパスが含まれること()
    {
        var ex = BusinessException.FileWriteAccessDenied(@"C:\test\file.xlsx");

        ex.ErrorCode.Should().Be("BIZ014");
        ex.Message.Should().Contain(@"C:\test\file.xlsx");
    }

    [Fact]
    public void 全ファクトリメソッドがAppExceptionを継承していること()
    {
        // すべてのBusinessExceptionがAppExceptionであること
        var exceptions = new AppException[]
        {
            BusinessException.CardAlreadyLent("idm"),
            BusinessException.CardNotLent("idm"),
            BusinessException.UnregisteredStaff("idm"),
            BusinessException.UnregisteredCard("idm"),
            BusinessException.DeletedStaff("idm"),
            BusinessException.DeletedCard("idm"),
            BusinessException.LowBalance("001", 100, 1000),
            BusinessException.OperationNotAllowed("op"),
            BusinessException.OperationTimeout(),
            BusinessException.BackupPathNotConfigured(),
            BusinessException.BackupFailed(),
            BusinessException.RestoreFailed(),
            BusinessException.ReportGenerationFailed(),
            BusinessException.FileWriteAccessDenied(),
        };

        foreach (var ex in exceptions)
        {
            ex.Should().BeAssignableTo<AppException>();
            ex.ErrorCode.Should().NotBeNullOrEmpty();
            ex.UserFriendlyMessage.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void エラーコードが全てユニークであること()
    {
        var exceptions = new AppException[]
        {
            BusinessException.CardAlreadyLent("idm"),
            BusinessException.CardNotLent("idm"),
            BusinessException.UnregisteredStaff("idm"),
            BusinessException.UnregisteredCard("idm"),
            BusinessException.DeletedStaff("idm"),
            BusinessException.DeletedCard("idm"),
            BusinessException.LowBalance("001", 100, 1000),
            BusinessException.OperationNotAllowed("op"),
            BusinessException.OperationTimeout(),
            BusinessException.BackupPathNotConfigured(),
            BusinessException.BackupFailed(),
            BusinessException.RestoreFailed(),
            BusinessException.ReportGenerationFailed(),
            BusinessException.FileWriteAccessDenied(),
        };

        var errorCodes = exceptions.Select(e => e.ErrorCode).ToArray();
        errorCodes.Should().OnlyHaveUniqueItems();
    }
}
