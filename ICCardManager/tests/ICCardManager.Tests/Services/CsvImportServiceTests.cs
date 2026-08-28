using System.IO;
using System.Text;
using FluentAssertions;
using ICCardManager.Common.Exceptions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;
using System.Data.SQLite;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Services;

/// <summary>
/// CsvImportServiceの単体テスト
/// </summary>
public class CsvImportServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<IValidationService> _validationServiceMock;
    private readonly Mock<DbContext> _dbContextMock;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly SQLiteConnection _connection;
    private readonly DbContext _realDbContext;
    private readonly CsvImportService _service;

    // UTF-8 with BOM (Excel対応)
    private static readonly Encoding CsvEncoding = new UTF8Encoding(true);

    public CsvImportServiceTests()
    {
        // テスト用の一時ディレクトリを作成
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CsvImportServiceTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        // リポジトリ等をモック
        _cardRepositoryMock = new Mock<ICardRepository>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _validationServiceMock = new Mock<IValidationService>();
        _dbContextMock = new Mock<DbContext>();
        _cacheServiceMock = new Mock<ICacheService>();

        // デフォルトのバリデーション設定（すべて有効）
        _validationServiceMock.Setup(x => x.ValidateCardIdm(It.IsAny<string>()))
            .Returns(ValidationResult.Success());
        _validationServiceMock.Setup(x => x.ValidateStaffIdm(It.IsAny<string>()))
            .Returns(ValidationResult.Success());

        // トランザクションのモック
        // セマフォを保持しないConnectionLease/TransactionScopeを使用
        // （テスト内でLeaseConnectionAsyncが呼ばれてもデッドロックしないように）
        var connectionString = "Data Source=:memory:";
        _connection = new SQLiteConnection(connectionString);
        _connection.Open();
        _realDbContext = new DbContext(":memory:");
        var noOpLease = new ConnectionLease(_connection, () => { });
        var noOpTransaction = _connection.BeginTransaction();
        var transactionScope = new ICCardManager.Data.TransactionScope(noOpLease, noOpTransaction);
        _dbContextMock.Setup(x => x.BeginTransactionAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(transactionScope);

        _service = new CsvImportService(
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _validationServiceMock.Object,
            _dbContextMock.Object,
            _cacheServiceMock.Object);
    }

    public void Dispose()
    {
        // SQLite接続を閉じる
        _connection?.Dispose();
        _realDbContext?.Dispose();

        // テスト用ディレクトリを削除
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // クリーンアップ失敗は無視
        }

        GC.SuppressFinalize(this);
    }

    #region ImportCardsAsync テスト

    /// <summary>
    /// カードのインポートが正常に動作することを確認
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_WithValidData_ImportsSuccessfully()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,テスト1
FEDCBA9876543210,PASMO,002,テスト2";

        var filePath = Path.Combine(_testDirectory, "cards_import.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        // Act
        var result = await _service.ImportCardsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(2);
        result.ErrorCount.Should().Be(0);
    }

    /// <summary>
    /// 削除済みカードが復元（RestoreAsync→UpdateAsync）されることを確認。
    /// プレビューだけでなく実際のインポートでも復元フローが動作するべき。
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_DeletedCard_RestoresAndUpdates()
    {
        // Arrange — 削除済みカードが既存
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,復元したい";

        var filePath = Path.Combine(_testDirectory, "cards_restore.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var deletedCard = new IcCard
        {
            CardIdm = "0123456789ABCDEF",
            CardType = "Suica",
            CardNumber = "001",
            IsDeleted = true
        };
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(deletedCard);
        _cardRepositoryMock.Setup(x => x.RestoreAsync("0123456789ABCDEF", It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _cardRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        // Act — skipExisting=true でも削除済みは復元されること
        var result = await _service.ImportCardsAsync(filePath, skipExisting: true);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(1, "削除済みカードは復元+更新されてカウントに含まれる");
        result.SkippedCount.Should().Be(0, "削除済みはスキップされない");
        _cardRepositoryMock.Verify(x => x.RestoreAsync("0123456789ABCDEF", It.IsAny<SQLiteTransaction>()), Times.Once,
            "RestoreAsyncが呼ばれる");
        _cardRepositoryMock.Verify(x => x.UpdateAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()), Times.Once,
            "Restore後にUpdateAsyncが呼ばれる");
    }

    /// <summary>
    /// Issue #1757: カードCSVインポートで管理番号が重複したとき、行番号付きの
    /// 分かりやすいエラーとして報告されること（登録・更新の両経路）
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CardRepository</c> は UNIQUE 制約違反を <see cref="DuplicateCardNumberException"/> へ
    /// 変換する（登録は Issue #1106、更新・復元は Issue #1757 で追加）。取り込みループで
    /// 捕捉しないと <c>ExecuteImportWithErrorHandlingAsync</c> まで抜けて<b>結果全体のエラー</b>になり、
    /// <b>何行目が悪いのか分からない</b>（他のインポートエラーは行番号付き）。
    /// 文言自体は本例外が <see cref="ICCardManager.Common.Exceptions.AppException"/> を継承する
    /// （Issue #1757）ため <c>ToUserFacingErrorMessage</c> の <c>AppException</c> 分岐へ倒れるが、
    /// <b>行番号は失われる</b>。この 2 段構えのうち行番号の側を本テストが固定する。
    /// </para>
    /// <para>
    /// 重複は CSV の内容に起因する<b>復旧可能な入力ミス</b>なので、他のバリデーション
    /// エラーと同じ形（行番号 ＋ 行動指示付きの文言）で報告する。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]  // 既存カードの更新経路（Issue #1757 で新たに例外化された経路）
    [InlineData(false)] // 新規カードの登録経路（Issue #1106 から例外化されていた経路）
    public async Task ImportCardsAsync_DuplicateCardNumber_ReportsLineNumberedError(bool isUpdate)
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,nimoca,N-001,重複する番号";

        var filePath = Path.Combine(_testDirectory, $"cards_duplicate_{isUpdate}.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var duplicate = new DuplicateCardNumberException(
            "nimoca", "N-001", new InvalidOperationException("UNIQUE constraint failed"));

        if (isUpdate)
        {
            _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(new IcCard
            {
                CardIdm = "0123456789ABCDEF",
                CardType = "nimoca",
                CardNumber = "N-999"
            });
            _cardRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()))
                .ThrowsAsync(duplicate);
        }
        else
        {
            _cardRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), true)).ReturnsAsync((IcCard?)null);
            _cardRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()))
                .ThrowsAsync(duplicate);
        }

        // Act — 例外が外へ漏れず、結果オブジェクトとして報告されること
        var act = async () => await _service.ImportCardsAsync(filePath, skipExisting: false);
        var result = await act.Should().NotThrowAsync();

        // Assert
        result.Which.Success.Should().BeFalse();
        result.Which.ImportedCount.Should().Be(0, "重複行があるためロールバックされる");
        result.Which.Errors.Should().ContainSingle();

        var error = result.Which.Errors[0];
        error.LineNumber.Should().Be(2, "CSV の2行目（ヘッダーの次）が重複している");
        error.Message.Should().Contain("N-001");
        error.Message.Should().Contain("既に使用されています");
        error.Message.Should().EndWith("別の番号を指定してください。");
        error.Message.Should().NotContain("予期しないエラー");
    }

    /// <summary>
    /// 削除済みカードのRestoreAsyncが失敗した場合、エラー件数がカウントされロールバックされること
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_DeletedCard_RestoreFailure_RollsBack()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,復元失敗";

        var filePath = Path.Combine(_testDirectory, "cards_restore_fail.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var deletedCard = new IcCard { CardIdm = "0123456789ABCDEF", IsDeleted = true };
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(deletedCard);
        _cardRepositoryMock.Setup(x => x.RestoreAsync("0123456789ABCDEF", It.IsAny<SQLiteTransaction>())).ReturnsAsync(false);

        // Act
        var result = await _service.ImportCardsAsync(filePath);

        // Assert
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0, "失敗時はロールバックされる");
        result.ErrorCount.Should().BeGreaterThan(0);
        // 復元失敗時はUpdateAsyncは呼ばれない
        _cardRepositoryMock.Verify(x => x.UpdateAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()), Times.Never);
    }

    /// <summary>
    /// 既存カードが全項目一致のときスキップされることを確認（Issue #1376 で仕様明確化）
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_ExistingCard_Skipped()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,テスト";

        var filePath = Path.Combine(_testDirectory, "cards_existing.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // Issue #1376: 全項目一致の場合のみスキップされる仕様のため、
        // 既存レコードにも CSV と同じ備考を設定して完全一致にする
        var existingCard = new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001", Note = "テスト" };
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(existingCard);

        // Act
        var result = await _service.ImportCardsAsync(filePath, skipExisting: true);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(0);
        result.SkippedCount.Should().Be(1);
    }

    /// <summary>
    /// バリデーションエラーが正しく検出されることを確認
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_InvalidIdm_ReturnsValidationError()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
INVALID_IDM,Suica,001,テスト";

        var filePath = Path.Combine(_testDirectory, "cards_invalid.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _validationServiceMock.Setup(x => x.ValidateCardIdm("INVALID_IDM"))
            .Returns(ValidationResult.Failure("IDmの形式が不正です"));

        // Act
        var result = await _service.ImportCardsAsync(filePath);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCount.Should().Be(1);
        result.Errors.Should().Contain(e => e.Message.Contains("IDmの形式"));
    }

    /// <summary>
    /// 必須フィールドが欠けている場合のエラーを確認
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_MissingRequiredFields_ReturnsError()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
,Suica,001,テスト";

        var filePath = Path.Combine(_testDirectory, "cards_missing.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // Act
        var result = await _service.ImportCardsAsync(filePath);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCount.Should().Be(1);
        result.Errors.Should().Contain(e => e.Message.Contains("カードIDmは必須"));
    }

    /// <summary>
    /// ヘッダーのみのファイルでエラーになることを確認
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_HeaderOnly_ReturnsError()
    {
        // Arrange
        var csvContent = "カードIDm,カード種別,管理番号,備考";

        var filePath = Path.Combine(_testDirectory, "cards_header_only.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // Act
        var result = await _service.ImportCardsAsync(filePath);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("データがありません");
    }

    /// <summary>
    /// Issue #1264: 既に復元済み（IsDeleted=false）のカードを skipExisting=true で再インポート
    /// → Restore は呼ばれず通常のスキップとして扱われる。
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_AlreadyRestoredCard_SkipExistingTrue_Skipped()
    {
        // Arrange: IsDeleted=false（以前復元された後の状態）
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,テスト";
        var filePath = Path.Combine(_testDirectory, "cards_already_restored_skip.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // Issue #1376: 全項目一致のときのみ Skip される仕様のため、既存にも Note="テスト" を設定
        var restoredCard = new IcCard
        {
            CardIdm = "0123456789ABCDEF",
            CardType = "Suica",
            CardNumber = "001",
            Note = "テスト",
            IsDeleted = false
        };
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(restoredCard);

        // Act
        var result = await _service.ImportCardsAsync(filePath, skipExisting: true);

        // Assert: 通常のスキップ扱い。Restore / Update は呼ばれない
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(0);
        result.SkippedCount.Should().Be(1, "復元済みの既存カードは通常のスキップとして扱われる");
        _cardRepositoryMock.Verify(
            x => x.RestoreAsync(It.IsAny<string>(), It.IsAny<SQLiteTransaction>()),
            Times.Never,
            "IsDeleted=false のカードに対して Restore は呼ばれない");
        _cardRepositoryMock.Verify(
            x => x.UpdateAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()),
            Times.Never,
            "skipExisting=true では Update も呼ばれない");
    }

    /// <summary>
    /// Issue #1264: 既に復元済み（IsDeleted=false）のカードを skipExisting=false で再インポート
    /// → Restore は呼ばれず、通常の Update のみが行われる。
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_AlreadyRestoredCard_SkipExistingFalse_UpdatesOnly()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,NEW_NUMBER,更新内容";
        var filePath = Path.Combine(_testDirectory, "cards_already_restored_update.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var restoredCard = new IcCard
        {
            CardIdm = "0123456789ABCDEF",
            CardType = "Suica",
            CardNumber = "OLD_NUMBER",
            IsDeleted = false
        };
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(restoredCard);
        _cardRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        // Act
        var result = await _service.ImportCardsAsync(filePath, skipExisting: false);

        // Assert: 通常の Update のみ。Restore は呼ばれない
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(1);
        _cardRepositoryMock.Verify(
            x => x.RestoreAsync(It.IsAny<string>(), It.IsAny<SQLiteTransaction>()),
            Times.Never,
            "IsDeleted=false のカードでは Restore は呼ばれない");
        _cardRepositoryMock.Verify(
            x => x.UpdateAsync(
                It.Is<IcCard>(c => c.CardIdm == "0123456789ABCDEF" && c.CardNumber == "NEW_NUMBER"),
                It.IsAny<SQLiteTransaction>()),
            Times.Once,
            "CSV の新しい値で Update される");
    }


    /// <summary>
    /// Issue #1264: 復元 → 再度削除 → 再度復元のサイクル。
    /// 複数回インポートを繰り返しても、削除済み判定が毎回正しく動作し、
    /// Restore→Update が適切に呼ばれる。
    /// </summary>
    /// <remarks>
    /// ImportCardsAsync は各呼び出しで独立したトランザクションを使用するため、
    /// <c>BeginTransactionAsync</c> のモックを「呼び出しごとに新しい TransactionScope を
    /// 返す」Func 形式に上書きする必要がある（コンストラクタのデフォルト設定は単一scope）。
    /// </remarks>
    [Fact]
    public async Task ImportCardsAsync_RestoreCycle_WorksRepeatedly()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,サイクル";
        var filePath = Path.Combine(_testDirectory, "cards_restore_cycle.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // 呼び出しごとに新しいトランザクションスコープを返すよう上書き
        _dbContextMock.Setup(x => x.BeginTransactionAsync(It.IsAny<System.Threading.CancellationToken>()))
            .Returns(() =>
            {
                var tx = _connection.BeginTransaction();
                var lease = new ConnectionLease(_connection, () => { });
                var scope = new ICCardManager.Data.TransactionScope(lease, tx);
                return Task.FromResult(scope);
            });

        // 1周目: 削除済みカードが存在 → Restore + Update
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true))
            .ReturnsAsync(new IcCard { CardIdm = "0123456789ABCDEF", IsDeleted = true });
        _cardRepositoryMock.Setup(x => x.RestoreAsync("0123456789ABCDEF", It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _cardRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        var result1 = await _service.ImportCardsAsync(filePath);

        // 2周目: 外部で再度削除された想定で、同じ削除済みカードが返される
        var result2 = await _service.ImportCardsAsync(filePath);

        // Assert: 両サイクルとも成功し、Restore が2回呼ばれる
        result1.Success.Should().BeTrue();
        result1.ImportedCount.Should().Be(1);
        result2.Success.Should().BeTrue();
        result2.ImportedCount.Should().Be(1);
        _cardRepositoryMock.Verify(
            x => x.RestoreAsync("0123456789ABCDEF", It.IsAny<SQLiteTransaction>()),
            Times.Exactly(2),
            "2周目のインポートでも Restore が再度呼ばれる（サイクル動作）");
        _cardRepositoryMock.Verify(
            x => x.UpdateAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()),
            Times.Exactly(2),
            "2周目のインポートでも Update が再度呼ばれる");
    }

    /// <summary>
    /// Issue #1264: RestoreAsync は成功するが UpdateAsync が失敗するケース。
    /// 部分的な DB 変更（IsDeleted=false）がコミットされず、
    /// 全体としてトランザクションロールバック（ImportedCount=0）となる。
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_RestoreSucceedsButUpdateFails_RollsBack()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,復元できたがUpdate失敗";
        var filePath = Path.Combine(_testDirectory, "cards_restore_update_fail.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var deletedCard = new IcCard { CardIdm = "0123456789ABCDEF", IsDeleted = true };
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(deletedCard);
        // Restoreは成功
        _cardRepositoryMock.Setup(x => x.RestoreAsync("0123456789ABCDEF", It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        // Updateは失敗
        _cardRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(false);

        // Act
        var result = await _service.ImportCardsAsync(filePath);

        // Assert: 全体失敗でロールバックされる
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0, "Restore後Update失敗でロールバック");
        result.ErrorCount.Should().BeGreaterThan(0);
        // Restoreは1回呼ばれ、Updateも1回呼ばれる（成功判定の結合AND）
        _cardRepositoryMock.Verify(
            x => x.RestoreAsync("0123456789ABCDEF", It.IsAny<SQLiteTransaction>()),
            Times.Once);
        _cardRepositoryMock.Verify(
            x => x.UpdateAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()),
            Times.Once,
            "Restore成功後にUpdateが試行される");
    }

    /// <summary>
    /// Issue #1264: 1件の通常新規カード + 1件の削除済みカード復元の混合インポート。
    /// 両方とも成功してコミットされる（トランザクション原子性）。
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_MixedNewAndDeletedCard_BothSucceed()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,新規
FEDCBA9876543210,PASMO,002,削除済み復元";
        var filePath = Path.Combine(_testDirectory, "cards_mixed.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // 1件目: 未登録（新規）
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        // 2件目: 削除済み
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("FEDCBA9876543210", true))
            .ReturnsAsync(new IcCard { CardIdm = "FEDCBA9876543210", IsDeleted = true });
        _cardRepositoryMock.Setup(x => x.RestoreAsync("FEDCBA9876543210", It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _cardRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        // Act
        var result = await _service.ImportCardsAsync(filePath);

        // Assert: 両方成功
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(2, "新規1 + 復元1 の合計2件がカウントされる");
        _cardRepositoryMock.Verify(
            x => x.InsertAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()),
            Times.Once,
            "新規カードは Insert");
        _cardRepositoryMock.Verify(
            x => x.RestoreAsync("FEDCBA9876543210", It.IsAny<SQLiteTransaction>()),
            Times.Once,
            "削除済みカードは Restore");
    }

    /// <summary>
    /// Issue #1264: 混合インポートで1件でも Restore が失敗すると、
    /// 正常な新規カードの登録も含めて全体がロールバックされる（トランザクション原子性）。
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_MixedWithRestoreFailure_AllRollback()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,新規成功
FEDCBA9876543210,PASMO,002,削除済みで復元失敗";
        var filePath = Path.Combine(_testDirectory, "cards_mixed_fail.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // 1件目: 新規成功
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        // 2件目: 削除済み → RestoreAsync失敗
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("FEDCBA9876543210", true))
            .ReturnsAsync(new IcCard { CardIdm = "FEDCBA9876543210", IsDeleted = true });
        _cardRepositoryMock.Setup(x => x.RestoreAsync("FEDCBA9876543210", It.IsAny<SQLiteTransaction>())).ReturnsAsync(false);

        // Act
        var result = await _service.ImportCardsAsync(filePath);

        // Assert: 全体ロールバック
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0,
            "トランザクション原子性: Restore失敗で新規カードの登録もロールバック");
        result.ErrorCount.Should().Be(1);
    }

    /// <summary>
    /// Issue #1264: プレビューで削除済みカードは Restore アクションとして表示され、
    /// 状態変更「削除済み → 有効」が変更一覧の先頭に出力される。
    /// </summary>
    [Fact]
    public async Task PreviewCardsAsync_DeletedCard_ShowsRestoreActionWithStateChange()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,復元";
        var filePath = Path.Combine(_testDirectory, "cards_preview_restore.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var deletedCard = new IcCard
        {
            CardIdm = "0123456789ABCDEF",
            CardType = "Suica",
            CardNumber = "001",
            IsDeleted = true
        };
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(deletedCard);

        // Act — skipExisting=true でも削除済みは Restore 対象として扱われる
        var result = await _service.PreviewCardsAsync(filePath, skipExisting: true);

        // Assert
        result.IsValid.Should().BeTrue();
        result.UpdateCount.Should().Be(1, "復元は Update 扱いでカウントされる");
        result.SkipCount.Should().Be(0);

        var item = result.Items.Should().ContainSingle().Subject;
        item.Action.Should().Be(ImportAction.Restore);
        // 変更一覧の先頭に「状態: 削除済み → 有効」が挿入される（CsvImportService.Card.cs:308-313）
        item.Changes.Should().NotBeEmpty();
        item.Changes[0].FieldName.Should().Be("状態");
        item.Changes[0].OldValue.Should().Be("削除済み");
        item.Changes[0].NewValue.Should().Be("有効");
    }

    /// <summary>
    /// Issue #1376: skipExisting=true でも備考のみ変更された既存カードは更新されることを確認
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_NoteChanged_SkipExistingTrue_UpdatesInsteadOfSkip()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,新備考";

        var filePath = Path.Combine(_testDirectory, "cards_skip_note_changed.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // 備考のみ異なる既存カード
        var existingCard = new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001", Note = "旧備考" };
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(existingCard);
        _cardRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        // Act — skipExisting=true でも差分があれば更新される
        var result = await _service.ImportCardsAsync(filePath, skipExisting: true);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(1, "備考差分があるため更新される");
        result.SkippedCount.Should().Be(0);
        _cardRepositoryMock.Verify(
            x => x.UpdateAsync(It.Is<IcCard>(c => c.Note == "新備考"), It.IsAny<SQLiteTransaction>()),
            Times.Once);
    }

    #endregion

    #region ImportStaffAsync テスト

    /// <summary>
    /// 職員のインポートが正常に動作することを確認
    /// </summary>
    [Fact]
    public async Task ImportStaffAsync_WithValidData_ImportsSuccessfully()
    {
        // Arrange
        var csvContent = @"職員IDm,氏名,職員番号,備考
0123456789ABCDEF,山田太郎,001,テスト1
FEDCBA9876543210,鈴木花子,002,テスト2";

        var filePath = Path.Combine(_testDirectory, "staff_import.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), true)).ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        // Act
        var result = await _service.ImportStaffAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(2);
        result.ErrorCount.Should().Be(0);
    }

    /// <summary>
    /// 既存職員が全項目一致のときスキップされることを確認（Issue #1376 で仕様明確化）
    /// </summary>
    [Fact]
    public async Task ImportStaffAsync_ExistingStaff_Skipped()
    {
        // Arrange
        var csvContent = @"職員IDm,氏名,職員番号,備考
0123456789ABCDEF,山田太郎,001,テスト";

        var filePath = Path.Combine(_testDirectory, "staff_existing.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // Issue #1376: 全項目一致の場合のみスキップされる仕様のため、
        // 既存レコードにも CSV と同じ備考を設定して完全一致にする
        var existingStaff = new Staff { StaffIdm = "0123456789ABCDEF", Name = "山田太郎", Number = "001", Note = "テスト" };
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(existingStaff);

        // Act
        var result = await _service.ImportStaffAsync(filePath, skipExisting: true);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(0);
        result.SkippedCount.Should().Be(1);
    }

    /// <summary>
    /// 氏名が欠けている場合のエラーを確認
    /// </summary>
    [Fact]
    public async Task ImportStaffAsync_MissingName_ReturnsError()
    {
        // Arrange
        var csvContent = @"職員IDm,氏名,職員番号,備考
0123456789ABCDEF,,001,テスト";

        var filePath = Path.Combine(_testDirectory, "staff_missing_name.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // Act
        var result = await _service.ImportStaffAsync(filePath);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCount.Should().Be(1);
        result.Errors.Should().Contain(e => e.Message.Contains("氏名は必須"));
    }

    /// <summary>
    /// Issue #1376: skipExisting=true でも備考のみ変更された既存職員は更新されることを確認
    /// </summary>
    [Fact]
    public async Task ImportStaffAsync_NoteChanged_SkipExistingTrue_UpdatesInsteadOfSkip()
    {
        // Arrange
        var csvContent = @"職員IDm,氏名,職員番号,備考
0123456789ABCDEF,山田太郎,001,新備考";

        var filePath = Path.Combine(_testDirectory, "staff_skip_note_changed.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // 備考のみ異なる既存職員
        var existingStaff = new Staff { StaffIdm = "0123456789ABCDEF", Name = "山田太郎", Number = "001", Note = "旧備考" };
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(existingStaff);
        _staffRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        // Act — skipExisting=true でも差分があれば更新される
        var result = await _service.ImportStaffAsync(filePath, skipExisting: true);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(1, "備考差分があるため更新される");
        result.SkippedCount.Should().Be(0);
        _staffRepositoryMock.Verify(
            x => x.UpdateAsync(It.Is<Staff>(s => s.Note == "新備考"), It.IsAny<SQLiteTransaction>()),
            Times.Once);
    }

    #endregion

    #region PreviewCardsAsync テスト

    /// <summary>
    /// カードのプレビューが正常に動作することを確認
    /// </summary>
    [Fact]
    public async Task PreviewCardsAsync_WithValidData_ReturnsPreview()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,テスト1
FEDCBA9876543210,PASMO,002,テスト2";

        var filePath = Path.Combine(_testDirectory, "cards_preview.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), true)).ReturnsAsync((IcCard?)null);

        // Act
        var result = await _service.PreviewCardsAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.NewCount.Should().Be(2);
        result.UpdateCount.Should().Be(0);
        result.SkipCount.Should().Be(0);
        result.Items.Should().HaveCount(2);
        result.Items.Should().AllSatisfy(item => item.Action.Should().Be(ImportAction.Insert));
    }

    /// <summary>
    /// 既存カードが全項目一致のときプレビューで Skip と判定されることを確認（Issue #1376 で仕様明確化）
    /// </summary>
    [Fact]
    public async Task PreviewCardsAsync_ExistingCard_ShowsAsSkip()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,テスト";

        var filePath = Path.Combine(_testDirectory, "cards_preview_existing.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // Issue #1376: 備考も含めて一致しているため Skip になる
        var existingCard = new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001", Note = "テスト" };
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(existingCard);

        // Act
        var result = await _service.PreviewCardsAsync(filePath, skipExisting: true);

        // Assert
        result.IsValid.Should().BeTrue();
        result.NewCount.Should().Be(0);
        result.SkipCount.Should().Be(1);
        result.Items.Should().ContainSingle(item => item.Action == ImportAction.Skip);
    }

    /// <summary>
    /// 既存カードが更新としてプレビューされることを確認（データに変更がある場合）
    /// </summary>
    [Fact]
    public async Task PreviewCardsAsync_ExistingCardNoSkip_ShowsAsUpdate()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,テスト";

        var filePath = Path.Combine(_testDirectory, "cards_preview_update.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // 既存カードは管理番号が「000」なので、CSVの「001」と差異があり更新対象となる
        var existingCard = new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "000" };
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(existingCard);

        // Act
        var result = await _service.PreviewCardsAsync(filePath, skipExisting: false);

        // Assert
        result.IsValid.Should().BeTrue();
        result.UpdateCount.Should().Be(1);
        result.Items.Should().ContainSingle(item => item.Action == ImportAction.Update);
    }

    /// <summary>
    /// バリデーションエラーがあるとプレビューが無効になることを確認
    /// </summary>
    [Fact]
    public async Task PreviewCardsAsync_ValidationError_ReturnsInvalid()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
INVALID_IDM,Suica,001,テスト";

        var filePath = Path.Combine(_testDirectory, "cards_preview_invalid.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _validationServiceMock.Setup(x => x.ValidateCardIdm("INVALID_IDM"))
            .Returns(ValidationResult.Failure("IDmの形式が不正です"));

        // Act
        var result = await _service.PreviewCardsAsync(filePath);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorCount.Should().Be(1);
    }

    /// <summary>
    /// Issue #1370: 備考欄のみが変更された既存カードを Update として検出することを確認
    /// </summary>
    [Fact]
    public async Task PreviewCardsAsync_NoteChanged_DetectsAsUpdate()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,新備考";

        var filePath = Path.Combine(_testDirectory, "cards_preview_note_changed.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // 既存カードと CSV は カード種別・管理番号 が同一で、備考のみ異なる
        var existingCard = new IcCard
        {
            CardIdm = "0123456789ABCDEF",
            CardType = "Suica",
            CardNumber = "001",
            Note = "旧備考"
        };
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(existingCard);

        // Act
        var result = await _service.PreviewCardsAsync(filePath, skipExisting: false);

        // Assert: 備考変更を Update として検出する
        result.IsValid.Should().BeTrue();
        result.UpdateCount.Should().Be(1);
        result.SkipCount.Should().Be(0);
        result.Items.Should().ContainSingle(item =>
            item.Action == ImportAction.Update &&
            item.Changes.Any(c => c.FieldName == "備考" &&
                                  c.OldValue == "旧備考" &&
                                  c.NewValue == "新備考"));
    }

    /// <summary>
    /// Issue #1370: カード種別・管理番号・備考すべて同一の場合は Skip として扱われることを確認
    /// </summary>
    [Fact]
    public async Task PreviewCardsAsync_AllFieldsIdentical_DetectsAsSkip()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,同じ備考";

        var filePath = Path.Combine(_testDirectory, "cards_preview_identical.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var existingCard = new IcCard
        {
            CardIdm = "0123456789ABCDEF",
            CardType = "Suica",
            CardNumber = "001",
            Note = "同じ備考"
        };
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(existingCard);

        // Act — Issue #1376: 全項目一致は skipExisting=true のときに Skip 扱い
        var result = await _service.PreviewCardsAsync(filePath, skipExisting: true);

        // Assert: 全フィールド同一なので Skip 扱い
        result.IsValid.Should().BeTrue();
        result.UpdateCount.Should().Be(0);
        result.SkipCount.Should().Be(1);
        result.Items.Should().ContainSingle(item => item.Action == ImportAction.Skip);
    }

    /// <summary>
    /// Issue #1370: 既存 Note が null で CSV 側が空文字のケースは変更なしと扱われることを確認
    /// </summary>
    [Fact]
    public async Task PreviewCardsAsync_NoteNullVsEmpty_TreatedAsIdentical()
    {
        // Arrange: CSV の備考は空欄
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,";

        var filePath = Path.Combine(_testDirectory, "cards_preview_note_empty.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // 既存カードの Note は null
        var existingCard = new IcCard
        {
            CardIdm = "0123456789ABCDEF",
            CardType = "Suica",
            CardNumber = "001",
            Note = null
        };
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(existingCard);

        // Act — Issue #1376: 全項目一致は skipExisting=true のときに Skip 扱い
        var result = await _service.PreviewCardsAsync(filePath, skipExisting: true);

        // Assert: null と空文字は同一扱い → Skip
        result.IsValid.Should().BeTrue();
        result.SkipCount.Should().Be(1);
        result.Items.Should().ContainSingle(item =>
            item.Action == ImportAction.Skip &&
            !item.Changes.Any(c => c.FieldName == "備考"));
    }

    /// <summary>
    /// Issue #1376: skipExisting=true でも備考のみ変更された既存カードを Update として検出することを確認
    /// </summary>
    [Fact]
    public async Task PreviewCardsAsync_NoteChanged_SkipExistingTrue_DetectsAsUpdate()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,新備考";

        var filePath = Path.Combine(_testDirectory, "cards_preview_skip_note_changed.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var existingCard = new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001", Note = "旧備考" };
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(existingCard);

        // Act — skipExisting=true でも差分があれば Update
        var result = await _service.PreviewCardsAsync(filePath, skipExisting: true);

        // Assert
        result.IsValid.Should().BeTrue();
        result.UpdateCount.Should().Be(1);
        result.SkipCount.Should().Be(0);
        result.Items.Should().ContainSingle(item =>
            item.Action == ImportAction.Update &&
            item.Changes.Any(c => c.FieldName == "備考" &&
                                  c.OldValue == "旧備考" &&
                                  c.NewValue == "新備考"));
    }

    #endregion

    #region PreviewStaffAsync テスト

    /// <summary>
    /// 職員のプレビューが正常に動作することを確認
    /// </summary>
    [Fact]
    public async Task PreviewStaffAsync_WithValidData_ReturnsPreview()
    {
        // Arrange
        var csvContent = @"職員IDm,氏名,職員番号,備考
0123456789ABCDEF,山田太郎,001,テスト1
FEDCBA9876543210,鈴木花子,002,テスト2";

        var filePath = Path.Combine(_testDirectory, "staff_preview.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), true)).ReturnsAsync((Staff?)null);

        // Act
        var result = await _service.PreviewStaffAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.NewCount.Should().Be(2);
        result.Items.Should().HaveCount(2);
        result.Items.Should().AllSatisfy(item =>
        {
            item.Action.Should().Be(ImportAction.Insert);
            item.Name.Should().NotBeNullOrEmpty();
        });
    }

    /// <summary>
    /// Issue #1370: 備考欄のみが変更された既存職員を Update として検出することを確認
    /// </summary>
    [Fact]
    public async Task PreviewStaffAsync_NoteChanged_DetectsAsUpdate()
    {
        // Arrange
        var csvContent = @"職員IDm,氏名,職員番号,備考
0123456789ABCDEF,山田太郎,001,新備考";

        var filePath = Path.Combine(_testDirectory, "staff_preview_note_changed.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // 既存職員と CSV は 氏名・職員番号 が同一で、備考のみ異なる
        var existingStaff = new Staff
        {
            StaffIdm = "0123456789ABCDEF",
            Name = "山田太郎",
            Number = "001",
            Note = "旧備考"
        };
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(existingStaff);

        // Act
        var result = await _service.PreviewStaffAsync(filePath, skipExisting: false);

        // Assert: 備考変更を Update として検出する
        result.IsValid.Should().BeTrue();
        result.UpdateCount.Should().Be(1);
        result.SkipCount.Should().Be(0);
        result.Items.Should().ContainSingle(item =>
            item.Action == ImportAction.Update &&
            item.Changes.Any(c => c.FieldName == "備考" &&
                                  c.OldValue == "旧備考" &&
                                  c.NewValue == "新備考"));
    }

    /// <summary>
    /// Issue #1370: 氏名・職員番号・備考すべて同一の場合は Skip として扱われることを確認
    /// </summary>
    [Fact]
    public async Task PreviewStaffAsync_AllFieldsIdentical_DetectsAsSkip()
    {
        // Arrange
        var csvContent = @"職員IDm,氏名,職員番号,備考
0123456789ABCDEF,山田太郎,001,同じ備考";

        var filePath = Path.Combine(_testDirectory, "staff_preview_identical.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var existingStaff = new Staff
        {
            StaffIdm = "0123456789ABCDEF",
            Name = "山田太郎",
            Number = "001",
            Note = "同じ備考"
        };
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(existingStaff);

        // Act — Issue #1376: 全項目一致は skipExisting=true のときに Skip 扱い
        var result = await _service.PreviewStaffAsync(filePath, skipExisting: true);

        // Assert: 全フィールド同一なので Skip 扱い
        result.IsValid.Should().BeTrue();
        result.UpdateCount.Should().Be(0);
        result.SkipCount.Should().Be(1);
        result.Items.Should().ContainSingle(item => item.Action == ImportAction.Skip);
    }

    /// <summary>
    /// Issue #1376: skipExisting=true でも備考のみ変更された既存職員を Update として検出することを確認
    /// </summary>
    [Fact]
    public async Task PreviewStaffAsync_NoteChanged_SkipExistingTrue_DetectsAsUpdate()
    {
        // Arrange
        var csvContent = @"職員IDm,氏名,職員番号,備考
0123456789ABCDEF,山田太郎,001,新備考";

        var filePath = Path.Combine(_testDirectory, "staff_preview_skip_note_changed.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var existingStaff = new Staff { StaffIdm = "0123456789ABCDEF", Name = "山田太郎", Number = "001", Note = "旧備考" };
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(existingStaff);

        // Act — skipExisting=true でも差分があれば Update
        var result = await _service.PreviewStaffAsync(filePath, skipExisting: true);

        // Assert
        result.IsValid.Should().BeTrue();
        result.UpdateCount.Should().Be(1);
        result.SkipCount.Should().Be(0);
        result.Items.Should().ContainSingle(item =>
            item.Action == ImportAction.Update &&
            item.Changes.Any(c => c.FieldName == "備考" &&
                                  c.OldValue == "旧備考" &&
                                  c.NewValue == "新備考"));
    }

    #endregion

    #region CSVパース テスト

    /// <summary>
    /// ダブルクォートで囲まれたフィールドが正しくパースされることを確認
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_QuotedFields_ParsesCorrectly()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,""Su,ica"",001,""テスト,備考""";

        var filePath = Path.Combine(_testDirectory, "cards_quoted.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()))
            .Callback<IcCard, SQLiteTransaction>((card, _) =>
            {
                // カード種別が正しくパースされているか確認
                card.CardType.Should().Be("Su,ica");
                card.Note.Should().Be("テスト,備考");
            })
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportCardsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
    }

    /// <summary>
    /// エスケープされたダブルクォートが正しくパースされることを確認
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_EscapedQuotes_ParsesCorrectly()
    {
        // Arrange
        // CSVでダブルクォートをエスケープする場合、""で表す
        var csvContent = "カードIDm,カード種別,管理番号,備考\n0123456789ABCDEF,Suica,001,\"テスト\"\"備考\"\"\"";


        var filePath = Path.Combine(_testDirectory, "cards_escaped.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()))
            .Callback<IcCard, SQLiteTransaction>((card, _) =>
            {
                // エスケープされたダブルクォートが正しくパースされているか確認
                card.Note.Should().Be("テスト\"備考\"");
            })
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportCardsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
    }

    #endregion

    #region PreviewLedgersAsync テスト (Issue #428: 残高整合性チェック)

    /// <summary>
    /// 履歴のプレビューで残高整合性チェックが正常にパスすることを確認
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_ValidBalanceConsistency_ReturnsValid()
    {
        // Arrange
        // 残高整合: 初回1000円、1000 + 0 - 200 = 800円
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2024-01-01 10:00:00,0123456789ABCDEF,001,鉄道（A駅～B駅）,,200,1000,山田太郎,
2024-01-02 10:00:00,0123456789ABCDEF,001,鉄道（B駅～C駅）,,200,800,山田太郎,";

        var filePath = Path.Combine(_testDirectory, "ledgers_valid_balance.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // カードが存在するようにモック設定
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.ErrorCount.Should().Be(0);
        result.NewCount.Should().Be(2);
    }

    /// <summary>
    /// 履歴のプレビューで残高不整合が検出されることを確認
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_InvalidBalanceConsistency_ReturnsError()
    {
        // Arrange
        // 残高不整合: 1000 + 0 - 200 = 800 なのに 750 と記録
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2024-01-01 10:00:00,0123456789ABCDEF,001,鉄道（A駅～B駅）,,200,1000,山田太郎,
2024-01-02 10:00:00,0123456789ABCDEF,001,鉄道（B駅～C駅）,,300,750,山田太郎,";

        var filePath = Path.Combine(_testDirectory, "ledgers_invalid_balance.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // カードが存在するようにモック設定
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorCount.Should().Be(1);
        result.Errors.Should().Contain(e => e.Message.Contains("残高が一致しません"));
        result.Errors.Should().Contain(e => e.Message.Contains("期待値: 700円"));
    }

    /// <summary>
    /// チャージ（受入金額あり）を含む残高整合性チェックが正常に動作することを確認
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_WithCharge_BalanceConsistencyValid()
    {
        // Arrange
        // 初回1000円、1000 + 1000 - 0 = 2000（チャージ）、2000 + 0 - 500 = 1500
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2024-01-01 10:00:00,0123456789ABCDEF,001,鉄道（A駅～B駅）,,200,1000,山田太郎,
2024-01-02 10:00:00,0123456789ABCDEF,001,役務費によりチャージ,1000,,2000,山田太郎,
2024-01-03 10:00:00,0123456789ABCDEF,001,鉄道（C駅～D駅）,,500,1500,山田太郎,";

        var filePath = Path.Combine(_testDirectory, "ledgers_with_charge.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.ErrorCount.Should().Be(0);
    }

    /// <summary>
    /// 複数カードの残高整合性チェックが独立して動作することを確認
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_MultipleCards_BalanceConsistencyPerCard()
    {
        // Arrange
        // カード1: 初回1000円、1000 - 200 = 800 (OK)
        // カード2: 初回500円、500 - 50 = 450 なのに 350 と記録 (NG)
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2024-01-01 10:00:00,0123456789ABCDEF,001,鉄道（A駅～B駅）,,200,1000,山田太郎,
2024-01-01 10:00:00,FEDCBA9876543210,002,鉄道（X駅～Y駅）,,100,500,鈴木花子,
2024-01-02 10:00:00,0123456789ABCDEF,001,鉄道（B駅～C駅）,,200,800,山田太郎,
2024-01-02 10:00:00,FEDCBA9876543210,002,鉄道（Y駅～Z駅）,,50,350,鈴木花子,";

        var filePath = Path.Combine(_testDirectory, "ledgers_multi_cards.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001" },
            new IcCard { CardIdm = "FEDCBA9876543210", CardType = "PASMO", CardNumber = "002" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorCount.Should().Be(1);
        // カード2のエラーのみ（500 - 50 = 450円が期待値なのに350円と記録）
        result.Errors.Should().Contain(e => e.Data == "FEDCBA9876543210");
        result.Errors.Should().Contain(e => e.Message.Contains("期待値: 450円") && e.Message.Contains("実際: 350円"));
    }

    /// <summary>
    /// 1件のみの履歴では残高整合性チェックがスキップされることを確認
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_SingleRecord_NoBalanceCheck()
    {
        // Arrange
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2024-01-01 10:00:00,0123456789ABCDEF,001,鉄道（A駅～B駅）,,200,1000,山田太郎,";

        var filePath = Path.Combine(_testDirectory, "ledgers_single.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.ErrorCount.Should().Be(0);
        result.NewCount.Should().Be(1);
    }

    #endregion

    #region Issue #907: 最初の行のDB直前残高との整合性チェック

    /// <summary>
    /// DB上に直前残高がある場合、最初の行の残高がDB直前残高と整合すればOK
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_最初の行がDB直前残高と整合_正常()
    {
        // Arrange: DB上の直前残高は1200円
        // CSV1行目: 受入=0, 払出=200, 残額=1000 → 期待: 1200 + 0 - 200 = 1000 ✓
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2024-01-15 10:00:00,0123456789ABCDEF,001,鉄道（A駅～B駅）,,200,1000,山田太郎,";

        var filePath = Path.Combine(_testDirectory, "ledgers_first_row_db_valid.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // DB上の直前残高: 1200円
        _ledgerRepositoryMock.Setup(x => x.GetLatestBeforeDateAsync("0123456789ABCDEF", It.IsAny<DateTime>()))
            .ReturnsAsync(new Ledger { CardIdm = "0123456789ABCDEF", Balance = 1200, Date = new DateTime(2024, 1, 14) });

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.ErrorCount.Should().Be(0);
    }

    /// <summary>
    /// DB上に直前残高がある場合、最初の行の残高がDB直前残高と不整合ならエラー
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_最初の行がDB直前残高と不整合_エラー()
    {
        // Arrange: DB上の直前残高は1200円
        // CSV1行目: 受入=0, 払出=200, 残額=900 → 期待: 1200 + 0 - 200 = 1000 ≠ 900 ✗
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2024-01-15 10:00:00,0123456789ABCDEF,001,鉄道（A駅～B駅）,,200,900,山田太郎,";

        var filePath = Path.Combine(_testDirectory, "ledgers_first_row_db_invalid.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // DB上の直前残高: 1200円
        _ledgerRepositoryMock.Setup(x => x.GetLatestBeforeDateAsync("0123456789ABCDEF", It.IsAny<DateTime>()))
            .ReturnsAsync(new Ledger { CardIdm = "0123456789ABCDEF", Balance = 1200, Date = new DateTime(2024, 1, 14) });

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorCount.Should().Be(1);
        result.Errors.Should().Contain(e => e.Message.Contains("残高が一致しません"));
        result.Errors.Should().Contain(e => e.Message.Contains("期待値: 1000円"));
        result.Errors.Should().Contain(e => e.Message.Contains("前回残高（DB）: 1200円"));
    }

    /// <summary>
    /// DB上に直前レコードがない場合（新規カード）、最初の行はチェックしない
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_DB直前レコードなし_最初の行スキップ()
    {
        // Arrange: DBに直前残高なし → 最初の行は検証不可
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2024-01-15 10:00:00,0123456789ABCDEF,001,鉄道（A駅～B駅）,,200,999,山田太郎,
2024-01-16 10:00:00,0123456789ABCDEF,001,鉄道（B駅～C駅）,,100,899,山田太郎,";

        var filePath = Path.Combine(_testDirectory, "ledgers_first_row_no_db.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // DB上に直前レコードなし（デフォルトでnullを返す）

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert: 2行目のチェーンは正しいのでOK（999 - 100 = 899）
        result.IsValid.Should().BeTrue();
        result.ErrorCount.Should().Be(0);
    }

    /// <summary>
    /// チャージ（受入金額あり）の最初の行がDB直前残高と整合すること
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_チャージの最初の行がDB直前残高と整合()
    {
        // Arrange: DB上の直前残高は200円
        // CSV1行目: 受入=1000(チャージ), 払出=0, 残額=1200 → 期待: 200 + 1000 - 0 = 1200 ✓
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2024-01-15 10:00:00,0123456789ABCDEF,001,役務費によりチャージ,1000,,1200,山田太郎,";

        var filePath = Path.Combine(_testDirectory, "ledgers_first_row_charge.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        _ledgerRepositoryMock.Setup(x => x.GetLatestBeforeDateAsync("0123456789ABCDEF", It.IsAny<DateTime>()))
            .ReturnsAsync(new Ledger { CardIdm = "0123456789ABCDEF", Balance = 200, Date = new DateTime(2024, 1, 14) });

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// インポート時にも最初の行のDB直前残高チェックが動作すること
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_最初の行がDB直前残高と不整合_エラー()
    {
        // Arrange: DB上の直前残高は5000円
        // CSV1行目: 受入=0, 払出=260, 残額=4000 → 期待: 5000 + 0 - 260 = 4740 ≠ 4000 ✗
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2024-01-15 10:00:00,0123456789ABCDEF,001,鉄道（博多～天神）,,260,4000,山田太郎,";

        var filePath = Path.Combine(_testDirectory, "import_first_row_db_invalid.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        _ledgerRepositoryMock.Setup(x => x.GetLatestBeforeDateAsync("0123456789ABCDEF", It.IsAny<DateTime>()))
            .ReturnsAsync(new Ledger { CardIdm = "0123456789ABCDEF", Balance = 5000, Date = new DateTime(2024, 1, 14) });

        // Act
        var result = await _service.ImportLedgersAsync(filePath);

        // Assert
        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("残高が一致しません"));
        result.Errors.Should().Contain(e => e.Message.Contains("前回残高（DB）: 5000円"));
    }

    #endregion

    #region Issue #639: 繰越レコードの金額変更インポートテスト

    /// <summary>
    /// 既存レコードの残額が変更された場合、プレビューでUpdateと判定されることを確認（Issue #639）
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_BalanceChanged_DetectedAsUpdate()
    {
        // Arrange: ID付きCSVで残額を8806→10000に変更
        var csvContent = @"ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
1,2025-02-01 00:00:00,0123456789ABCDEF,001,12月から繰越,10000,,10000,,";

        var filePath = Path.Combine(_testDirectory, "ledgers_balance_change.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        // 既存レコード: 残額8806円
        var existingLedger = new Ledger
        {
            Id = 1,
            CardIdm = "0123456789ABCDEF",
            Date = new DateTime(2025, 2, 1),
            Summary = "12月から繰越",
            Income = 8806,
            Expense = 0,
            Balance = 8806
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(existingLedger);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.UpdateCount.Should().Be(1);
        result.SkipCount.Should().Be(0);
        result.Items.Should().HaveCount(1);
        result.Items[0].Action.Should().Be(ImportAction.Update);
        result.Items[0].Changes.Should().Contain(c => c.FieldName == "受入金額");
        result.Items[0].Changes.Should().Contain(c => c.FieldName == "残額");
    }

    /// <summary>
    /// 既存レコードの受入金額のみが変更された場合もUpdateと判定されることを確認（Issue #639）
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_IncomeChanged_DetectedAsUpdate()
    {
        // Arrange: 受入金額を5000→6000に変更（残額も連動して変更）
        var csvContent = @"ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
10,2025-01-15 10:00:00,0123456789ABCDEF,001,役務費によりチャージ,6000,,6000,,";

        var filePath = Path.Combine(_testDirectory, "ledgers_income_change.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        var existingLedger = new Ledger
        {
            Id = 10,
            CardIdm = "0123456789ABCDEF",
            Date = new DateTime(2025, 1, 15, 10, 0, 0),
            Summary = "役務費によりチャージ",
            Income = 5000,
            Expense = 0,
            Balance = 5000
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(existingLedger);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.UpdateCount.Should().Be(1);
        result.Items[0].Action.Should().Be(ImportAction.Update);
        result.Items[0].Changes.Should().Contain(c => c.FieldName == "受入金額" && c.OldValue == "5000円" && c.NewValue == "6000円");
        result.Items[0].Changes.Should().Contain(c => c.FieldName == "残額" && c.OldValue == "5000円" && c.NewValue == "6000円");
    }

    /// <summary>
    /// 金額が変更されていない場合はSkipと判定されることを確認（Issue #639）
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_NoChanges_DetectedAsSkip()
    {
        // Arrange: 完全に同一のデータ
        var csvContent = @"ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
1,2025-02-01 00:00:00,0123456789ABCDEF,001,12月から繰越,8806,,8806,,";

        var filePath = Path.Combine(_testDirectory, "ledgers_no_change.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        var existingLedger = new Ledger
        {
            Id = 1,
            CardIdm = "0123456789ABCDEF",
            Date = new DateTime(2025, 2, 1),
            Summary = "12月から繰越",
            Income = 8806,
            Expense = 0,
            Balance = 8806
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(existingLedger);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.SkipCount.Should().Be(1);
        result.UpdateCount.Should().Be(0);
        result.Items.Should().HaveCount(1);
        result.Items[0].Action.Should().Be(ImportAction.Skip);
    }

    /// <summary>
    /// 繰越レコードの残額変更がインポートでUpdateAsync呼び出しに到達することを確認（Issue #639）
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_BalanceChanged_CallsUpdateAsync()
    {
        // Arrange: ID付きCSVで残額を8806→10000に変更
        var csvContent = @"ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
1,2025-02-01 00:00:00,0123456789ABCDEF,001,12月から繰越,10000,,10000,,";

        var filePath = Path.Combine(_testDirectory, "ledgers_import_balance.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        var existingLedger = new Ledger
        {
            Id = 1,
            CardIdm = "0123456789ABCDEF",
            Date = new DateTime(2025, 2, 1),
            Summary = "12月から繰越",
            Income = 8806,
            Expense = 0,
            Balance = 8806,
            LenderIdm = "AABBCCDDEEFF0011",
            IsLentRecord = false
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(existingLedger);
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // Act
        var result = await _service.ImportLedgersAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(1); // updatedCount is included in ImportedCount
        result.SkippedCount.Should().Be(0);

        // UpdateAsyncが呼ばれ、新しい金額が渡されていることを確認
        _ledgerRepositoryMock.Verify(x => x.UpdateAsync(It.Is<Ledger>(l =>
            l.Id == 1 &&
            l.Income == 10000 &&
            l.Balance == 10000 &&
            l.Summary == "12月から繰越"
        ), It.IsAny<SQLiteTransaction>()), Times.Once);
    }

    /// <summary>
    /// 更新時にCSVに含まれないフィールド（LenderIdm等）が既存レコードから引き継がれることを確認（Issue #639）
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_Update_PreservesNonCsvFields()
    {
        // Arrange
        var csvContent = @"ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
5,2025-01-10 14:00:00,0123456789ABCDEF,001,鉄道（博多駅～天神駅）,,200,800,山田太郎,出張";

        var filePath = Path.Combine(_testDirectory, "ledgers_preserve_fields.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        // 既存レコード: LenderIdm等の非CSVフィールドを持つ
        var lentAt = new DateTime(2025, 1, 10, 9, 0, 0);
        var returnedAt = new DateTime(2025, 1, 10, 18, 0, 0);
        var existingLedger = new Ledger
        {
            Id = 5,
            CardIdm = "0123456789ABCDEF",
            Date = new DateTime(2025, 1, 10, 14, 0, 0),
            Summary = "鉄道（博多駅～天神駅）",
            Income = 0,
            Expense = 200,
            Balance = 1000, // 残額が異なる → 変更検出
            StaffName = "山田太郎",
            Note = "出張",
            LenderIdm = "AABBCCDDEEFF0011",
            ReturnerIdm = "1122334455667788",
            LentAt = lentAt,
            ReturnedAt = returnedAt,
            IsLentRecord = false
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(5)).ReturnsAsync(existingLedger);
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // Act
        var result = await _service.ImportLedgersAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(1);

        // UpdateAsyncが呼ばれ、非CSVフィールドが引き継がれていることを確認
        _ledgerRepositoryMock.Verify(x => x.UpdateAsync(It.Is<Ledger>(l =>
            l.Id == 5 &&
            l.Balance == 800 &&
            l.LenderIdm == "AABBCCDDEEFF0011" &&
            l.ReturnerIdm == "1122334455667788" &&
            l.LentAt == lentAt &&
            l.ReturnedAt == returnedAt &&
            l.IsLentRecord == false
        ), It.IsAny<SQLiteTransaction>()), Times.Once);
    }

    /// <summary>
    /// 金額変更がない場合（摘要等のみ変更なし）はスキップされることを確認（Issue #639）
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_NoChanges_Skipped()
    {
        // Arrange: 完全に同一のデータ
        var csvContent = @"ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
1,2025-02-01 00:00:00,0123456789ABCDEF,001,12月から繰越,8806,,8806,,";

        var filePath = Path.Combine(_testDirectory, "ledgers_import_no_change.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        var existingLedger = new Ledger
        {
            Id = 1,
            CardIdm = "0123456789ABCDEF",
            Date = new DateTime(2025, 2, 1),
            Summary = "12月から繰越",
            Income = 8806,
            Expense = 0,
            Balance = 8806
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(existingLedger);

        // Act
        var result = await _service.ImportLedgersAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(0);
        result.SkippedCount.Should().Be(1);

        // UpdateAsyncは呼ばれない
        _ledgerRepositoryMock.Verify(x => x.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Never);
    }

    /// <summary>
    /// 日時が変更された場合もUpdateと判定されることを確認（Issue #639）
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_DateChanged_DetectedAsUpdate()
    {
        // Arrange: 日時を変更
        var csvContent = @"ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
1,2025-01-15 00:00:00,0123456789ABCDEF,001,12月から繰越,8806,,8806,,";

        var filePath = Path.Combine(_testDirectory, "ledgers_date_change.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        // 既存レコード: 日時が2/1
        var existingLedger = new Ledger
        {
            Id = 1,
            CardIdm = "0123456789ABCDEF",
            Date = new DateTime(2025, 2, 1),
            Summary = "12月から繰越",
            Income = 8806,
            Expense = 0,
            Balance = 8806
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(existingLedger);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.UpdateCount.Should().Be(1);
        result.Items[0].Action.Should().Be(ImportAction.Update);
        result.Items[0].Changes.Should().Contain(c => c.FieldName == "日時");
    }

    #endregion

    #region ImportLedgersAsync 残高整合性チェック (Issue #754)

    /// <summary>
    /// スキップされたレコードが間にある場合でも、残高整合性チェックが正しく動作することを確認（Issue #754）
    /// バグ: 変更なしでスキップされたレコードが検証リストから除外され、
    /// 前後関係が崩れて誤った「前回残高」でエラーになっていた
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_SkippedRecordsBetween_BalanceValidationCorrect()
    {
        // Arrange: 6行のCSV。行2,3,4は変更なし（スキップ）、行5は摘要変更（更新）、行6は新規
        // 修正前: 行5の前回残高に行2の残高(7336)が使われ、不正なエラーになっていた
        // 修正後: スキップ行を含む全レコードで検証するため、行5の前回残高は行4(6916)が正しく使われる
        var csvContent = @"ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
1,2025-01-01 00:00:00,0123456789ABCDEF,001,1月から繰越,7336,,7336,,
2,2025-01-10 00:00:00,0123456789ABCDEF,001,鉄道（天神～博多）,,210,7126,,
3,2025-01-10 00:00:00,0123456789ABCDEF,001,鉄道（博多～天神）,,210,6916,,
4,2025-01-15 00:00:00,0123456789ABCDEF,001,鉄道（天神～六本松）,,420,6496,,
5,2025-01-20 00:00:00,0123456789ABCDEF,001,鉄道（六本松～天神）修正,,420,6076,,出張";

        var filePath = Path.Combine(_testDirectory, "ledgers_import_skipped_between.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        // 行2～4: 変更なし → スキップされる
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2025, 1, 1),
            Summary = "1月から繰越", Income = 7336, Expense = 0, Balance = 7336
        });
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(2)).ReturnsAsync(new Ledger
        {
            Id = 2, CardIdm = "0123456789ABCDEF", Date = new DateTime(2025, 1, 10),
            Summary = "鉄道（天神～博多）", Income = 0, Expense = 210, Balance = 7126
        });
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(3)).ReturnsAsync(new Ledger
        {
            Id = 3, CardIdm = "0123456789ABCDEF", Date = new DateTime(2025, 1, 10),
            Summary = "鉄道（博多～天神）", Income = 0, Expense = 210, Balance = 6916
        });
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(4)).ReturnsAsync(new Ledger
        {
            Id = 4, CardIdm = "0123456789ABCDEF", Date = new DateTime(2025, 1, 15),
            Summary = "鉄道（天神～六本松）", Income = 0, Expense = 420, Balance = 6496
        });
        // 行5: 摘要が異なる → 更新対象
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(5)).ReturnsAsync(new Ledger
        {
            Id = 5, CardIdm = "0123456789ABCDEF", Date = new DateTime(2025, 1, 20),
            Summary = "鉄道（六本松～天神）", Income = 0, Expense = 420, Balance = 6076,
            Note = null
        });
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // Act
        var result = await _service.ImportLedgersAsync(filePath);

        // Assert: 残高整合性エラーなし（スキップ行を含む全行で検証される）
        result.Success.Should().BeTrue("残高は正しく連続しているためエラーにならないこと");
        result.ImportedCount.Should().Be(1, "摘要変更の1件のみ更新");
        result.SkippedCount.Should().Be(4, "変更なしの4件はスキップ");
        result.ErrorCount.Should().Be(0);
    }

    /// <summary>
    /// スキップ行を含む場合でも、本当に残高が不整合な行はエラーになることを確認（Issue #754）
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_SkippedRecords_RealInconsistency_DetectsError()
    {
        // Arrange: 行3の残高が不正（6916であるべきだが6900と記録）
        var csvContent = @"ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
1,2025-01-01 00:00:00,0123456789ABCDEF,001,1月から繰越,7336,,7336,,
2,2025-01-10 00:00:00,0123456789ABCDEF,001,鉄道（天神～博多）,,210,7126,,
3,2025-01-10 00:00:00,0123456789ABCDEF,001,鉄道（博多～天神）修正,,210,6900,,";

        var filePath = Path.Combine(_testDirectory, "ledgers_import_real_inconsistency.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        // 行2,3は変更なし、行3だけ変更あり
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2025, 1, 1),
            Summary = "1月から繰越", Income = 7336, Expense = 0, Balance = 7336
        });
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(2)).ReturnsAsync(new Ledger
        {
            Id = 2, CardIdm = "0123456789ABCDEF", Date = new DateTime(2025, 1, 10),
            Summary = "鉄道（天神～博多）", Income = 0, Expense = 210, Balance = 7126
        });
        // 行3: 摘要が異なる → 更新対象、かつ残高が不正
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(3)).ReturnsAsync(new Ledger
        {
            Id = 3, CardIdm = "0123456789ABCDEF", Date = new DateTime(2025, 1, 10),
            Summary = "鉄道（博多～天神）", Income = 0, Expense = 210, Balance = 6916
        });

        // Act
        var result = await _service.ImportLedgersAsync(filePath);

        // Assert: 残高不整合が正しく検出される
        result.Success.Should().BeFalse("残高不整合があるためエラー");
        result.Errors.Should().Contain(e =>
            e.Message.Contains("残高が一致しません") &&
            e.Message.Contains("前回残高: 7126円"),
            "前回残高は行2の7126円であること（スキップ行を含む正しい直前行）");
    }

    #endregion

    #region ImportLedgersAsync skipExisting テスト (Issue #903)

    /// <summary>
    /// skipExisting=trueの場合、既存の重複レコードはスキップされること
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_SkipExistingTrue_既存レコードはスキップされること()
    {
        // Arrange: ID列なしのCSV（旧フォーマット）
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2025-01-10 00:00:00,0123456789ABCDEF,001,鉄道（天神～博多）,,210,7126,,";

        var filePath = Path.Combine(_testDirectory, "ledgers_skip_existing_true.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        // 既存データとして同一キーが存在するようモック
        var existingKeys = new HashSet<(string, DateTime, string, int, int, int)>
        {
            ("0123456789ABCDEF", new DateTime(2025, 1, 10), "鉄道（天神～博多）", 0, 210, 7126)
        };
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(existingKeys);

        // Act: skipExisting=true（デフォルト）
        var result = await _service.ImportLedgersAsync(filePath, skipExisting: true);

        // Assert: 重複レコードはスキップされ、InsertAsyncは呼ばれない
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(0);
        result.SkippedCount.Should().Be(1);
        _ledgerRepositoryMock.Verify(x => x.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Never);
    }

    /// <summary>
    /// skipExisting=falseの場合、既存の重複レコードもスキップせず新規登録されること（Issue #903）
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_SkipExistingFalse_既存レコードもインポートされること()
    {
        // Arrange: ID列なしのCSV（旧フォーマット）
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2025-01-10 00:00:00,0123456789ABCDEF,001,鉄道（天神～博多）,,210,7126,,";

        var filePath = Path.Combine(_testDirectory, "ledgers_skip_existing_false.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(1);

        // Act: skipExisting=false
        var result = await _service.ImportLedgersAsync(filePath, skipExisting: false);

        // Assert: 重複チェックせず新規登録される
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(1);
        result.SkippedCount.Should().Be(0);
        _ledgerRepositoryMock.Verify(x => x.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Once);
        // GetExistingLedgerKeysAsync は呼ばれないこと
        _ledgerRepositoryMock.Verify(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    /// <summary>
    /// プレビュー時もskipExisting=falseの場合、重複レコードはInsert扱いになること（Issue #903）
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_SkipExistingFalse_重複レコードはInsert扱いになること()
    {
        // Arrange: ID列なしのCSV（旧フォーマット）
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2025-01-10 00:00:00,0123456789ABCDEF,001,鉄道（天神～博多）,,210,7126,,";

        var filePath = Path.Combine(_testDirectory, "ledgers_preview_skip_false.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        // Act: skipExisting=false
        var result = await _service.PreviewLedgersAsync(filePath, skipExisting: false);

        // Assert: Insert扱い（Skipではない）
        result.Items.Should().HaveCount(1);
        result.Items[0].Action.Should().Be(ImportAction.Insert);
        result.NewCount.Should().Be(1);
        result.SkipCount.Should().Be(0);
        // GetExistingLedgerKeysAsync は呼ばれないこと
        _ledgerRepositoryMock.Verify(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    /// <summary>
    /// ID列ありCSVで変更がないレコードでも、skipExisting=falseならUpdateAsyncが呼ばれること（Issue #903）
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_IdBased_SkipExistingFalse_変更なしでもUpdateが呼ばれること()
    {
        // Arrange: ID付きCSVで完全に同一のデータ
        var csvContent = @"ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
1,2025-02-01 00:00:00,0123456789ABCDEF,001,12月から繰越,8806,,8806,,";

        var filePath = Path.Combine(_testDirectory, "ledgers_id_skip_false.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        var existingLedger = new Ledger
        {
            Id = 1,
            CardIdm = "0123456789ABCDEF",
            Date = new DateTime(2025, 2, 1),
            Summary = "12月から繰越",
            Income = 8806,
            Expense = 0,
            Balance = 8806
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(existingLedger);
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        // Act: skipExisting=false
        var result = await _service.ImportLedgersAsync(filePath, skipExisting: false);

        // Assert: 変更がなくても更新される（スキップされない）
        result.Success.Should().BeTrue();
        result.SkippedCount.Should().Be(0);
        result.ImportedCount.Should().Be(1);
        _ledgerRepositoryMock.Verify(x => x.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Once);
    }

    /// <summary>
    /// ID列ありCSVで変更がないレコードで、skipExisting=trueならスキップされること（Issue #903）
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_IdBased_SkipExistingTrue_変更なしならスキップされること()
    {
        // Arrange: ID付きCSVで完全に同一のデータ
        var csvContent = @"ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
1,2025-02-01 00:00:00,0123456789ABCDEF,001,12月から繰越,8806,,8806,,";

        var filePath = Path.Combine(_testDirectory, "ledgers_id_skip_true.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        var existingLedger = new Ledger
        {
            Id = 1,
            CardIdm = "0123456789ABCDEF",
            Date = new DateTime(2025, 2, 1),
            Summary = "12月から繰越",
            Income = 8806,
            Expense = 0,
            Balance = 8806
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(existingLedger);

        // Act: skipExisting=true（デフォルト）
        var result = await _service.ImportLedgersAsync(filePath, skipExisting: true);

        // Assert: 変更がないのでスキップされる
        result.Success.Should().BeTrue();
        result.SkippedCount.Should().Be(1);
        _ledgerRepositoryMock.Verify(x => x.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _ledgerRepositoryMock.Verify(x => x.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Never);
    }

    /// <summary>
    /// プレビュー時、ID列ありCSVで変更がないレコードでもskipExisting=falseならUpdate扱いになること（Issue #903）
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_IdBased_SkipExistingFalse_変更なしでもUpdate扱いになること()
    {
        // Arrange: ID付きCSVで完全に同一のデータ
        var csvContent = @"ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
1,2025-02-01 00:00:00,0123456789ABCDEF,001,12月から繰越,8806,,8806,,";

        var filePath = Path.Combine(_testDirectory, "ledgers_id_preview_skip_false.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        var existingLedger = new Ledger
        {
            Id = 1,
            CardIdm = "0123456789ABCDEF",
            Date = new DateTime(2025, 2, 1),
            Summary = "12月から繰越",
            Income = 8806,
            Expense = 0,
            Balance = 8806
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(existingLedger);

        // Act: skipExisting=false
        var result = await _service.PreviewLedgersAsync(filePath, skipExisting: false);

        // Assert: Update扱い（Skipではない）
        result.IsValid.Should().BeTrue();
        result.SkipCount.Should().Be(0);
        result.UpdateCount.Should().Be(1);
        result.Items.Should().HaveCount(1);
        result.Items[0].Action.Should().Be(ImportAction.Update);
    }

    #endregion

    #region PreviewLedgerDetailsAsync テスト (Issue #751)

    /// <summary>
    /// 利用履歴詳細のプレビューが正常に動作することを確認
    /// </summary>
    [Fact]
    public async Task PreviewLedgerDetailsAsync_正常データ_プレビュー成功()
    {
        // Arrange
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
1,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,
1,2024-01-15 17:00:00,0123456789ABCDEF,001,天神,博多,,260,9480,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_preview.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // ledger_id=1が存在するようにモック
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15),
            Summary = "鉄道（博多～天神 往復）", Income = 0, Expense = 520, Balance = 9480
        });

        // Act
        var result = await _service.PreviewLedgerDetailsAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        // Issue #1379: プレビュー件数はインポート件数と揃えて CSV 行数ベース（2行 → 2件）
        result.UpdateCount.Should().Be(2);
        result.Items.Should().HaveCount(1);
        result.Items[0].Action.Should().Be(ImportAction.Update);
        result.Items[0].AdditionalInfo.Should().Contain("2件");
        // Issue #905: プレビューアイテムに利用履歴IDとカードIDmが正しく設定されること
        result.Items[0].Idm.Should().Be("1");
        result.Items[0].Name.Should().Be("0123456789ABCDEF");
    }

    /// <summary>
    /// Issue #905: 複数のledger_idを含むCSVのプレビューで各アイテムに正しいカードIDmが表示されること
    /// </summary>
    [Fact]
    public async Task PreviewLedgerDetailsAsync_複数LedgerId_各アイテムにカードIDmが表示される()
    {
        // Arrange
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
1,2024-01-15 10:30:00,AAAA456789ABCDEF,001,博多,天神,,260,9740,0,0,0,
2,2024-01-16 09:00:00,BBBB456789ABCDEF,002,天神,博多,,260,9480,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_multi_ledger.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1, CardIdm = "AAAA456789ABCDEF", Date = new DateTime(2024, 1, 15),
            Summary = "鉄道（博多～天神）", Income = 0, Expense = 260, Balance = 9740
        });
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(2)).ReturnsAsync(new Ledger
        {
            Id = 2, CardIdm = "BBBB456789ABCDEF", Date = new DateTime(2024, 1, 16),
            Summary = "鉄道（天神～博多）", Income = 0, Expense = 260, Balance = 9480
        });

        // Act
        var result = await _service.PreviewLedgerDetailsAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Items.Should().HaveCount(2);
        result.Items[0].Idm.Should().Be("1");
        result.Items[0].Name.Should().Be("AAAA456789ABCDEF");
        result.Items[0].AdditionalInfo.Should().Be("1件");
        result.Items[1].Idm.Should().Be("2");
        result.Items[1].Name.Should().Be("BBBB456789ABCDEF");
        result.Items[1].AdditionalInfo.Should().Be("1件");
    }

    /// <summary>
    /// 存在しないledger_idがエラーになることを確認
    /// </summary>
    [Fact]
    public async Task PreviewLedgerDetailsAsync_存在しないledger_id_エラー()
    {
        // Arrange
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
999,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_missing_ledger.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // ledger_id=999は存在しない
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(999)).ReturnsAsync((Ledger?)null);

        // Act
        var result = await _service.PreviewLedgerDetailsAsync(filePath);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorCount.Should().Be(1);
        result.Errors.Should().Contain(e => e.Message.Contains("利用履歴ID 999 が存在しません"));
    }

    /// <summary>
    /// 不正なブール値（0/1以外）がエラーになることを確認
    /// </summary>
    [Fact]
    public async Task PreviewLedgerDetailsAsync_不正なブール値_エラー()
    {
        // Arrange
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
1,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,2,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_invalid_bool.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // Act
        var result = await _service.PreviewLedgerDetailsAsync(filePath);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorCount.Should().Be(1);
        result.Errors.Should().Contain(e => e.Message.Contains("チャージは0または1で指定してください"));
    }

    #endregion

    #region ImportLedgerDetailsAsync テスト (Issue #751)

    /// <summary>
    /// 利用履歴詳細のインポートが正常に動作することを確認
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_正常データ_インポート成功()
    {
        // Arrange
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
1,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,
1,2024-01-15 17:00:00,0123456789ABCDEF,001,天神,博多,,260,9480,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_import.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15),
            Summary = "鉄道", Income = 0, Expense = 520, Balance = 9480
        });
        _ledgerRepositoryMock.Setup(x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);
        // Issue #1808: 親 Ledger の UpdateAsync の戻り値を確認するようになったため、成功を明示する
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(true);

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(2);

        // ReplaceDetailsAsyncが1回呼ばれ、2件の詳細が渡される
        _ledgerRepositoryMock.Verify(x => x.ReplaceDetailsAsync(1,
            It.Is<IEnumerable<LedgerDetail>>(d => d.Count() == 2)), Times.Once);
    }

    /// <summary>
    /// 明細は「新しい順」で <c>ReplaceDetailsAsync</c> へ渡されること
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1913: CSV の明細行は <c>CsvExportService</c> と同じ時系列昇順（古い→新しい）で並ぶ。
    /// <c>ReplaceDetailsAsync</c> は DELETE + INSERT で rowid を再採番し、渡された順にそのまま
    /// INSERT するため、昇順のまま渡すと <c>LedgerDetail.SequenceNumber</c> の規約
    /// （FeliCa 互換で<b>小さい rowid ＝ 新しい</b>）が反転する。
    /// </para>
    /// <para>
    /// 反転すると、以後の摘要再生成でブロック順が逆になり、バス停名の同期（Issue #1904）は
    /// 先頭ブロックを最後の利用へ対応付ける。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ImportLedgerDetailsAsync_明細は新しい順でReplaceDetailsAsyncへ渡されること()
    {
        // Arrange: 同一日付の 3 区間（エクスポートと同じ時系列昇順で並べる）
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
1,2024-01-15 00:00:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,
1,2024-01-15 00:00:00,0123456789ABCDEF,001,薬院,大橋,,210,9530,0,0,0,
1,2024-01-15 00:00:00,0123456789ABCDEF,001,姪浜,西新,,230,9300,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_order.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15),
            Summary = "鉄道", Income = 0, Expense = 700, Balance = 9300
        });

        List<LedgerDetail> savedDetails = null;
        _ledgerRepositoryMock.Setup(x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>()))
            .Callback<int, IEnumerable<LedgerDetail>>((_, details) => savedDetails = details.ToList())
            .ReturnsAsync(true);

        Ledger savedLedger = null;
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => savedLedger = l)
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        savedDetails.Should().NotBeNull();
        savedDetails.Select(d => d.EntryStation).Should().Equal(
            new[] { "姪浜", "薬院", "博多" },
            "先に INSERT した明細ほど小さい rowid になるため、最新の明細から渡すこと（Issue #1913）");

        // 対の表明: Reverse は DB 呼び出しにだけ適用し、摘要は時系列昇順のまま生成すること
        savedLedger.Should().NotBeNull();
        savedLedger.Summary.Should().Be(
            "鉄道（博多～天神、薬院～大橋、姪浜～西新）",
            "摘要のブロック順は CSV の並び（時系列昇順）のままであること");
    }

    /// <summary>
    /// ヘッダーのみのファイルでエラーになることを確認
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_ヘッダーのみ_エラー()
    {
        // Arrange
        var csvContent = "利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID";

        var filePath = Path.Combine(_testDirectory, "details_header_only.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("データがありません");
    }

    /// <summary>
    /// 複数のledger_idがグループごとにReplaceDetailsAsyncで置換されることを確認
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_複数ledger_グループごとに置換()
    {
        // Arrange
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
1,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,
2,2024-01-16 09:00:00,0123456789ABCDEF,001,天神,博多,,260,9480,0,0,0,
1,2024-01-15 17:00:00,0123456789ABCDEF,001,天神,博多,,260,9480,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_multi_ledger.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15),
            Summary = "鉄道", Income = 0, Expense = 520, Balance = 9480
        });
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(2)).ReturnsAsync(new Ledger
        {
            Id = 2, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 16),
            Summary = "鉄道", Income = 0, Expense = 260, Balance = 9220
        });
        _ledgerRepositoryMock.Setup(x => x.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);
        // Issue #1808: 親 Ledger の UpdateAsync の戻り値を確認するようになったため、成功を明示する
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(true);

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(3);

        // ledger_id=1に2件、ledger_id=2に1件
        _ledgerRepositoryMock.Verify(x => x.ReplaceDetailsAsync(1,
            It.Is<IEnumerable<LedgerDetail>>(d => d.Count() == 2)), Times.Once);
        _ledgerRepositoryMock.Verify(x => x.ReplaceDetailsAsync(2,
            It.Is<IEnumerable<LedgerDetail>>(d => d.Count() == 1)), Times.Once);
    }

    /// <summary>
    /// 空欄がnullとして正しくパースされることを確認
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_NULL値_正しくパース()
    {
        // Arrange: 駅名・バス停・金額・残額・グループIDが全て空欄
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
1,,0123456789ABCDEF,001,,,,,,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_null_values.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15),
            Summary = "テスト", Income = 0, Expense = 0, Balance = 0
        });
        _ledgerRepositoryMock.Setup(x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);
        // Issue #1808: 親 Ledger の UpdateAsync の戻り値を確認するようになったため、成功を明示する
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(true);

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(1);

        // ReplaceDetailsAsyncに渡された詳細のnull値を検証
        _ledgerRepositoryMock.Verify(x => x.ReplaceDetailsAsync(1,
            It.Is<IEnumerable<LedgerDetail>>(details =>
                details.First().UseDate == null &&
                details.First().EntryStation == null &&
                details.First().ExitStation == null &&
                details.First().BusStops == null &&
                details.First().Amount == null &&
                details.First().Balance == null &&
                details.First().GroupId == null
            )), Times.Once);
    }

    #endregion

    #region ReadCsvFileAsync - ファイル共有読み取りテスト

    [Fact]
    public async Task ReadCsvFileAsync_他プロセスが書き込みロック中でも読み取りできること()
    {
        // Arrange: CSVファイルを作成し、書き込みロックを保持したまま読み取りを試みる
        var filePath = Path.Combine(_testDirectory, "locked_file.csv");
        File.WriteAllText(filePath, "ヘッダー1,ヘッダー2\nデータ1,データ2\n", Encoding.UTF8);

        // 他プロセスが書き込みモードで開いている状態をシミュレート
        using var lockStream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        // Act: ロック中のファイルを読み取り
        var lines = await CsvImportService.ReadCsvFileAsync(filePath);

        // Assert
        lines.Should().HaveCount(2);
        lines[0].Should().Be("ヘッダー1,ヘッダー2");
        lines[1].Should().Be("データ1,データ2");
    }

    [Fact]
    public async Task ReadCsvFileAsync_UTF8_BOM付きファイルを正しく読み取れること()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "bom_file.csv");
        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(filePath, "名前,値\nテスト,123\n", utf8Bom);

        // Act
        var lines = await CsvImportService.ReadCsvFileAsync(filePath);

        // Assert
        lines.Should().HaveCount(2);
        lines[0].Should().Be("名前,値");
    }

    [Fact]
    public async Task ReadCsvFileAsync_空ファイルの場合は空リストを返すこと()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "empty_file.csv");
        File.WriteAllText(filePath, "", Encoding.UTF8);

        // Act
        var lines = await CsvImportService.ReadCsvFileAsync(filePath);

        // Assert
        lines.Should().BeEmpty();
    }

    #endregion

    #region Issue #1744: CSVインポートの文字コード判別

    /// <summary>Shift_JIS（cp932）でCSVファイルを書き出す</summary>
    private string WriteShiftJisCsv(string fileName, string content)
    {
        var filePath = Path.Combine(_testDirectory, fileName);
        File.WriteAllBytes(filePath, Encoding.GetEncoding(932).GetBytes(content));
        return filePath;
    }

    /// <summary>UTF-8 でも Shift_JIS でも復号できないバイト列のCSVを書き出す</summary>
    private string WriteUndecidableCsv(string fileName)
    {
        var filePath = Path.Combine(_testDirectory, fileName);
        // 0x82 は Shift_JIS のリードバイトだが 0x20 は正当なトレイルバイトではなく、
        // UTF-8 としても 0x82 単独は不正。どちらの候補でも復号できない。
        File.WriteAllBytes(filePath, new byte[] { 0x82, 0x20, 0x0A, 0x82, 0x20 });
        return filePath;
    }

    [Fact]
    public async Task ReadCsvFileAsync_BOM無しShiftJISのファイルを文字化けせずに読み取れること()
    {
        // Arrange: 日本語版 Excel で「CSV（コンマ区切り）」保存すると BOM 無し Shift_JIS になる
        var filePath = WriteShiftJisCsv(
            "sjis_no_bom.csv",
            "職員IDm,氏名,職員番号,備考\n0123456789ABCDEF,山田太郎,001,福岡市役所");

        // Act
        var lines = await CsvImportService.ReadCsvFileAsync(filePath);

        // Assert
        lines.Should().HaveCount(2);
        lines[0].Should().Be("職員IDm,氏名,職員番号,備考");
        lines[1].Should().Be("0123456789ABCDEF,山田太郎,001,福岡市役所");
        lines[1].Should().NotContain("�");
    }

    [Fact]
    public async Task ReadCsvFileAsync_文字コードを判別できないファイルは例外を投げること()
    {
        // Arrange
        var filePath = WriteUndecidableCsv("undecidable.csv");

        // Act
        var act = async () => await CsvImportService.ReadCsvFileAsync(filePath);

        // Assert: 生の例外メッセージではなくユーザー向け文言を持つ例外であること（Issue #1614）
        var exception = await act.Should().ThrowAsync<FileOperationException>();
        exception.Which.UserFriendlyMessage.Should().Contain("文字コード");
        exception.Which.UserFriendlyMessage.Should().EndWith("インポートしてください。");
    }

    [Fact]
    public void 文字コード判別不能のエラー文言がエラーメッセージ品質基準を満たすこと()
    {
        // Issue #1275 の「何が／なぜ／どうすれば」3要素を固定する
        var message = FileOperationException
            .UndecidableEncoding(@"C:\work\staff_20260812.csv").UserFriendlyMessage;

        message.Should().Contain("文字コード");            // 何が
        message.Should().Contain("UTF-8");                 // なぜ（判別に用いた候補を示す）
        message.Should().Contain("Shift_JIS");
        message.Should().Contain("CSV UTF-8");             // どうすれば（Excel の保存形式名）
        message.Should().EndWith("してください。");         // 行動指示型で終わる
        message.Length.Should().BeGreaterOrEqualTo(20);
        message.Should().NotContain("エラーが発生しました");
        message.Should().NotContain("不正な値");
        // 内部情報（ファイルパス）をユーザー向け文言へ露出しない（Issue #1614）
        message.Should().NotContain(@"C:\work");
    }

    [Fact]
    public void 宣言された文字コードで読めないエラー文言がエラーメッセージ品質基準を満たすこと()
    {
        var message = FileOperationException
            .UnreadableDeclaredEncoding("UTF-8（BOM付き）", @"C:\work\ledger_20260812.csv").UserFriendlyMessage;

        message.Should().Contain("UTF-8（BOM付き）");        // 何が（判別できた文字コードを名指しする）
        message.Should().Contain("壊れている");              // なぜ（曖昧さではなく破損だと伝える）
        message.Should().EndWith("してください。");           // 行動指示型で終わる
        message.Length.Should().BeGreaterOrEqualTo(20);
        // 判別不能の文言と取り違えない（誤診断＋無意味な指示になるため）
        message.Should().NotContain("判別できませんでした");
        message.Should().NotContain("エラーが発生しました");
        message.Should().NotContain(@"C:\work");
    }

    [Fact]
    public async Task ImportStaffAsync_BOM無しShiftJISのCSVでも氏名が文字化けせずDBへ渡ること()
    {
        // Arrange: Issue #1744 の故障シナリオ（Excel で再保存された職員CSVの取り込み）
        var filePath = WriteShiftJisCsv(
            "staff_sjis.csv",
            "職員IDm,氏名,職員番号,備考\n0123456789ABCDEF,山田太郎,001,交通課");

        Staff? inserted = null;
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), true)).ReturnsAsync((Staff?)null);
        _staffRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()))
            .Callback<Staff, SQLiteTransaction>((s, _) => inserted = s)
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportStaffAsync(filePath);

        // Assert: 文字化けした氏名がそのまま staff へ書き込まれていた退行を固定する
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(1);
        inserted.Should().NotBeNull();
        inserted!.Name.Should().Be("山田太郎");
        inserted.Note.Should().Be("交通課");
    }

    /// <summary>
    /// ReadCsvFileAsync を共有する 8 経路すべて。
    /// 1 つでも独自の catch 連鎖を持つと catch (Exception) へ落ちて生の例外メッセージが出るため
    /// （Issue #1614 違反）、経路を列挙して全件を同じ契約で固定する。
    /// </summary>
    public static IEnumerable<object[]> 全インポート経路 => new[]
    {
        new object[] { "職員インポート" },
        new object[] { "職員プレビュー" },
        new object[] { "カードインポート" },
        new object[] { "カードプレビュー" },
        new object[] { "履歴インポート" },
        new object[] { "履歴プレビュー" },
        new object[] { "履歴詳細インポート" },
        new object[] { "履歴詳細プレビュー" }
    };

    /// <summary>指定した経路を実行し、（成功したか, エラーメッセージ）を返す</summary>
    private async Task<(bool Succeeded, string ErrorMessage)> InvokeImportRouteAsync(string route, string filePath)
    {
        switch (route)
        {
            case "職員インポート":
                var staffImport = await _service.ImportStaffAsync(filePath);
                return (staffImport.Success, staffImport.ErrorMessage);
            case "職員プレビュー":
                var staffPreview = await _service.PreviewStaffAsync(filePath);
                return (staffPreview.IsValid, staffPreview.ErrorMessage);
            case "カードインポート":
                var cardImport = await _service.ImportCardsAsync(filePath);
                return (cardImport.Success, cardImport.ErrorMessage);
            case "カードプレビュー":
                var cardPreview = await _service.PreviewCardsAsync(filePath);
                return (cardPreview.IsValid, cardPreview.ErrorMessage);
            case "履歴インポート":
                var ledgerImport = await _service.ImportLedgersAsync(filePath);
                return (ledgerImport.Success, ledgerImport.ErrorMessage);
            case "履歴プレビュー":
                var ledgerPreview = await _service.PreviewLedgersAsync(filePath);
                return (ledgerPreview.IsValid, ledgerPreview.ErrorMessage);
            case "履歴詳細インポート":
                var detailImport = await _service.ImportLedgerDetailsAsync(filePath);
                return (detailImport.Success, detailImport.ErrorMessage);
            case "履歴詳細プレビュー":
                var detailPreview = await _service.PreviewLedgerDetailsAsync(filePath);
                return (detailPreview.IsValid, detailPreview.ErrorMessage);
            default:
                throw new ArgumentOutOfRangeException(nameof(route), route, "未知のインポート経路");
        }
    }

    [Theory]
    [MemberData(nameof(全インポート経路))]
    public async Task 文字コードを判別できないCSVはどの経路でも行動指示付きのエラーで中断すること(string route)
    {
        // Arrange
        var filePath = WriteUndecidableCsv($"undecidable_{route}.csv");

        // Act
        var (succeeded, errorMessage) = await InvokeImportRouteAsync(route, filePath);

        // Assert
        succeeded.Should().BeFalse();
        errorMessage.Should().Contain("文字コード");
        errorMessage.Should().EndWith("インポートしてください。");
        errorMessage.Should().NotContain("予期しないエラー", "生の例外メッセージを UI へ出さない（Issue #1614）");
    }

    [Theory]
    [MemberData(nameof(全インポート経路))]
    public async Task BOMが示す文字コードで読めないCSVはどの経路でも破損として案内すること(string route)
    {
        // Arrange: UTF-8 BOM を名乗るが本文が壊れているファイル（転送の失敗・切り詰めを模す）
        var filePath = Path.Combine(_testDirectory, $"corrupted_{route}.csv");
        var utf8Bom = new UTF8Encoding(true).GetPreamble();
        var body = new byte[] { 0x82, 0x20 };
        var bytes = new byte[utf8Bom.Length + body.Length];
        Buffer.BlockCopy(utf8Bom, 0, bytes, 0, utf8Bom.Length);
        Buffer.BlockCopy(body, 0, bytes, utf8Bom.Length, body.Length);
        File.WriteAllBytes(filePath, bytes);

        // Act
        var (succeeded, errorMessage) = await InvokeImportRouteAsync(route, filePath);

        // Assert: 「判別できません／CSV UTF-8 で保存し直して」は誤診断かつ無意味な指示になる
        succeeded.Should().BeFalse();
        errorMessage.Should().Contain("UTF-8（BOM付き）", "何として読めなかったかを示す");
        errorMessage.Should().Contain("壊れている", "原因が曖昧さではなく破損であることを伝える");
        errorMessage.Should().NotContain("判別できませんでした");
        errorMessage.Should().EndWith("インポートしてください。");
        errorMessage.Should().NotContain("予期しないエラー");
    }

    [Fact]
    public async Task ImportStaffAsync_文字コードを判別できないCSVでは1件も書き込まないこと()
    {
        // Arrange
        var filePath = WriteUndecidableCsv("staff_undecidable_no_write.csv");

        // Act
        var result = await _service.ImportStaffAsync(filePath);

        // Assert
        result.Success.Should().BeFalse();
        _staffRepositoryMock.Verify(
            x => x.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _staffRepositoryMock.Verify(
            x => x.UpdateAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()), Times.Never);
    }

    #endregion

    #region Issue #1744: 文字コード判別のログ

    private static void VerifyLogged(Mock<ILogger> loggerMock, LogLevel level, string expectedFragment)
    {
        loggerMock.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains(expectedFragment)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce,
            $"{level} ログに「{expectedFragment}」を含む記録が必要");
    }

    private static void VerifyNeverLogged(Mock<ILogger> loggerMock, LogLevel level)
    {
        loggerMock.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Never,
            $"{level} ログを出してはいけない");
    }

    [Fact]
    public async Task ReadCsvFileAsync_ShiftJISと判別したときは文字コード名をInformationで記録すること()
    {
        // Arrange: LogDebug では本番のログレベル設定（Information）で出力されない（Issue #1716）
        var filePath = WriteShiftJisCsv("sjis_logged.csv", "氏名\n山田太郎");
        var loggerMock = new Mock<ILogger>();

        // Act
        await CsvImportService.ReadCsvFileAsync(filePath, loggerMock.Object);

        // Assert: レベルだけでなく「調査を先に進める値」が載っていることまで表明する
        VerifyLogged(loggerMock, LogLevel.Information, "Shift_JIS");
    }

    [Fact]
    public async Task ReadCsvFileAsync_UTF8と判別したときは文字コードのログを出さないこと()
    {
        // Arrange: 既定の文字コードでも毎回出すとログが肥大化する
        var filePath = Path.Combine(_testDirectory, "utf8_not_logged.csv");
        File.WriteAllText(filePath, "氏名\n山田太郎", CsvEncoding);
        var loggerMock = new Mock<ILogger>();

        // Act
        await CsvImportService.ReadCsvFileAsync(filePath, loggerMock.Object);

        // Assert
        VerifyNeverLogged(loggerMock, LogLevel.Information);
    }

    [Fact]
    public async Task ReadCsvFileAsync_BOM無しUTF8と判別したときも文字コードのログを出さないこと()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "utf8_nobom_not_logged.csv");
        File.WriteAllBytes(filePath, new UTF8Encoding(false).GetBytes("氏名\n山田太郎"));
        var loggerMock = new Mock<ILogger>();

        // Act
        await CsvImportService.ReadCsvFileAsync(filePath, loggerMock.Object);

        // Assert
        VerifyNeverLogged(loggerMock, LogLevel.Information);
    }

    [Fact]
    public async Task ReadCsvFileAsync_判別不能のときは中断の理由をWarningで記録すること()
    {
        // Arrange: 中断こそログが要る。呼び出し元の catch は結果へ文言を写すだけでログを書かない
        var filePath = WriteUndecidableCsv("undecidable_logged.csv");
        var loggerMock = new Mock<ILogger>();

        // Act
        var act = async () => await CsvImportService.ReadCsvFileAsync(filePath, loggerMock.Object);

        // Assert
        await act.Should().ThrowAsync<FileOperationException>();
        VerifyLogged(loggerMock, LogLevel.Warning, "判別できません");
        VerifyLogged(loggerMock, LogLevel.Warning, filePath);
    }

    [Fact]
    public async Task ReadCsvFileAsync_BOMが示す文字コードで読めないときはWarningに文字コード名を載せること()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "corrupted_logged.csv");
        var preamble = new UTF8Encoding(true).GetPreamble();
        var bytes = new byte[preamble.Length + 2];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        bytes[preamble.Length] = 0x82;
        bytes[preamble.Length + 1] = 0x20;
        File.WriteAllBytes(filePath, bytes);
        var loggerMock = new Mock<ILogger>();

        // Act
        var act = async () => await CsvImportService.ReadCsvFileAsync(filePath, loggerMock.Object);

        // Assert
        await act.Should().ThrowAsync<FileOperationException>();
        VerifyLogged(loggerMock, LogLevel.Warning, "UTF-8（BOM付き）");
    }

    #endregion

    #region Issue #906: 利用履歴詳細の利用履歴ID自動付与

    /// <summary>
    /// 利用履歴ID空欄のCSVでプレビューが新規作成として表示されること
    /// </summary>
    [Fact]
    public async Task PreviewLedgerDetailsAsync_利用履歴ID空欄_新規作成としてプレビュー()
    {
        // Arrange
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,
,2024-01-15 17:00:00,0123456789ABCDEF,001,天神,博多,,260,9480,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_auto_id_preview.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true))
            .ReturnsAsync(new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん" });

        // Act
        var result = await _service.PreviewLedgerDetailsAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        // Issue #1379: NewCount は CSV 行数ベース（2 行 → 2 件）
        result.NewCount.Should().Be(2);
        result.Items.Should().HaveCount(1);
        result.Items[0].Action.Should().Be(ImportAction.Insert);
        result.Items[0].Idm.Should().Be("(自動付与)");
        result.Items[0].Name.Should().Be("0123456789ABCDEF");
        result.Items[0].AdditionalInfo.Should().Contain("2件");
        // Issue #918: 日付情報も表示される
        result.Items[0].AdditionalInfo.Should().Contain("2024-01-15");
    }

    /// <summary>
    /// 利用履歴ID空欄でカードIDmも空欄の場合エラーになること
    /// </summary>
    [Fact]
    public async Task PreviewLedgerDetailsAsync_利用履歴ID空欄_カードIDm空欄_エラー()
    {
        // Arrange
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
,2024-01-15 10:30:00,,001,博多,天神,,260,9740,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_auto_id_no_card.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // Act
        var result = await _service.PreviewLedgerDetailsAsync(filePath);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("カードIDmは必須です"));
    }

    /// <summary>
    /// 利用履歴ID空欄で未登録カードIDmの場合エラーになること
    /// </summary>
    [Fact]
    public async Task PreviewLedgerDetailsAsync_利用履歴ID空欄_未登録カード_エラー()
    {
        // Arrange
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
,2024-01-15 10:30:00,FFFF456789ABCDEF,001,博多,天神,,260,9740,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_auto_id_unknown_card.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("FFFF456789ABCDEF", true))
            .ReturnsAsync((IcCard?)null);

        // Act
        var result = await _service.PreviewLedgerDetailsAsync(filePath);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("登録されていません"));
    }

    /// <summary>
    /// 利用履歴ID空欄のCSVでLedgerが自動作成されてインポートが成功すること
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_利用履歴ID空欄_Ledger自動作成()
    {
        // Arrange
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,
,2024-01-15 17:00:00,0123456789ABCDEF,001,天神,博多,,260,9480,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_auto_id_import.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true))
            .ReturnsAsync(new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん" });

        // InsertAsyncで新しいledger IDとして100を返す
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(100);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(100, It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(2);

        // Ledgerが自動作成されること
        _ledgerRepositoryMock.Verify(x => x.InsertAsync(It.Is<Ledger>(l =>
            l.CardIdm == "0123456789ABCDEF" &&
            l.Expense == 520 &&
            l.Balance == 9480)), Times.Once);

        // 詳細が新しいledger IDで挿入されること
        _ledgerRepositoryMock.Verify(x => x.InsertDetailsAsync(100,
            It.Is<IEnumerable<LedgerDetail>>(d => d.Count() == 2)), Times.Once);
    }

    /// <summary>
    /// 利用履歴ID空欄と既存IDの混在CSVが正しく処理されること
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_空欄IDと既存ID混在_両方処理()
    {
        // Arrange
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
1,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,
,2024-01-16 09:00:00,AAAA456789ABCDEF,002,天神,博多,,260,9480,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_mixed_ids.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // 既存ledger
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15),
            Summary = "鉄道", Income = 0, Expense = 260, Balance = 9740
        });
        _ledgerRepositoryMock.Setup(x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);
        // Issue #1808: 親 Ledger の UpdateAsync の戻り値を確認するようになったため、成功を明示する
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(true);

        // 新規カード
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("AAAA456789ABCDEF", true))
            .ReturnsAsync(new IcCard { CardIdm = "AAAA456789ABCDEF", CardType = "nimoca" });
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(200);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(200, It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(2);

        // 既存ledgerは置換
        _ledgerRepositoryMock.Verify(x => x.ReplaceDetailsAsync(1,
            It.Is<IEnumerable<LedgerDetail>>(d => d.Count() == 1)), Times.Once);
        // 新規ledgerは作成+挿入
        _ledgerRepositoryMock.Verify(x => x.InsertAsync(It.Is<Ledger>(l =>
            l.CardIdm == "AAAA456789ABCDEF")), Times.Once);
        _ledgerRepositoryMock.Verify(x => x.InsertDetailsAsync(200,
            It.Is<IEnumerable<LedgerDetail>>(d => d.Count() == 1)), Times.Once);
    }

    /// <summary>
    /// 自動作成されるLedgerの摘要がSummaryGeneratorで生成されること
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_自動作成Ledgerの摘要が正しく生成される()
    {
        // Arrange: 博多→天神の片道利用
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_auto_summary.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true))
            .ReturnsAsync(new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん" });

        Ledger? capturedLedger = null;
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => capturedLedger = l)
            .ReturnsAsync(100);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(100, It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        capturedLedger.Should().NotBeNull();
        capturedLedger!.Summary.Should().Contain("鉄道");
        capturedLedger.Summary.Should().Contain("博多");
        capturedLedger.Summary.Should().Contain("天神");
        // Issue #918: 日付でグループ化するため、Date部分のみ（時刻なし）
        capturedLedger.Date.Date.Should().Be(new DateTime(2024, 1, 15));
    }

    /// <summary>
    /// チャージ行の利用履歴ID空欄でincomeが正しく計算されること
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_チャージ行_incomeが正しく計算()
    {
        // Arrange
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
,2024-01-15 10:00:00,0123456789ABCDEF,001,,,,,10000,1,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_auto_charge.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true))
            .ReturnsAsync(new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん" });

        Ledger? capturedLedger = null;
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => capturedLedger = l)
            .ReturnsAsync(100);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(100, It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        capturedLedger.Should().NotBeNull();
        // チャージ行はAmountが空でBalanceが10000、IsCharge=1
        // CalculateGroupFinancialsでチャージのAmountがnull→income=0
        // ただしBalanceは10000
        capturedLedger!.Balance.Should().Be(10000);
    }

    #endregion

    #region Issue #918: 利用履歴詳細インポート時の日付グループ化・金額更新

    /// <summary>
    /// 既存Ledgerの詳細を置換した際に親Ledgerの金額が再計算されること
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_既存Ledger詳細置換_親Ledgerの金額が再計算される()
    {
        // Arrange: 既存Ledgerの詳細を金額変更して再インポート
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
1,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,300,9700,0,0,0,
1,2024-01-15 17:00:00,0123456789ABCDEF,001,天神,博多,,300,9400,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_update_ledger_amounts.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // 既存Ledger（元は260円×2=520円）
        var existingLedger = new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15),
            Summary = "鉄道（博多～天神 往復）", Income = 0, Expense = 520, Balance = 9480
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(existingLedger);
        _ledgerRepositoryMock.Setup(x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(2);

        // 親Ledgerが更新され、金額が300×2=600に再計算されること
        _ledgerRepositoryMock.Verify(x => x.UpdateAsync(It.Is<Ledger>(l =>
            l.Id == 1 &&
            l.Expense == 600 &&
            l.Balance == 9400)), Times.Once);
    }

    /// <summary>
    /// 異なる日付の利用履歴ID空欄行が日付ごとに別のプレビューアイテムとして表示されること
    /// </summary>
    [Fact]
    public async Task PreviewLedgerDetailsAsync_異なる日付の空欄ID行_日付ごとに別プレビュー()
    {
        // Arrange: 同一カードで3日分の履歴（利用履歴ID空欄）
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,
,2024-01-15 17:00:00,0123456789ABCDEF,001,天神,博多,,260,9480,0,0,0,
,2024-01-16 08:30:00,0123456789ABCDEF,001,博多,天神,,260,9220,0,0,0,
,2024-01-17 09:00:00,0123456789ABCDEF,001,博多,天神,,260,8960,0,0,0,
,2024-01-17 18:00:00,0123456789ABCDEF,001,天神,博多,,260,8700,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_multi_date_preview.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true))
            .ReturnsAsync(new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん" });

        // Act
        var result = await _service.PreviewLedgerDetailsAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        // Issue #1379: NewCount は CSV 行数ベース（5 行 → 5 件）。アイテムは日付単位で 3 つ
        result.NewCount.Should().Be(5);
        result.Items.Should().HaveCount(3);

        // 日付順にソートされていること
        result.Items[0].AdditionalInfo.Should().Contain("2件").And.Contain("2024-01-15");
        result.Items[1].AdditionalInfo.Should().Contain("1件").And.Contain("2024-01-16");
        result.Items[2].AdditionalInfo.Should().Contain("2件").And.Contain("2024-01-17");
    }

    /// <summary>
    /// 異なる日付の利用履歴ID空欄行が日付ごとに別のLedgerとして作成されること
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_異なる日付の空欄ID行_日付ごとに別Ledger作成()
    {
        // Arrange: 同一カードで2日分の履歴（利用履歴ID空欄）
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,
,2024-01-15 17:00:00,0123456789ABCDEF,001,天神,博多,,260,9480,0,0,0,
,2024-01-16 08:30:00,0123456789ABCDEF,001,博多,天神,,260,9220,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_multi_date_import.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true))
            .ReturnsAsync(new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん" });

        var capturedLedgers = new List<Ledger>();
        var ledgerIdCounter = 100;
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => capturedLedgers.Add(l))
            .ReturnsAsync(() => ledgerIdCounter++);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(3);

        // 2日分なのでLedgerが2つ作成されること
        capturedLedgers.Should().HaveCount(2);

        // 1日目: 2024-01-15（2件）
        var day1 = capturedLedgers.FirstOrDefault(l => l.Date.Date == new DateTime(2024, 1, 15));
        day1.Should().NotBeNull();
        day1!.Expense.Should().Be(520); // 260 + 260

        // 2日目: 2024-01-16（1件）
        var day2 = capturedLedgers.FirstOrDefault(l => l.Date.Date == new DateTime(2024, 1, 16));
        day2.Should().NotBeNull();
        day2!.Expense.Should().Be(260);

        // InsertDetailsAsyncが2回呼ばれること（日付ごとに1回）
        _ledgerRepositoryMock.Verify(x => x.InsertDetailsAsync(It.IsAny<int>(),
            It.IsAny<IEnumerable<LedgerDetail>>()), Times.Exactly(2));
    }

    /// <summary>
    /// 異なるカードIDmの利用履歴ID空欄行が別々のLedgerとして作成されること（日付が同じでも）
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_異なるカードの同日空欄ID行_カードごとに別Ledger作成()
    {
        // Arrange: 2枚のカードの同日履歴（利用履歴ID空欄）
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,
,2024-01-15 10:30:00,AAAA456789ABCDEF,002,博多,天神,,260,4740,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_multi_card_same_date.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true))
            .ReturnsAsync(new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん" });
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("AAAA456789ABCDEF", true))
            .ReturnsAsync(new IcCard { CardIdm = "AAAA456789ABCDEF", CardType = "nimoca" });

        var capturedLedgers = new List<Ledger>();
        var ledgerIdCounter = 100;
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => capturedLedgers.Add(l))
            .ReturnsAsync(() => ledgerIdCounter++);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(2);

        // 2枚のカードなのでLedgerが2つ作成されること
        capturedLedgers.Should().HaveCount(2);
        capturedLedgers.Should().Contain(l => l.CardIdm == "0123456789ABCDEF");
        capturedLedgers.Should().Contain(l => l.CardIdm == "AAAA456789ABCDEF");
    }

    #endregion

    #region Issue #937: プレビュー時にカード名も表示

    /// <summary>
    /// 利用履歴プレビューでカード名がIDmと一緒に表示されること
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_カード名がIdmと共に表示される()
    {
        // Arrange
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2024-01-01 10:00:00,0123456789ABCDEF,001,鉄道（A駅～B駅）,,200,1000,山田太郎,";

        var filePath = Path.Combine(_testDirectory, "ledgers_card_name.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Items.Should().HaveCount(1);
        result.Items[0].Idm.Should().Be("はやかけん 001 (0123456789ABCDEF)");
    }

    /// <summary>
    /// 複数カードの利用履歴プレビューで各カード名が正しく表示されること
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_複数カードでそれぞれのカード名が表示される()
    {
        // Arrange
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2024-01-01 10:00:00,AAAA456789ABCDEF,001,鉄道（A駅～B駅）,,200,1000,山田太郎,
2024-01-02 10:00:00,BBBB456789ABCDEF,002,鉄道（C駅～D駅）,,300,700,山田太郎,";

        var filePath = Path.Combine(_testDirectory, "ledgers_multi_card_name.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "AAAA456789ABCDEF", CardType = "はやかけん", CardNumber = "001" },
            new IcCard { CardIdm = "BBBB456789ABCDEF", CardType = "nimoca", CardNumber = "002" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Items.Should().HaveCount(2);
        result.Items[0].Idm.Should().Be("はやかけん 001 (AAAA456789ABCDEF)");
        result.Items[1].Idm.Should().Be("nimoca 002 (BBBB456789ABCDEF)");
    }

    /// <summary>
    /// カード情報が取得できない場合はIDmのみが表示されること（フォールバック）
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_カード情報なしの場合はIdmのみ表示()
    {
        // Arrange
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2024-01-01 10:00:00,0123456789ABCDEF,001,鉄道（A駅～B駅）,,200,1000,山田太郎,";

        var filePath = Path.Combine(_testDirectory, "ledgers_no_card_info.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // カードは存在するがカード名情報が空
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "", CardNumber = "" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Items.Should().HaveCount(1);
        // カード名が空の場合はIDmのみ表示
        result.Items[0].Idm.Should().Be("0123456789ABCDEF");
    }

    /// <summary>
    /// 利用履歴詳細プレビュー（既存LedgerID）でカード名がカードIDm列に表示されること
    /// </summary>
    [Fact]
    public async Task PreviewLedgerDetailsAsync_既存LedgerIdでカード名が表示される()
    {
        // Arrange
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
1,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_card_name.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // カード情報を設定
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "SUGOCA", CardNumber = "003" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15),
            Summary = "鉄道（博多～天神）", Income = 0, Expense = 260, Balance = 9740
        });

        // Act
        var result = await _service.PreviewLedgerDetailsAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Items.Should().HaveCount(1);
        result.Items[0].Name.Should().Be("SUGOCA 003 (0123456789ABCDEF)");
    }

    /// <summary>
    /// 利用履歴詳細プレビュー（利用履歴ID空欄・新規作成）でカード名が表示されること
    /// </summary>
    [Fact]
    public async Task PreviewLedgerDetailsAsync_新規作成でカード名が表示される()
    {
        // Arrange
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_card_name_new.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // カード情報を設定（GetByIdmAsync と GetAllIncludingDeletedAsync 両方必要）
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true))
            .ReturnsAsync(new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" });

        // Act
        var result = await _service.PreviewLedgerDetailsAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Items.Should().HaveCount(1);
        result.Items[0].Idm.Should().Be("(自動付与)");
        result.Items[0].Name.Should().Be("はやかけん 001 (0123456789ABCDEF)");
    }

    #endregion

    #region Issue #938: 追加行の詳細表示

    /// <summary>
    /// 新規追加する利用履歴詳細の内容がChangesに格納されること
    /// </summary>
    [Fact]
    public async Task PreviewLedgerDetailsAsync_Insert行に追加内容の詳細が表示される()
    {
        // Arrange
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,
,2024-01-15 17:00:00,0123456789ABCDEF,001,天神,博多,,260,9480,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_insert_changes.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true))
            .ReturnsAsync(new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" });

        // Act
        var result = await _service.PreviewLedgerDetailsAsync(filePath);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Items.Should().HaveCount(1);
        result.Items[0].Action.Should().Be(ImportAction.Insert);
        result.Items[0].HasChanges.Should().BeTrue("追加行にも詳細が表示されるべき");
        result.Items[0].Changes.Should().HaveCount(2);
        result.Items[0].Changes[0].FieldName.Should().Be("[1行目]");
        result.Items[0].Changes[0].OldValue.Should().Be("(新規追加)");
        result.Items[0].Changes[0].NewValue.Should().Contain("博多→天神");
        result.Items[0].Changes[0].NewValue.Should().Contain("260円");
        result.Items[0].Changes[1].FieldName.Should().Be("[2行目]");
        result.Items[0].Changes[1].NewValue.Should().Contain("天神→博多");
    }

    /// <summary>
    /// ChangesHeaderがアクションに応じて変化すること
    /// </summary>
    [Fact]
    public void ChangesHeader_Insertの場合は追加する内容()
    {
        var insertItem = new CsvImportPreviewItem { Action = ImportAction.Insert };
        insertItem.ChangesHeader.Should().Be("追加する内容:");

        var updateItem = new CsvImportPreviewItem { Action = ImportAction.Update };
        updateItem.ChangesHeader.Should().Be("変更内容の詳細:");

        var skipItem = new CsvImportPreviewItem { Action = ImportAction.Skip };
        skipItem.ChangesHeader.Should().Be("スキップするデータ:");
    }

    /// <summary>
    /// FormatDetailDescriptionで鉄道利用の説明が正しく生成されること
    /// </summary>
    [Fact]
    public void FormatDetailDescription_鉄道利用()
    {
        var detail = new LedgerDetail
        {
            UseDate = new DateTime(2024, 1, 15, 10, 30, 0),
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 260,
            Balance = 9740
        };

        var result = CsvImportService.FormatDetailDescription(detail);

        result.Should().Be("2024-01-15 10:30 博多→天神 260円 残額9740円");
    }

    /// <summary>
    /// FormatDetailDescriptionでチャージの説明が正しく生成されること
    /// </summary>
    [Fact]
    public void FormatDetailDescription_チャージ()
    {
        var detail = new LedgerDetail
        {
            UseDate = new DateTime(2024, 1, 15, 12, 0, 0),
            IsCharge = true,
            Amount = 1000,
            Balance = 10740
        };

        var result = CsvImportService.FormatDetailDescription(detail);

        result.Should().Be("2024-01-15 12:00 チャージ 1000円 残額10740円");
    }

    /// <summary>
    /// FormatDetailDescriptionでバス利用の説明が正しく生成されること
    /// </summary>
    [Fact]
    public void FormatDetailDescription_バス利用()
    {
        var detail = new LedgerDetail
        {
            UseDate = new DateTime(2024, 1, 15, 14, 0, 0),
            IsBus = true,
            BusStops = "天神バス停",
            Amount = 200,
            Balance = 9540
        };

        var result = CsvImportService.FormatDetailDescription(detail);

        result.Should().Be("2024-01-15 14:00 バス（天神バス停） 200円 残額9540円");
    }

    /// <summary>
    /// FormatDetailDescriptionでポイント還元の説明が正しく生成されること
    /// </summary>
    [Fact]
    public void FormatDetailDescription_ポイント還元()
    {
        var detail = new LedgerDetail
        {
            UseDate = new DateTime(2024, 1, 15, 16, 0, 0),
            IsPointRedemption = true,
            Amount = 50,
            Balance = 9590
        };

        var result = CsvImportService.FormatDetailDescription(detail);

        result.Should().Be("2024-01-15 16:00 ポイント還元 50円 残額9590円");
    }

    #endregion

    #region Issue #969: 追加・スキップ時もクリックで詳細表示

    /// <summary>
    /// FieldChangeのIsDisplayOnlyがtrueの場合、矢印なしの表示になること
    /// </summary>
    [Fact]
    public void FieldChange_IsDisplayOnly_矢印なしの表示()
    {
        var normalChange = new FieldChange
        {
            FieldName = "日付",
            OldValue = "2024-01-01",
            NewValue = "2024-01-02",
            IsDisplayOnly = false
        };
        normalChange.DisplayText.Should().Be("日付: 2024-01-01 → 2024-01-02");

        var displayOnlyChange = new FieldChange
        {
            FieldName = "日付",
            NewValue = "2024-01-01 09:30:00",
            IsDisplayOnly = true
        };
        displayOnlyChange.DisplayText.Should().Be("日付: 2024-01-01 09:30:00");
    }

    /// <summary>
    /// ChangesHeaderがSkipの場合に「スキップするデータ:」を返すこと
    /// </summary>
    [Fact]
    public void ChangesHeader_Skipの場合はスキップするデータ()
    {
        var skipItem = new CsvImportPreviewItem { Action = ImportAction.Skip };
        skipItem.ChangesHeader.Should().Be("スキップするデータ:");

        var restoreItem = new CsvImportPreviewItem { Action = ImportAction.Restore };
        restoreItem.ChangesHeader.Should().Be("変更内容の詳細:");
    }

    /// <summary>
    /// CreateLedgerDisplayChangesが正しくフィールドを生成すること
    /// </summary>
    [Fact]
    public void CreateLedgerDisplayChanges_全フィールドが正しく生成される()
    {
        var date = new DateTime(2024, 1, 15, 9, 30, 0);
        var result = CsvImportService.CreateLedgerDisplayChanges(
            date, "鉄道（博多～天神）", 0, 260, 9740, "田中太郎", "出張");

        result.Should().HaveCount(6); // 日付、摘要、払出金額、残高、職員名、備考（受入金額は0なので含まない）
        result[0].FieldName.Should().Be("日付");
        result[0].NewValue.Should().Be("2024-01-15 09:30:00");
        result[0].IsDisplayOnly.Should().BeTrue();
        result[1].FieldName.Should().Be("摘要");
        result[1].NewValue.Should().Be("鉄道（博多～天神）");
        result[2].FieldName.Should().Be("払出金額");
        result[2].NewValue.Should().Be("260円");
        result[3].FieldName.Should().Be("残高");
        result[3].NewValue.Should().Be("9,740円");
        result[4].FieldName.Should().Be("職員名");
        result[4].NewValue.Should().Be("田中太郎");
        result[5].FieldName.Should().Be("備考");
        result[5].NewValue.Should().Be("出張");
    }

    /// <summary>
    /// CreateLedgerDisplayChangesで受入金額のみの場合（チャージ等）
    /// </summary>
    [Fact]
    public void CreateLedgerDisplayChanges_受入金額のみ()
    {
        var date = new DateTime(2024, 1, 15, 10, 0, 0);
        var result = CsvImportService.CreateLedgerDisplayChanges(
            date, "役務費によりチャージ", 3000, 0, 12740, null, null);

        result.Should().HaveCount(4); // 日付、摘要、受入金額、残高
        result[2].FieldName.Should().Be("受入金額");
        result[2].NewValue.Should().Be("3,000円");
    }

    /// <summary>
    /// CreateSkipDetailChangesが正しく既存データを表示すること
    /// </summary>
    [Fact]
    public void CreateSkipDetailChanges_既存データの表示()
    {
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2024, 1, 15, 10, 30, 0),
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 260,
                Balance = 9740
            },
            new LedgerDetail
            {
                UseDate = new DateTime(2024, 1, 15, 17, 0, 0),
                EntryStation = "天神",
                ExitStation = "博多",
                Amount = 260,
                Balance = 9480
            }
        };

        var result = CsvImportService.CreateSkipDetailChanges(details);

        result.Should().HaveCount(2);
        result[0].FieldName.Should().Be("[1行目]");
        result[0].NewValue.Should().Contain("博多→天神");
        result[0].NewValue.Should().Contain("260円");
        result[0].IsDisplayOnly.Should().BeTrue();
        result[1].FieldName.Should().Be("[2行目]");
        result[1].NewValue.Should().Contain("天神→博多");
    }

    /// <summary>
    /// 利用履歴プレビューでInsert行にデータ内容が表示されること
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_Insert行にデータ内容が表示される()
    {
        // Arrange
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2024-06-01 09:00:00,0123456789ABCDEF,001,鉄道（博多～天神）,,260,9740,,";

        var filePath = Path.Combine(_testDirectory, "ledger_insert_display.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items[0].Action.Should().Be(ImportAction.Insert);
        result.Items[0].HasChanges.Should().BeTrue("追加行にもデータ内容が表示されるべき");
        result.Items[0].Changes.Should().Contain(c => c.FieldName == "日付" && c.IsDisplayOnly);
        result.Items[0].Changes.Should().Contain(c => c.FieldName == "摘要" && c.NewValue == "鉄道（博多～天神）");
        result.Items[0].ChangesHeader.Should().Be("追加する内容:");
    }

    /// <summary>
    /// 利用履歴プレビューでSkip行（重複）にデータ内容が表示されること
    /// </summary>
    [Fact]
    public async Task PreviewLedgersAsync_Skip行にデータ内容が表示される()
    {
        // Arrange: 既存データと同じ内容のCSV
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2024-06-01 09:00:00,0123456789ABCDEF,001,鉄道（博多～天神）,,260,9740,,";

        var filePath = Path.Combine(_testDirectory, "ledger_skip_display.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        // 同じキーの既存データ
        var existingKeys = new HashSet<(string, DateTime, string, int, int, int)>
        {
            ("0123456789ABCDEF", new DateTime(2024, 6, 1, 9, 0, 0), "鉄道（博多～天神）", 0, 260, 9740)
        };
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(existingKeys);

        // Act
        var result = await _service.PreviewLedgersAsync(filePath);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items[0].Action.Should().Be(ImportAction.Skip);
        result.Items[0].HasChanges.Should().BeTrue("スキップ行にもデータ内容が表示されるべき");
        result.Items[0].Changes.Should().Contain(c => c.FieldName == "摘要" && c.IsDisplayOnly);
        result.Items[0].ChangesHeader.Should().Be("スキップするデータ:");
    }

    /// <summary>
    /// 利用履歴詳細プレビューでSkip行に既存データの内容が表示されること
    /// </summary>
    [Fact]
    public async Task PreviewLedgerDetailsAsync_Skip行に既存データの内容が表示される()
    {
        // Arrange: 既存と同一の詳細データ
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
1,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_skip_display.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true))
            .ReturnsAsync(new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" });

        // 既存の利用履歴（Detailsに同一内容を含む）
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(new Ledger
            {
                Id = 1,
                CardIdm = "0123456789ABCDEF",
                Details = new List<LedgerDetail>
                {
                    new LedgerDetail
                    {
                        LedgerId = 1,
                        UseDate = new DateTime(2024, 1, 15, 10, 30, 0),
                        EntryStation = "博多",
                        ExitStation = "天神",
                        Amount = 260,
                        Balance = 9740,
                        IsCharge = false,
                        IsPointRedemption = false,
                        IsBus = false
                    }
                }
            });

        // Act
        var result = await _service.PreviewLedgerDetailsAsync(filePath);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items[0].Action.Should().Be(ImportAction.Skip);
        result.Items[0].HasChanges.Should().BeTrue("スキップ行にも既存データの内容が表示されるべき");
        result.Items[0].Changes.Should().Contain(c => c.FieldName == "[1行目]" && c.IsDisplayOnly);
        result.Items[0].Changes[0].NewValue.Should().Contain("博多→天神");
        result.Items[0].ChangesHeader.Should().Be("スキップするデータ:");
    }

    #endregion

    #region Issue #1379: プレビュー件数とインポート件数の整合性

    /// <summary>
    /// Issue #1379: 既存 Ledger の更新時、プレビューの UpdateCount とインポート後の ImportedCount が CSV 行数で一致すること
    /// </summary>
    [Fact]
    public async Task LedgerDetails_既存更新_プレビューとインポートの件数が一致()
    {
        // Arrange: 同じ ledger_id=1 に 3 行（既存と差分あり）
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
1,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,
1,2024-01-15 12:00:00,0123456789ABCDEF,001,天神,博多,,260,9480,0,0,0,
1,2024-01-15 17:00:00,0123456789ABCDEF,001,博多,天神,,260,9220,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_count_mismatch_update.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        // 既存 Ledger は Details 空（＝差分あり、更新対象）
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15),
            Summary = "鉄道", Income = 0, Expense = 0, Balance = 9220,
            Details = new List<LedgerDetail>()
        });
        _ledgerRepositoryMock.Setup(x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);
        // Issue #1808: 親 Ledger の UpdateAsync の戻り値を確認するようになったため、成功を明示する
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(true);

        // Act
        var previewResult = await _service.PreviewLedgerDetailsAsync(filePath);
        var importResult = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert: プレビューの件数合計 == インポート後の件数合計 == CSV データ行数
        previewResult.UpdateCount.Should().Be(3);
        importResult.ImportedCount.Should().Be(3);
        (previewResult.NewCount + previewResult.UpdateCount + previewResult.SkipCount)
            .Should().Be(importResult.ImportedCount + importResult.SkippedCount,
                "Issue #1379: プレビュー合計とインポート合計は CSV 行数で一致する必要がある");
    }

    /// <summary>
    /// Issue #1379: 既存 Ledger のスキップ時、プレビューの SkipCount とインポート後の SkippedCount が CSV 行数で一致すること
    /// </summary>
    [Fact]
    public async Task LedgerDetails_既存スキップ_プレビューとインポートの件数が一致()
    {
        // Arrange: 既存データと完全一致する 2 行（差分なし → スキップ）
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
1,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,
1,2024-01-15 17:00:00,0123456789ABCDEF,001,天神,博多,,260,9480,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_count_mismatch_skip.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var existingDetails = new List<LedgerDetail>
        {
            new LedgerDetail { LedgerId = 1, UseDate = new DateTime(2024, 1, 15, 10, 30, 0), EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 9740 },
            new LedgerDetail { LedgerId = 1, UseDate = new DateTime(2024, 1, 15, 17, 0, 0), EntryStation = "天神", ExitStation = "博多", Amount = 260, Balance = 9480 }
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15),
            Summary = "鉄道", Income = 0, Expense = 520, Balance = 9480,
            Details = existingDetails
        });

        // Act
        var previewResult = await _service.PreviewLedgerDetailsAsync(filePath);
        var importResult = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert: プレビューとインポートの Skip 件数が CSV 行数で一致
        previewResult.SkipCount.Should().Be(2);
        importResult.SkippedCount.Should().Be(2);
        importResult.ImportedCount.Should().Be(0);
        (previewResult.NewCount + previewResult.UpdateCount + previewResult.SkipCount)
            .Should().Be(importResult.ImportedCount + importResult.SkippedCount,
                "Issue #1379: プレビュー合計とインポート合計は CSV 行数で一致する必要がある");
    }

    /// <summary>
    /// Issue #1379: 新規 Ledger 作成時、プレビューの NewCount とインポート後の ImportedCount が CSV 行数で一致すること
    /// </summary>
    [Fact]
    public async Task LedgerDetails_新規作成_プレビューとインポートの件数が一致()
    {
        // Arrange: 利用履歴 ID 空欄、同一日 3 行
        var csvContent = @"利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID
,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,
,2024-01-15 12:00:00,0123456789ABCDEF,001,天神,博多,,260,9480,0,0,0,
,2024-01-15 17:00:00,0123456789ABCDEF,001,博多,天神,,260,9220,0,0,0,";

        var filePath = Path.Combine(_testDirectory, "details_count_mismatch_new.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true))
            .ReturnsAsync(new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" });

        var ledgerIdCounter = 100;
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(() => ledgerIdCounter++);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        // Act
        var previewResult = await _service.PreviewLedgerDetailsAsync(filePath);
        var importResult = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert: プレビューの NewCount とインポートの ImportedCount が CSV 行数で一致
        previewResult.NewCount.Should().Be(3);
        importResult.ImportedCount.Should().Be(3);
        (previewResult.NewCount + previewResult.UpdateCount + previewResult.SkipCount)
            .Should().Be(importResult.ImportedCount + importResult.SkippedCount,
                "Issue #1379: プレビュー合計とインポート合計は CSV 行数で一致する必要がある");
    }

    #endregion

    #region Issue #1808: CSVインポートの無言欠陥（親Ledger更新の握りつぶし・職員番号の幻の差分・往復クォート混入）

    private const string DetailCsvHeader =
        "利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID";

    /// <summary>
    /// 明細インポートで親 Ledger の <c>UpdateAsync</c> が 0 行（他 PC が履歴を削除済み）を返したとき、
    /// エラーとして報告しインポート件数に含めないこと。旧実装は戻り値を捨てて「インポート完了」にしていた。
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_親Ledgerの更新が0行_エラーとして報告しインポート件数に含めない()
    {
        // Arrange
        var csvContent = DetailCsvHeader + @"
1,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,300,9700,0,0,0,";
        var filePath = Path.Combine(_testDirectory, "details_parent_update_conflict.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var existingLedger = new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15),
            Summary = "鉄道（博多～天神）", Income = 0, Expense = 260, Balance = 9740
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(existingLedger);
        _ledgerRepositoryMock.Setup(x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);
        // 競合: WHERE id = 1 に一致する行が無い（Issue #1753 の影響行数検出）
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(false);

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0, "親 Ledger を更新できなかった明細は取り込めていない");
        result.ErrorCount.Should().Be(1);
        var error = result.Errors.Single();
        error.LineNumber.Should().Be(2);
        AssertParentLedgerConflictMessage(error.Message, ledgerId: 1);
    }

    /// <summary>
    /// 明細を置き換えたあとの再読取で親 Ledger が見つからない（置換と読取の間に削除された）ときも、
    /// 同じくエラーとして報告し、存在しない行への <c>UpdateAsync</c> は呼ばないこと。
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_置換後に親Ledgerが見つからない_エラーとして報告しUpdateAsyncを呼ばない()
    {
        // Arrange
        var csvContent = DetailCsvHeader + @"
1,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,300,9700,0,0,0,";
        var filePath = Path.Combine(_testDirectory, "details_parent_missing_after_replace.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var existingLedger = new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15),
            Summary = "鉄道（博多～天神）", Income = 0, Expense = 260, Balance = 9740
        };
        // 1 回目（存在チェック）は見つかり、2 回目（置換後の再読取）は削除済み
        _ledgerRepositoryMock.SetupSequence(x => x.GetByIdAsync(1))
            .ReturnsAsync(existingLedger)
            .ReturnsAsync((Ledger?)null);
        _ledgerRepositoryMock.Setup(x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0);
        result.ErrorCount.Should().Be(1);
        AssertParentLedgerConflictMessage(result.Errors.Single().Message, ledgerId: 1);
        _ledgerRepositoryMock.Verify(x => x.UpdateAsync(It.IsAny<Ledger>()), Times.Never);
    }

    /// <summary>
    /// 親 Ledger が <c>ReplaceDetailsAsync</c> より前に削除されていると、明細 INSERT が
    /// FOREIGN KEY 制約違反で例外になる（foreign_keys=ON）。この経路でも生の <c>ex.Message</c> を
    /// UI へ出さず（Issue #1614）、「履歴が削除された可能性」を名指しした行動指示で終わる文言にすること。
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_置換が外部キー制約違反_生の例外メッセージを出さず削除競合として案内()
    {
        // Arrange
        var csvContent = DetailCsvHeader + @"
1,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,300,9700,0,0,0,";
        var filePath = Path.Combine(_testDirectory, "details_parent_fk_violation.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var existingLedger = new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15),
            Summary = "鉄道（博多～天神）", Income = 0, Expense = 260, Balance = 9740
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(existingLedger);
        _ledgerRepositoryMock.Setup(x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>()))
            .ThrowsAsync(new SQLiteException(SQLiteErrorCode.Constraint, "constraint failed\r\nFOREIGN KEY constraint failed"));

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0);
        var error = result.Errors.Should().ContainSingle().Subject;
        error.LineNumber.Should().Be(2);
        error.Message.Should().Contain("利用履歴ID 1");
        error.Message.Should().Contain("削除された可能性");
        error.Message.Should().NotContain("FOREIGN KEY");
        error.Message.Should().NotContain("constraint");
        error.Message.Should().MatchRegex("してください。?$");
        _ledgerRepositoryMock.Verify(x => x.UpdateAsync(It.IsAny<Ledger>()), Times.Never);
    }

    /// <summary>
    /// 明細の置換は確定したが親 Ledger の <c>UpdateAsync</c> が例外（共有モードの SQLITE_BUSY 等）のとき、
    /// 「明細は置き換えた・親の摘要・金額は未更新」という実際の状態を案内し、生の <c>ex.Message</c> を出さないこと。
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_親Ledger更新が例外_明細は置換済みと案内し生の例外メッセージを出さない()
    {
        // Arrange
        var csvContent = DetailCsvHeader + @"
1,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,300,9700,0,0,0,";
        var filePath = Path.Combine(_testDirectory, "details_parent_update_busy.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var existingLedger = new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15),
            Summary = "鉄道（博多～天神）", Income = 0, Expense = 260, Balance = 9740
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(existingLedger);
        _ledgerRepositoryMock.Setup(x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>()))
            .ThrowsAsync(new SQLiteException(SQLiteErrorCode.Busy, "database is locked"));

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeFalse();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Message.Should().Contain("明細は置き換えました");
        error.Message.Should().NotContain("database is locked");
        error.Message.Should().MatchRegex("してください。?$");
    }

    /// <summary>
    /// 競合エラーの文言が「何が／なぜ／どうすれば」を満たすこと（.claude/rules/error-messages.md）。
    /// 「影響行数 0」の原因は行の消失に特定できるため、それを名指しし、モード中立に「可能性」で述べる（Issue #1759）。
    /// </summary>
    private static void AssertParentLedgerConflictMessage(string message, int ledgerId)
    {
        message.Should().Contain($"利用履歴ID {ledgerId}", "何が: どの履歴か");
        message.Should().Contain("削除された可能性", "なぜ: 行が消えた（競合）ことを名指しする");
        message.Should().NotContain("失敗しました", "原因を特定できる以上、汎用の失敗文言にしない");
        message.Should().MatchRegex("してください。?$", "どうすれば: 行動指示で終わる");
        message.Length.Should().BeGreaterThanOrEqualTo(20);
    }

    /// <summary>
    /// 職員番号が未設定（DB は null）の職員は、CSV の空欄（"" や空白のみ）と一致し、
    /// 他項目が同一なら Skip になること。旧実装は <c>null != ""</c> で毎回「更新」に数え、
    /// 実在しない差分「職員番号: (なし) → （空）」をプレビューに出していた。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PreviewStaffAsync_職員番号が未設定の職員とCSVの空欄_差分なしとしてSkip(string csvNumberCell)
    {
        // Arrange
        var csvContent = "職員IDm,氏名,職員番号,備考\n" +
                         $"0123456789ABCDEF,山田太郎,{csvNumberCell},同じ備考";
        var filePath = Path.Combine(_testDirectory, $"staff_preview_null_number_{csvNumberCell.Length}.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var existingStaff = new Staff
        {
            StaffIdm = "0123456789ABCDEF",
            Name = "山田太郎",
            Number = null, // 保存時に IsNullOrWhiteSpace → null へ正規化されている
            Note = "同じ備考"
        };
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(existingStaff);

        // Act
        var result = await _service.PreviewStaffAsync(filePath, skipExisting: true);

        // Assert
        result.IsValid.Should().BeTrue();
        result.UpdateCount.Should().Be(0);
        result.SkipCount.Should().Be(1);
        var item = result.Items.Should().ContainSingle().Subject;
        item.Action.Should().Be(ImportAction.Skip);
        item.Changes.Should().BeEmpty("職員番号 null と空欄は同じ「未設定」であり差分ではない");
    }

    /// <summary>
    /// 職員番号が未設定でも、備考が実際に変わっていれば従来どおり Update と判定し、
    /// 差分一覧に「職員番号」の幻の行が混ざらないこと（是正が正当な差分検出を塞いでいないこと）。
    /// </summary>
    [Fact]
    public async Task PreviewStaffAsync_職員番号が未設定で備考のみ変更_職員番号の差分は出さずUpdate()
    {
        // Arrange
        var csvContent = @"職員IDm,氏名,職員番号,備考
0123456789ABCDEF,山田太郎,,新備考";
        var filePath = Path.Combine(_testDirectory, "staff_preview_null_number_note_changed.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var existingStaff = new Staff
        {
            StaffIdm = "0123456789ABCDEF",
            Name = "山田太郎",
            Number = null,
            Note = "旧備考"
        };
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(existingStaff);

        // Act
        var result = await _service.PreviewStaffAsync(filePath, skipExisting: true);

        // Assert
        result.UpdateCount.Should().Be(1);
        var item = result.Items.Should().ContainSingle().Subject;
        item.Action.Should().Be(ImportAction.Update);
        item.Changes.Should().ContainSingle(c => c.FieldName == "備考");
        item.Changes.Should().NotContain(c => c.FieldName == "職員番号");
    }

    /// <summary>
    /// インポート本体（<c>ImportStaffAsync</c>）でも同じ差分検出を通るため、
    /// 職員番号未設定＋他項目一致の職員は skipExisting=true で Skip され、更新が発行されないこと。
    /// </summary>
    [Fact]
    public async Task ImportStaffAsync_職員番号が未設定で他項目一致_skipExistingTrueでSkipされUpdateAsyncを呼ばない()
    {
        // Arrange
        var csvContent = @"職員IDm,氏名,職員番号,備考
0123456789ABCDEF,山田太郎,,同じ備考";
        var filePath = Path.Combine(_testDirectory, "staff_import_null_number_identical.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var existingStaff = new Staff
        {
            StaffIdm = "0123456789ABCDEF",
            Name = "山田太郎",
            Number = null,
            Note = "同じ備考"
        };
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(existingStaff);
        _staffRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        // Act
        var result = await _service.ImportStaffAsync(filePath, skipExisting: true);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(0);
        result.SkippedCount.Should().Be(1);
        _staffRepositoryMock.Verify(
            x => x.UpdateAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()), Times.Never);
    }

    /// <summary>
    /// 往復対称性: <c>CsvExportService</c> がエクスポートした職員 CSV（備考 <c>-異動予定</c> は
    /// 式インジェクション対策で <c>'-異動予定</c> と出力される）をそのまま取り込むと、
    /// 全項目一致として Skip になること。旧実装はエクスポート由来の <c>'</c> を DB へ持ち込み、
    /// 管理者マニュアル §5.6.5 が推奨する「エクスポート CSV を編集して取り込む」運用が汚染経路になっていた。
    /// </summary>
    [Fact]
    public async Task PreviewStaffAsync_エクスポートしたCSVをそのまま取り込む_全項目一致でSkip()
    {
        // Arrange: DB には UI から入力された自然な値が入っている
        var staff = new Staff
        {
            StaffIdm = "0123456789ABCDEF",
            Name = "山田太郎",
            Number = "001",
            Note = "-異動予定"
        };
        _staffRepositoryMock.Setup(x => x.GetAllAsync()).ReturnsAsync(new List<Staff> { staff });
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync("0123456789ABCDEF", true)).ReturnsAsync(staff);

        var exportService = new CsvExportService(
            _cardRepositoryMock.Object, _staffRepositoryMock.Object, _ledgerRepositoryMock.Object);
        var filePath = Path.Combine(_testDirectory, "staff_roundtrip.csv");
        var exportResult = await exportService.ExportStaffAsync(filePath);
        exportResult.Success.Should().BeTrue();
        // 前提の確認: エクスポート側は Excel 安全性のため ' を付与している（Issue #1267）
        (await Task.Run(() => File.ReadAllText(filePath, CsvEncoding))).Should().Contain("'-異動予定");

        // Act
        var result = await _service.PreviewStaffAsync(filePath, skipExisting: true);

        // Assert
        result.IsValid.Should().BeTrue();
        result.UpdateCount.Should().Be(0);
        result.SkipCount.Should().Be(1);
        result.Items.Should().ContainSingle().Which.Changes.Should().BeEmpty();
    }

    /// <summary>
    /// 職員 CSV の備考先頭にあるサニタイズ由来の <c>'</c>（直後が危険文字）は取り除いて保存し、
    /// 危険文字で始まる値そのものはサニタイズせず自然な形で保存すること（防御は sink 側の
    /// エクスポート／帳票出力が担う。UI 入力と同じ扱いに揃える）。
    /// 一方 <c>'</c> の直後が安全文字なら利用者の入力としてそのまま保存する。
    /// </summary>
    [Theory]
    [InlineData("'-異動予定", "-異動予定")]  // エクスポート由来の ' を除去
    [InlineData("-異動予定", "-異動予定")]   // 危険文字始まりでも ' を付けない
    [InlineData("'メモ", "'メモ")]           // 利用者が入力した ' はそのまま
    public async Task ImportStaffAsync_備考のサニタイズ由来クォート_取り除いて自然な値で保存(string csvNote, string expectedNote)
    {
        // Arrange
        var csvContent = "職員IDm,氏名,職員番号,備考\n" +
                         $"0123456789ABCDEF,山田太郎,001,{csvNote}";
        var filePath = Path.Combine(_testDirectory, $"staff_import_unsanitize_{Guid.NewGuid():N}.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), true)).ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        // Act
        var result = await _service.ImportStaffAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        _staffRepositoryMock.Verify(
            x => x.InsertAsync(It.Is<Staff>(s => s.Note == expectedNote), It.IsAny<SQLiteTransaction>()),
            Times.Once);
    }

    /// <summary>
    /// カード CSV の備考も同じ往復対称性を持つこと（管理番号・種別も含めエクスポートが全列を
    /// サニタイズするため、取り込み側もテキスト列すべてで <c>'</c> を取り除く）。
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_テキスト列のサニタイズ由来クォート_取り除いて保存()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,はやかけん,'-01,'-予備機";
        var filePath = Path.Combine(_testDirectory, "cards_import_unsanitize.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), true)).ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        // Act
        var result = await _service.ImportCardsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        _cardRepositoryMock.Verify(
            x => x.InsertAsync(It.Is<IcCard>(c => c.CardNumber == "-01" && c.Note == "-予備機"), It.IsAny<SQLiteTransaction>()),
            Times.Once);
    }

    /// <summary>
    /// 履歴 CSV の摘要・備考も同じ往復対称性を持つこと。
    /// エクスポート由来の <c>'</c> が付いた備考は、既存の値（自然な形）と一致すれば差分にならない。
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_備考がエクスポート由来のクォート付き_既存の自然な値と一致してSkip()
    {
        // Arrange: 備考 "-立替" は CSV 上では "'-立替"（エクスポート由来）
        var csvContent = @"ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
1,2025-02-01 00:00:00,0123456789ABCDEF,001,12月から繰越,8806,,8806,,'-立替";
        var filePath = Path.Combine(_testDirectory, "ledgers_import_unsanitize_skip.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        });
        var existingLedger = new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2025, 2, 1),
            Summary = "12月から繰越", Income = 8806, Expense = 0, Balance = 8806, Note = "-立替"
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(existingLedger);

        // Act
        var result = await _service.ImportLedgersAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.SkippedCount.Should().Be(1);
        result.ImportedCount.Should().Be(0);
        _ledgerRepositoryMock.Verify(x => x.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Never);
    }

    /// <summary>
    /// 明細 CSV の乗車駅・降車駅・バス停も同じ往復対称性を持つこと。
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_駅名バス停のサニタイズ由来クォート_取り除いて保存()
    {
        // Arrange
        var csvContent = DetailCsvHeader + @"
1,2024-01-15 10:30:00,0123456789ABCDEF,001,'-博多,'@天神,'=中央,300,9700,0,0,0,";
        var filePath = Path.Combine(_testDirectory, "details_import_unsanitize.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var existingLedger = new Ledger
        {
            Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15),
            Summary = "鉄道（博多～天神）", Income = 0, Expense = 260, Balance = 9740
        };
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(existingLedger);
        List<LedgerDetail>? captured = null;
        _ledgerRepositoryMock.Setup(x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>()))
            .Callback<int, IEnumerable<LedgerDetail>>((_, d) => captured = d.ToList())
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(true);

        // Act
        var result = await _service.ImportLedgerDetailsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        var detail = captured!.Should().ContainSingle().Subject;
        detail.EntryStation.Should().Be("-博多");
        detail.ExitStation.Should().Be("@天神");
        detail.BusStops.Should().Be("=中央");
    }

    #endregion
}
