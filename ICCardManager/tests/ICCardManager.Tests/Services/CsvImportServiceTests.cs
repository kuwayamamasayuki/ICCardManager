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
}
