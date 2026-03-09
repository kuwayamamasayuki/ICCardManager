using System.IO;
using System.Text;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
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

        // トランザクションのモック（実際のトランザクションは使用しない）
        var connectionString = "Data Source=:memory:";
        _connection = new SQLiteConnection(connectionString);
        _connection.Open();
        var transaction = _connection.BeginTransaction();

        _dbContextMock.Setup(x => x.BeginTransaction()).Returns(transaction);

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
    /// 既存カードがスキップされることを確認
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_ExistingCard_Skipped()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,テスト";

        var filePath = Path.Combine(_testDirectory, "cards_existing.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var existingCard = new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001" };
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
    /// 既存職員がスキップされることを確認
    /// </summary>
    [Fact]
    public async Task ImportStaffAsync_ExistingStaff_Skipped()
    {
        // Arrange
        var csvContent = @"職員IDm,氏名,職員番号,備考
0123456789ABCDEF,山田太郎,001,テスト";

        var filePath = Path.Combine(_testDirectory, "staff_existing.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var existingStaff = new Staff { StaffIdm = "0123456789ABCDEF", Name = "山田太郎", Number = "001" };
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
    /// 既存カードがスキップとしてプレビューされることを確認
    /// </summary>
    [Fact]
    public async Task PreviewCardsAsync_ExistingCard_ShowsAsSkip()
    {
        // Arrange
        var csvContent = @"カードIDm,カード種別,管理番号,備考
0123456789ABCDEF,Suica,001,テスト";

        var filePath = Path.Combine(_testDirectory, "cards_preview_existing.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var existingCard = new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001" };
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
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(true);
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
        )), Times.Once);
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
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(true);
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
        )), Times.Once);
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
        _ledgerRepositoryMock.Verify(x => x.UpdateAsync(It.IsAny<Ledger>()), Times.Never);
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
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(true);
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
        _ledgerRepositoryMock.Verify(x => x.InsertAsync(It.IsAny<Ledger>()), Times.Never);
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
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);

        // Act: skipExisting=false
        var result = await _service.ImportLedgersAsync(filePath, skipExisting: false);

        // Assert: 重複チェックせず新規登録される
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(1);
        result.SkippedCount.Should().Be(0);
        _ledgerRepositoryMock.Verify(x => x.InsertAsync(It.IsAny<Ledger>()), Times.Once);
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
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(true);

        // Act: skipExisting=false
        var result = await _service.ImportLedgersAsync(filePath, skipExisting: false);

        // Assert: 変更がなくても更新される（スキップされない）
        result.Success.Should().BeTrue();
        result.SkippedCount.Should().Be(0);
        result.ImportedCount.Should().Be(1);
        _ledgerRepositoryMock.Verify(x => x.UpdateAsync(It.IsAny<Ledger>()), Times.Once);
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
        _ledgerRepositoryMock.Verify(x => x.UpdateAsync(It.IsAny<Ledger>()), Times.Never);
        _ledgerRepositoryMock.Verify(x => x.InsertAsync(It.IsAny<Ledger>()), Times.Never);
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
        result.UpdateCount.Should().Be(1); // ledger_id=1のグループ1件
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
        result.NewCount.Should().Be(1);
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
        // 3日分なので3つのプレビューアイテム
        result.NewCount.Should().Be(3);
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
        skipItem.ChangesHeader.Should().Be("変更内容の詳細:");
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
}
