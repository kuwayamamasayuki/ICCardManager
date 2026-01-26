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
        lines[0].Should().Be("日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考");
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
