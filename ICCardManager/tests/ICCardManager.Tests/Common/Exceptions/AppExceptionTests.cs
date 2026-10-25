using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common.Exceptions;
using Xunit;


namespace ICCardManager.Tests.Common.Exceptions;

/// <summary>
/// カスタム例外クラスの単体テスト
/// </summary>
public class AppExceptionTests
{
    #region CardReaderException Tests

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
    public void CardReaderException_CardRemoved_ReturnsCorrectInfo()
    {
        // Act
        var exception = CardReaderException.CardRemoved();

        // Assert
        exception.UserFriendlyMessage.Should().Contain("カードが離されました");
        exception.ErrorCode.Should().Be("CR009");
    }

    [Fact]
    public void CardReaderException_CardRemoved_WithInnerException_PreservesInnerException()
    {
        // Arrange
        var innerException = new IOException("Card removed");

        // Act
        var exception = CardReaderException.CardRemoved(innerException);

        // Assert
        exception.InnerException.Should().BeSameAs(innerException);
        exception.ErrorCode.Should().Be("CR009");
    }

    #endregion

    #region DatabaseException Tests

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

    #endregion

    #region Exception Inheritance Tests

    [Fact]
    public void AllCustomExceptions_InheritFromAppException()
    {
        // Arrange & Act
        // Issue #2330: 本番で送出されるファクトリで確かめる（未使用のファクトリは削除した）
        var cardReaderException = CardReaderException.ReadFailed();
        var databaseException = DatabaseException.QueryFailed();
        var fileOperationException = FileOperationException.UndecidableEncoding("/path/file.csv");
        var businessException = BusinessException.LentStatusUpdateConflict("CARD123", "貸出");

        // Assert
        cardReaderException.Should().BeAssignableTo<AppException>();
        databaseException.Should().BeAssignableTo<AppException>();
        fileOperationException.Should().BeAssignableTo<AppException>();
        businessException.Should().BeAssignableTo<AppException>();
    }

    /// <summary>
    /// <see cref="Exception"/> として投げて捕捉しても、整備済みの文言とエラーコードが
    /// 失われないこと。
    /// </summary>
    /// <remarks>
    /// Issue #2106: 旧名 <c>AllCustomExceptions_AreSerializableAsException</c> は「シリアライズ可能」を
    /// 名乗りながら、<c>Exception[]</c> の要素が <c>Exception</c> へ代入可能であること（コンパイルが保証する
    /// 自明な性質）しか表明していなかった。本番の例外クラスはいずれも <c>[Serializable]</c> も
    /// シリアライズ用コンストラクタも持たず、往復を保証していない（WPF の単一プロセスアプリで
    /// AppDomain 越し・リモーティングを使わないため）。保証していない性質は表明しない。
    /// 代わりに、上位の汎用ハンドラー（<c>catch (Exception)</c> → <see cref="ICCardManager.Common.ExceptionMessageFormatter"/> /
    /// <c>ErrorDialogHelper.GetErrorInfo</c>）が頼っている「基底型で受けても文言とコードを取り出せる」ことを、
    /// 例外ごとの具体値で表明する。
    /// </remarks>
    [Fact]
    public void AllCustomExceptions_汎用ハンドラーのToUserMessageは基底型で受けても各例外の整備済み文言を使うこと()
    {
        var cases = new (Func<Exception> Create, string ErrorCode, string UserFriendlyMessage)[]
        {
            // Issue #2330: 本番で送出されるファクトリで確かめる（未使用のファクトリは削除した）
            (() => CardReaderException.ReadFailed(), "CR002",
                "カードの読み取りに失敗しました。カードをリーダーに置き直してください。"),
            (() => DatabaseException.QueryFailed(), "DB002",
                "データの操作中にエラーが発生しました。再度お試しください。"),
            (() => BusinessException.LentStatusUpdateConflict("CARD123", "貸出"), "BIZ015",
                "貸出を記録できませんでした。交通系ICカード管理（F3）で削除されていないか確認してください。"),
        };

        foreach (var (create, errorCode, userFriendlyMessage) in cases)
        {
            Exception? caught = null;
            try
            {
                throw create();
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            // 汎用ハンドラーと同じく、静的な型が Exception のまま渡す
            ICCardManager.Common.ExceptionMessageFormatter.ToUserMessage(caught!, "処理")
                .Should().Be(userFriendlyMessage, "汎用ハンドラーは基底型で受けても整備済みの文言を使う");
            caught.Should().BeAssignableTo<AppException>().Which.ErrorCode.Should().Be(errorCode);
        }
    }

    #endregion

    // エラーコードの重複・分類（接頭辞）の検査は ErrorCodeUniquenessConventionTests が本番ソースの走査で担う。
    // ファクトリの手書き一覧（旧 AllErrorCodes_AreUniqueWithinCategory）は DB009 を載せ忘れており、
    // ファクトリを足すたびに一覧へ足す運用が守られていなかった（Issue #2101）。
}
