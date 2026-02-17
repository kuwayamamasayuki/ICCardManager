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
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(999)).ReturnsAsync((Ledger)null);

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
}
