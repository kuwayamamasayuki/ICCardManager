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

    /// <summary>
    /// Issue #1004: 同一日内のポイント還元と利用が残高チェーン順で出力されることを確認
    /// </summary>
    [Fact]
    public async Task ExportLedgersAsync_SameDatePointRedemptionAndUsage_OrderedByBalanceChain()
    {
        // Arrange - 3/10に利用(1876→1456)、その後ポイント還元(1456→1696)
        // IDはポイント還元が先（小さい）だが、残高チェーンでは利用が先
        var ledgers = new List<Ledger>
        {
            new Ledger
            {
                Id = 15,
                CardIdm = "01010212CC0C2A1F",
                Date = new DateTime(2026, 3, 9),
                Summary = "鉄道（薬院～博多 往復）",
                Income = 0,
                Expense = 420,
                Balance = 1876,
                StaffName = "桑山　雅行"
            },
            new Ledger
            {
                Id = 16,
                CardIdm = "01010212CC0C2A1F",
                Date = new DateTime(2026, 3, 10),
                Summary = "ポイント還元",
                Income = 240,
                Expense = 0,
                Balance = 1696
            },
            new Ledger
            {
                Id = 17,
                CardIdm = "01010212CC0C2A1F",
                Date = new DateTime(2026, 3, 10),
                Summary = "鉄道（薬院～博多 往復）",
                Income = 0,
                Expense = 420,
                Balance = 1456,
                StaffName = "桑山　雅行"
            }
        };

        _ledgerRepositoryMock
            .Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);

        var filePath = Path.Combine(_testDirectory, "ledgers_balance_chain.csv");

        // Act
        var result = await _service.ExportLedgersAsync(
            filePath,
            new DateTime(2026, 3, 1),
            new DateTime(2026, 3, 31));

        // Assert
        result.Success.Should().BeTrue();

        var lines = await Task.Run(() => File.ReadAllLines(filePath, Encoding.UTF8));
        lines.Should().HaveCount(4); // ヘッダー + 3行

        // 3/10のデータ: 利用(ID:17)が先、ポイント還元(ID:16)が後
        // 残高チェーン: 1876 → 1456(利用-420) → 1696(還元+240)
        lines[2].Should().Contain("鉄道（薬院～博多 往復）", "利用がポイント還元より先に出力されるべき");
        lines[3].Should().Contain("ポイント還元", "ポイント還元は利用の後に出力されるべき");

        // 残高順も確認
        lines[2].Should().Contain(",1456,", "利用後の残高は1456円");
        lines[3].Should().Contain(",1696,", "ポイント還元後の残高は1696円");
    }

    /// <summary>
    /// Issue #1004: 同一日内のチャージと利用も残高チェーン順で出力されることを確認
    /// </summary>
    [Fact]
    public async Task ExportLedgersAsync_SameDateChargeAndUsage_OrderedByBalanceChain()
    {
        // Arrange - 3/4にチャージ(1726→2726)、その後利用(2726→2306)
        var ledgers = new List<Ledger>
        {
            new Ledger
            {
                Id = 8,
                CardIdm = "01010212CC0C2A1F",
                Date = new DateTime(2026, 3, 4),
                Summary = "役務費によりチャージ",
                Income = 1000,
                Expense = 0,
                Balance = 2726
            },
            new Ledger
            {
                Id = 9,
                CardIdm = "01010212CC0C2A1F",
                Date = new DateTime(2026, 3, 4),
                Summary = "鉄道（薬院～博多 往復）",
                Income = 0,
                Expense = 420,
                Balance = 2306
            }
        };

        _ledgerRepositoryMock
            .Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);

        var filePath = Path.Combine(_testDirectory, "ledgers_charge_usage.csv");

        // Act
        var result = await _service.ExportLedgersAsync(
            filePath,
            new DateTime(2026, 3, 1),
            new DateTime(2026, 3, 31));

        // Assert
        result.Success.Should().BeTrue();

        var lines = await Task.Run(() => File.ReadAllLines(filePath, Encoding.UTF8));
        lines.Should().HaveCount(3); // ヘッダー + 2行

        // チャージが先、利用が後（残高チェーン: 1726→2726→2306）
        lines[1].Should().Contain("役務費によりチャージ");
        lines[2].Should().Contain("鉄道（薬院～博多 往復）");
    }

    #endregion

    #region ExportLedgerDetailsAsync テスト (Issue #751)

    /// <summary>
    /// 利用履歴詳細のエクスポートが正常に動作することを確認
    /// </summary>
    [Fact]
    public async Task ExportLedgerDetailsAsync_正常データ_CSVが出力される()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            // Issue #904: FeliCa互換のSequenceNumber（小さいほど新しい）
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
                IsBus = false,
                SequenceNumber = 2 // 古い取引 → 大きいrowid
            },
            new LedgerDetail
            {
                LedgerId = 1,
                UseDate = new DateTime(2024, 1, 15, 17, 0, 0),
                EntryStation = "天神",
                ExitStation = "博多",
                Amount = 260,
                Balance = 9480,
                IsCharge = false,
                IsPointRedemption = false,
                IsBus = false,
                SequenceNumber = 1 // 新しい取引 → 小さいrowid
            }
        };

        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15) }
        };

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };

        _ledgerRepositoryMock
            .Setup(x => x.GetAllDetailsInDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(details);
        _ledgerRepositoryMock
            .Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(x => x.GetAllIncludingDeletedAsync())
            .ReturnsAsync(cards);

        var filePath = Path.Combine(_testDirectory, "ledger_details_export.csv");

        // Act
        var result = await _service.ExportLedgerDetailsAsync(
            filePath,
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 31));

        // Assert
        result.Success.Should().BeTrue();
        result.ExportedCount.Should().Be(2);
        result.FilePath.Should().Be(filePath);

        var lines = await Task.Run(() => File.ReadAllLines(filePath, Encoding.UTF8));
        lines.Should().HaveCount(3); // ヘッダー + 2行
        lines[0].Should().Be("利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID");

        // 1行目のデータを検証
        lines[1].Should().Contain("1,2024-01-15 10:30:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,");
    }

    /// <summary>
    /// 詳細が0件の場合、ヘッダーのみ出力されることを確認
    /// </summary>
    [Fact]
    public async Task ExportLedgerDetailsAsync_詳細なしのledger_ヘッダーのみ()
    {
        // Arrange
        _ledgerRepositoryMock
            .Setup(x => x.GetAllDetailsInDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<LedgerDetail>());
        _ledgerRepositoryMock
            .Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>());
        _cardRepositoryMock
            .Setup(x => x.GetAllIncludingDeletedAsync())
            .ReturnsAsync(new List<IcCard>());

        var filePath = Path.Combine(_testDirectory, "ledger_details_empty.csv");

        // Act
        var result = await _service.ExportLedgerDetailsAsync(
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
    /// NULL値が空欄で出力されることを確認
    /// </summary>
    [Fact]
    public async Task ExportLedgerDetailsAsync_NULL値_空欄で出力()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                LedgerId = 1,
                UseDate = null,
                EntryStation = null,
                ExitStation = null,
                BusStops = null,
                Amount = null,
                Balance = null,
                IsCharge = false,
                IsPointRedemption = false,
                IsBus = false,
                GroupId = null,
                SequenceNumber = 1
            }
        };

        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15) }
        };
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };

        _ledgerRepositoryMock
            .Setup(x => x.GetAllDetailsInDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(details);
        _ledgerRepositoryMock
            .Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(x => x.GetAllIncludingDeletedAsync())
            .ReturnsAsync(cards);

        var filePath = Path.Combine(_testDirectory, "ledger_details_null.csv");

        // Act
        var result = await _service.ExportLedgerDetailsAsync(
            filePath,
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 31));

        // Assert
        result.Success.Should().BeTrue();

        var lines = await Task.Run(() => File.ReadAllLines(filePath, Encoding.UTF8));
        // 1,,0123456789ABCDEF,001,,,,,,,0,0,0,
        lines[1].Should().Be("1,,0123456789ABCDEF,001,,,,,,0,0,0,");
    }

    /// <summary>
    /// ブール値が0と1で出力されることを確認
    /// </summary>
    [Fact]
    public async Task ExportLedgerDetailsAsync_ブール値_0と1で出力()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            // Issue #904: FeliCa互換のSequenceNumber（小さいほど新しい）
            new LedgerDetail
            {
                LedgerId = 1,
                UseDate = new DateTime(2024, 1, 15),
                Amount = 1000,
                Balance = 11000,
                IsCharge = true,
                IsPointRedemption = false,
                IsBus = false,
                SequenceNumber = 3 // 最古
            },
            new LedgerDetail
            {
                LedgerId = 1,
                UseDate = new DateTime(2024, 1, 15),
                IsCharge = false,
                IsPointRedemption = true,
                IsBus = false,
                SequenceNumber = 2 // 中間
            },
            new LedgerDetail
            {
                LedgerId = 1,
                UseDate = new DateTime(2024, 1, 15),
                BusStops = "天神",
                IsCharge = false,
                IsPointRedemption = false,
                IsBus = true,
                SequenceNumber = 1 // 最新
            }
        };

        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15) }
        };
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };

        _ledgerRepositoryMock
            .Setup(x => x.GetAllDetailsInDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(details);
        _ledgerRepositoryMock
            .Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(x => x.GetAllIncludingDeletedAsync())
            .ReturnsAsync(cards);

        var filePath = Path.Combine(_testDirectory, "ledger_details_bool.csv");

        // Act
        var result = await _service.ExportLedgerDetailsAsync(
            filePath,
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 31));

        // Assert
        result.Success.Should().BeTrue();

        var lines = await Task.Run(() => File.ReadAllLines(filePath, Encoding.UTF8));
        // IsCharge=true → "1"
        lines[1].Should().Contain(",1,0,0,");
        // IsPointRedemption=true → "1"
        lines[2].Should().Contain(",0,1,0,");
        // IsBus=true → "1"
        lines[3].Should().Contain(",0,0,1,");
    }

    /// <summary>
    /// 特殊文字を含む駅名が正しくエスケープされることを確認
    /// </summary>
    [Fact]
    public async Task ExportLedgerDetailsAsync_特殊文字_エスケープされる()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                LedgerId = 1,
                UseDate = new DateTime(2024, 1, 15),
                EntryStation = "新宿,東口",  // カンマを含む
                ExitStation = "渋谷\"駅\"",  // ダブルクォートを含む
                Amount = 200,
                Balance = 800,
                IsCharge = false,
                IsPointRedemption = false,
                IsBus = false,
                SequenceNumber = 1
            }
        };

        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15) }
        };
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };

        _ledgerRepositoryMock
            .Setup(x => x.GetAllDetailsInDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(details);
        _ledgerRepositoryMock
            .Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _cardRepositoryMock
            .Setup(x => x.GetAllIncludingDeletedAsync())
            .ReturnsAsync(cards);

        var filePath = Path.Combine(_testDirectory, "ledger_details_special.csv");

        // Act
        var result = await _service.ExportLedgerDetailsAsync(
            filePath,
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 31));

        // Assert
        result.Success.Should().BeTrue();

        var content = await Task.Run(() => File.ReadAllText(filePath, Encoding.UTF8));
        content.Should().Contain("\"新宿,東口\"");  // カンマはダブルクォートで囲む
        content.Should().Contain("\"渋谷\"\"駅\"\"\"");  // ダブルクォートはエスケープ
    }

    /// <summary>
    /// 利用履歴詳細エクスポート時のSequenceNumber順序が残高整合性を保つことを確認（Issue #904）
    /// FeliCa互換: SequenceNumber降順（大→小）で古い取引から順に出力される
    /// </summary>
    [Fact]
    public async Task ExportLedgerDetailsAsync_SequenceNumber降順で時系列順に出力される()
    {
        // Arrange: SequenceNumberがFeliCa互換（小さいほど新しい）のデータ
        // 時系列: 10:30(古,SeqNum=3) → 12:00(中,SeqNum=2) → 17:00(新,SeqNum=1)
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                LedgerId = 1,
                UseDate = new DateTime(2024, 1, 15, 17, 0, 0),
                EntryStation = "天神",
                ExitStation = "博多",
                Amount = 260,
                Balance = 9220,
                SequenceNumber = 1 // 最新
            },
            new LedgerDetail
            {
                LedgerId = 1,
                UseDate = new DateTime(2024, 1, 15, 10, 30, 0),
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 260,
                Balance = 9740,
                SequenceNumber = 3 // 最古
            },
            new LedgerDetail
            {
                LedgerId = 1,
                UseDate = new DateTime(2024, 1, 15, 12, 0, 0),
                EntryStation = "天神",
                ExitStation = "薬院",
                Amount = 260,
                Balance = 9480,
                SequenceNumber = 2 // 中間
            }
        };

        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = "0123456789ABCDEF", Date = new DateTime(2024, 1, 15) }
        };
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" }
        };

        _ledgerRepositoryMock.Setup(x => x.GetAllDetailsInDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>())).ReturnsAsync(details);
        _ledgerRepositoryMock.Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>())).ReturnsAsync(ledgers);
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        var filePath = Path.Combine(_testDirectory, "ledger_details_order.csv");

        // Act
        var result = await _service.ExportLedgerDetailsAsync(filePath, new DateTime(2024, 1, 1), new DateTime(2024, 1, 31));

        // Assert: 残高が降順（9740→9480→9220）＝時系列順になること
        result.Success.Should().BeTrue();
        var lines = await Task.Run(() => File.ReadAllLines(filePath, Encoding.UTF8));
        lines.Should().HaveCount(4); // ヘッダー + 3行

        // SequenceNumber=3（最古、10:30、残高9740）が最初
        lines[1].Should().Contain("2024-01-15 10:30:00");
        lines[1].Should().Contain(",9740,");

        // SequenceNumber=2（中間、12:00、残高9480）が2番目
        lines[2].Should().Contain("2024-01-15 12:00:00");
        lines[2].Should().Contain(",9480,");

        // SequenceNumber=1（最新、17:00、残高9220）が最後
        lines[3].Should().Contain("2024-01-15 17:00:00");
        lines[3].Should().Contain(",9220,");
    }

    /// <summary>
    /// Issue #964: SequenceNumber順と残高チェーン順が異なる場合でも
    /// 残高チェーンに基づく正しい時系列順で出力されることを確認
    /// （FeliCa循環バッファ境界の最古部分で発生）
    /// </summary>
    [Fact]
    public async Task ExportLedgerDetailsAsync_SequenceNumber順が残高チェーンと異なる場合_残高チェーン順で出力される()
    {
        // Arrange: Issue #964の再現データ
        // 正しい時系列: 薬院→博多(1526) → 博多→薬院(210円支払い, 1316)
        // だがSequenceNumberは逆順（FeliCa循環バッファ境界で発生）
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                LedgerId = 2,
                UseDate = new DateTime(2026, 3, 2),
                EntryStation = "博多",
                ExitStation = "薬院",
                Amount = 210,
                Balance = 1316,
                IsCharge = false,
                IsPointRedemption = false,
                IsBus = false,
                SequenceNumber = 20 // FeliCa上では大きいrowid（古いはず）だが実際は新しい
            },
            new LedgerDetail
            {
                LedgerId = 2,
                UseDate = new DateTime(2026, 3, 2),
                EntryStation = "薬院",
                ExitStation = "博多",
                Amount = 0,
                Balance = 1526,
                IsCharge = false,
                IsPointRedemption = false,
                IsBus = false,
                SequenceNumber = 1 // FeliCa上では小さいrowid（新しいはず）だが実際は古い
            }
        };

        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 2, CardIdm = "01010212CC0C2A1F", Date = new DateTime(2026, 3, 2) }
        };
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "01010212CC0C2A1F", CardType = "nimoca", CardNumber = "5042" }
        };

        _ledgerRepositoryMock.Setup(x => x.GetAllDetailsInDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>())).ReturnsAsync(details);
        _ledgerRepositoryMock.Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>())).ReturnsAsync(ledgers);
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);

        var filePath = Path.Combine(_testDirectory, "ledger_details_964.csv");

        // Act
        var result = await _service.ExportLedgerDetailsAsync(filePath, new DateTime(2026, 3, 1), new DateTime(2026, 3, 31));

        // Assert: 残高チェーン順（1526→1316）で出力される
        result.Success.Should().BeTrue();
        var lines = await Task.Run(() => File.ReadAllLines(filePath, Encoding.UTF8));
        lines.Should().HaveCount(3); // ヘッダー + 2行

        // 1行目: 薬院→博多（残額1526、時系列的に先）
        lines[1].Should().Contain(",薬院,博多,");
        lines[1].Should().Contain(",1526,");

        // 2行目: 博多→薬院（残額1316、時系列的に後）
        lines[2].Should().Contain(",博多,薬院,");
        lines[2].Should().Contain(",1316,");
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
