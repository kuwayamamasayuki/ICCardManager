using System.IO;
using System.Text;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Services;

/// <summary>
/// CsvExportServiceの単体テスト
/// </summary>
public class CsvExportServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly CsvExportService _service;

    public CsvExportServiceTests()
    {
        // テスト用の一時ディレクトリを作成
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CsvExportServiceTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        // リポジトリをモック
        _cardRepositoryMock = new Mock<ICardRepository>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();

        _service = new CsvExportService(
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object);
    }

    public void Dispose()
    {
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

    #region ExportCardsAsync テスト

    /// <summary>
    /// カードのエクスポートが正常に動作することを確認
    /// </summary>
    [Fact]
    public async Task ExportCardsAsync_WithValidData_ExportsSuccessfully()
    {
        // Arrange
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001", Note = "テスト1" },
            new IcCard { CardIdm = "FEDCBA9876543210", CardType = "PASMO", CardNumber = "002", Note = null }
        };
        _cardRepositoryMock.Setup(x => x.GetAllAsync()).ReturnsAsync(cards);

        var filePath = Path.Combine(_testDirectory, "cards_export.csv");

        // Act
        var result = await _service.ExportCardsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ExportedCount.Should().Be(2);
        result.FilePath.Should().Be(filePath);
        File.Exists(filePath).Should().BeTrue();

        // ファイル内容を確認
        var lines = await Task.Run(() => File.ReadAllLines(filePath, Encoding.UTF8));
        lines.Should().HaveCount(3); // ヘッダー + 2行
        lines[0].Should().Be("カードIDm,カード種別,管理番号,備考,削除済み");
    }

    /// <summary>
    /// 削除済みカードを含むエクスポートが正常に動作することを確認
    /// </summary>
    [Fact]
    public async Task ExportCardsAsync_IncludeDeleted_ExportsDeletedCards()
    {
        // Arrange
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "Suica", CardNumber = "001", IsDeleted = false },
            new IcCard { CardIdm = "FEDCBA9876543210", CardType = "PASMO", CardNumber = "002", IsDeleted = true }
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        var filePath = Path.Combine(_testDirectory, "cards_deleted.csv");

        // Act
        var result = await _service.ExportCardsAsync(filePath, includeDeleted: true);

        // Assert
        result.Success.Should().BeTrue();
        result.ExportedCount.Should().Be(2);

        var content = await Task.Run(() => File.ReadAllText(filePath, Encoding.UTF8));
        content.Should().Contain(",0"); // 削除済み=0
        content.Should().Contain(",1"); // 削除済み=1
    }

    /// <summary>
    /// 空のカード一覧でもエクスポートできることを確認
    /// </summary>
    [Fact]
    public async Task ExportCardsAsync_EmptyList_ExportsOnlyHeader()
    {
        // Arrange
        _cardRepositoryMock.Setup(x => x.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        var filePath = Path.Combine(_testDirectory, "cards_empty.csv");

        // Act
        var result = await _service.ExportCardsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ExportedCount.Should().Be(0);

        var lines = await Task.Run(() => File.ReadAllLines(filePath, Encoding.UTF8));
        lines.Should().HaveCount(1); // ヘッダーのみ
    }

    /// <summary>
    /// 特殊文字を含むデータが正しくエスケープされることを確認
    /// </summary>
    [Fact]
    public async Task ExportCardsAsync_WithSpecialCharacters_EscapesCorrectly()
    {
        // Arrange
        var cards = new List<IcCard>
        {
            new IcCard
            {
                CardIdm = "0123456789ABCDEF",
                CardType = "Su,ica", // カンマを含む
                CardNumber = "001",
                Note = "テスト\"備考\"" // ダブルクォートを含む
            }
        };
        _cardRepositoryMock.Setup(x => x.GetAllAsync()).ReturnsAsync(cards);

        var filePath = Path.Combine(_testDirectory, "cards_special.csv");

        // Act
        var result = await _service.ExportCardsAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();

        var content = await Task.Run(() => File.ReadAllText(filePath, Encoding.UTF8));
        content.Should().Contain("\"Su,ica\""); // カンマはダブルクォートで囲む
        content.Should().Contain("\"テスト\"\"備考\"\"\""); // ダブルクォートはエスケープ
    }

    #endregion

    #region ExportStaffAsync テスト

    /// <summary>
    /// 職員のエクスポートが正常に動作することを確認
    /// </summary>
    [Fact]
    public async Task ExportStaffAsync_WithValidData_ExportsSuccessfully()
    {
        // Arrange
        var staffList = new List<Staff>
        {
            new Staff { StaffIdm = "0123456789ABCDEF", Name = "山田太郎", Number = "001", Note = "テスト職員" },
            new Staff { StaffIdm = "FEDCBA9876543210", Name = "鈴木花子", Number = "002", Note = null }
        };
        _staffRepositoryMock.Setup(x => x.GetAllAsync()).ReturnsAsync(staffList);

        var filePath = Path.Combine(_testDirectory, "staff_export.csv");

        // Act
        var result = await _service.ExportStaffAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ExportedCount.Should().Be(2);
        result.FilePath.Should().Be(filePath);
        File.Exists(filePath).Should().BeTrue();

        // ファイル内容を確認
        var lines = await Task.Run(() => File.ReadAllLines(filePath, Encoding.UTF8));
        lines.Should().HaveCount(3); // ヘッダー + 2行
        lines[0].Should().Be("職員IDm,氏名,職員番号,備考,削除済み");
    }

    /// <summary>
    /// 削除済み職員を含むエクスポートが正常に動作することを確認
    /// </summary>
    [Fact]
    public async Task ExportStaffAsync_IncludeDeleted_ExportsDeletedStaff()
    {
        // Arrange
        var staffList = new List<Staff>
        {
            new Staff { StaffIdm = "0123456789ABCDEF", Name = "山田太郎", Number = "001", IsDeleted = false },
            new Staff { StaffIdm = "FEDCBA9876543210", Name = "退職者", Number = "099", IsDeleted = true }
        };
        _staffRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(staffList);

        var filePath = Path.Combine(_testDirectory, "staff_deleted.csv");

        // Act
        var result = await _service.ExportStaffAsync(filePath, includeDeleted: true);

        // Assert
        result.Success.Should().BeTrue();
        result.ExportedCount.Should().Be(2);
    }

    #endregion

    #region ExportLedgersAsync テスト

    /// <summary>
    /// 履歴のエクスポートが正常に動作することを確認
    /// </summary>
    [Fact]
    public async Task ExportLedgersAsync_WithValidData_ExportsSuccessfully()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new Ledger
            {
                Id = 1,
                CardIdm = "0123456789ABCDEF",
                Date = new DateTime(2024, 1, 15),
                Summary = "鉄道（博多～天神）",
                Income = 0,
                Expense = 260,
                Balance = 9740,
                StaffName = "山田太郎"
            },
            new Ledger
            {
                Id = 2,
                CardIdm = "0123456789ABCDEF",
                Date = new DateTime(2024, 1, 16),
                Summary = "チャージ",
                Income = 5000,
                Expense = 0,
                Balance = 14740,
                StaffName = "山田太郎"
            }
        };

        _ledgerRepositoryMock
            .Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);

        var filePath = Path.Combine(_testDirectory, "ledgers_export.csv");

        // Act
        var result = await _service.ExportLedgersAsync(
            filePath,
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 31));

        // Assert
        result.Success.Should().BeTrue();
        result.ExportedCount.Should().Be(2);
        result.FilePath.Should().Be(filePath);

        // ファイル内容を確認
        var lines = await Task.Run(() => File.ReadAllLines(filePath, Encoding.UTF8));
        lines.Should().HaveCount(3); // ヘッダー + 2行
        lines[0].Should().Be("ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考");
    }

    /// <summary>
    /// 空の期間で履歴がない場合でもエクスポートできることを確認
    /// </summary>
    [Fact]
    public async Task ExportLedgersAsync_EmptyRange_ExportsOnlyHeader()
    {
        // Arrange
        _ledgerRepositoryMock
            .Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>());

        var filePath = Path.Combine(_testDirectory, "ledgers_empty.csv");

        // Act
        var result = await _service.ExportLedgersAsync(
            filePath,
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 31));

        // Assert
        result.Success.Should().BeTrue();
        result.ExportedCount.Should().Be(0);

        var lines = await Task.Run(() => File.ReadAllLines(filePath, Encoding.UTF8));
        lines.Should().HaveCount(1); // ヘッダーのみ
    }

    /// <summary>
    /// Issue #592: 複数カードの履歴がカード別にまとめて出力されることを確認
    /// </summary>
    [Fact]
    public async Task ExportLedgersAsync_MultipleCards_GroupsByCard()
    {
        // Arrange
        // 意図的に異なるカードのデータを日付順に混在させる
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "CARD_A_IDM_00001", Date = new DateTime(2024, 1, 10), Summary = "A-利用1", Income = 0, Expense = 300, Balance = 9700 },
            new Ledger { Id = 2, CardIdm = "CARD_B_IDM_00002", Date = new DateTime(2024, 1, 12), Summary = "B-利用1", Income = 0, Expense = 200, Balance = 4800 },
            new Ledger { Id = 3, CardIdm = "CARD_A_IDM_00001", Date = new DateTime(2024, 1, 15), Summary = "A-利用2", Income = 0, Expense = 300, Balance = 9400 },
            new Ledger { Id = 4, CardIdm = "CARD_B_IDM_00002", Date = new DateTime(2024, 1, 18), Summary = "B-利用2", Income = 0, Expense = 200, Balance = 4600 },
            new Ledger { Id = 5, CardIdm = "CARD_A_IDM_00001", Date = new DateTime(2024, 1, 20), Summary = "A-利用3", Income = 0, Expense = 300, Balance = 9100 },
        };

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "CARD_A_IDM_00001", CardType = "はやかけん", CardNumber = "001" },
            new IcCard { CardIdm = "CARD_B_IDM_00002", CardType = "nimoca", CardNumber = "002" },
        };

        _ledgerRepositoryMock
            .Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(x => x.GetAllIncludingDeletedAsync())
            .ReturnsAsync(cards);

        var filePath = Path.Combine(_testDirectory, "ledgers_grouped.csv");

        // Act
        var result = await _service.ExportLedgersAsync(
            filePath,
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 31));

        // Assert
        result.Success.Should().BeTrue();
        result.ExportedCount.Should().Be(5);

        var lines = await Task.Run(() => File.ReadAllLines(filePath, Encoding.UTF8));
        lines.Should().HaveCount(6); // ヘッダー + 5行

        // nimoca < はやかけん（Latin文字 < カタカナ）の順でソート
        // カードBの履歴（nimoca/002）が先にまとまって出力される
        lines[1].Should().Contain("CARD_B_IDM_00002");
        lines[1].Should().Contain("B-利用1");
        lines[2].Should().Contain("CARD_B_IDM_00002");
        lines[2].Should().Contain("B-利用2");

        // カードAの履歴（はやかけん/001）が後にまとまって出力される
        lines[3].Should().Contain("CARD_A_IDM_00001");
        lines[3].Should().Contain("A-利用1");
        lines[4].Should().Contain("CARD_A_IDM_00001");
        lines[4].Should().Contain("A-利用2");
        lines[5].Should().Contain("CARD_A_IDM_00001");
        lines[5].Should().Contain("A-利用3");
    }

    /// <summary>
    /// Issue #592: 同一カード内の履歴が日付順・ID順を維持することを確認
    /// </summary>
    [Fact]
    public async Task ExportLedgersAsync_MultipleCards_MaintainsChronologicalOrderWithinCard()
    {
        // Arrange
        // 同一カード内で日付が前後するデータ
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 3, CardIdm = "CARD_A_IDM_00001", Date = new DateTime(2024, 1, 20), Summary = "A-3番目", Income = 0, Expense = 100, Balance = 9700 },
            new Ledger { Id = 1, CardIdm = "CARD_A_IDM_00001", Date = new DateTime(2024, 1, 5), Summary = "A-1番目", Income = 0, Expense = 100, Balance = 9900 },
            new Ledger { Id = 2, CardIdm = "CARD_A_IDM_00001", Date = new DateTime(2024, 1, 10), Summary = "A-2番目", Income = 0, Expense = 100, Balance = 9800 },
            new Ledger { Id = 4, CardIdm = "CARD_B_IDM_00002", Date = new DateTime(2024, 1, 1), Summary = "B-1番目", Income = 0, Expense = 200, Balance = 4800 },
        };

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "CARD_A_IDM_00001", CardType = "はやかけん", CardNumber = "001" },
            new IcCard { CardIdm = "CARD_B_IDM_00002", CardType = "nimoca", CardNumber = "002" },
        };

        _ledgerRepositoryMock
            .Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(x => x.GetAllIncludingDeletedAsync())
            .ReturnsAsync(cards);

        var filePath = Path.Combine(_testDirectory, "ledgers_order.csv");

        // Act
        var result = await _service.ExportLedgersAsync(
            filePath,
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 31));

        // Assert
        result.Success.Should().BeTrue();

        var lines = await Task.Run(() => File.ReadAllLines(filePath, Encoding.UTF8));

        // nimoca < はやかけん のため、カードBが先
        lines[1].Should().Contain("B-1番目"); // カードB（nimoca）1/1

        // カードA内は日付順（1/5 → 1/10 → 1/20）
        lines[2].Should().Contain("A-1番目"); // 1/5
        lines[3].Should().Contain("A-2番目"); // 1/10
        lines[4].Should().Contain("A-3番目"); // 1/20
    }

    /// <summary>
    /// Issue #592: カードのグループ順がカード種別→管理番号順であることを確認
    /// </summary>
    [Fact]
    public async Task ExportLedgersAsync_MultipleCards_OrderedByCardTypeThenNumber()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "CARD_C_IDM_00003", Date = new DateTime(2024, 1, 10), Summary = "C-利用", Income = 0, Expense = 100, Balance = 9900 },
            new Ledger { Id = 2, CardIdm = "CARD_A_IDM_00001", Date = new DateTime(2024, 1, 10), Summary = "A-利用", Income = 0, Expense = 100, Balance = 9900 },
            new Ledger { Id = 3, CardIdm = "CARD_B_IDM_00002", Date = new DateTime(2024, 1, 10), Summary = "B-利用", Income = 0, Expense = 100, Balance = 9900 },
        };

        // カード種別→管理番号順: nimoca/001 → nimoca/002 → はやかけん/001
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "CARD_A_IDM_00001", CardType = "nimoca", CardNumber = "001" },
            new IcCard { CardIdm = "CARD_B_IDM_00002", CardType = "nimoca", CardNumber = "002" },
            new IcCard { CardIdm = "CARD_C_IDM_00003", CardType = "はやかけん", CardNumber = "001" },
        };

        _ledgerRepositoryMock
            .Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(x => x.GetAllIncludingDeletedAsync())
            .ReturnsAsync(cards);

        var filePath = Path.Combine(_testDirectory, "ledgers_card_order.csv");

        // Act
        var result = await _service.ExportLedgersAsync(
            filePath,
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 31));

        // Assert
        result.Success.Should().BeTrue();

        var lines = await Task.Run(() => File.ReadAllLines(filePath, Encoding.UTF8));

        // nimoca/001 (カードA) → nimoca/002 (カードB) → はやかけん/001 (カードC)
        lines[1].Should().Contain("A-利用");
        lines[2].Should().Contain("B-利用");
        lines[3].Should().Contain("C-利用");
    }

    #endregion

    #region エラーハンドリング テスト

    /// <summary>
    /// ファイル書き込みエラー時にエラー結果が返されることを確認
    /// </summary>
    [Fact]
    public async Task ExportCardsAsync_InvalidPath_ReturnsError()
    {
        // Arrange
        _cardRepositoryMock.Setup(x => x.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // 無効なパス
        var invalidPath = Path.Combine(_testDirectory, "nonexistent", "nested", "folder", "file.csv");

        // Act
        var result = await _service.ExportCardsAsync(invalidPath);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    #endregion
}
