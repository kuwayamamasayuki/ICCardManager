using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Services.Import.Builders;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services.Import;

/// <summary>
/// <see cref="NewLedgerFromSegmentsBuilder"/> の単体テスト（Issue #1284 Task 8）。
/// 利用履歴 ID 空欄の詳細行から segment 分割を伴って新規 Ledger を作成する責務を検証する。
/// </summary>
public class NewLedgerFromSegmentsBuilderTests
{
    private const string CardIdm = "0102030405060708";

    private static LedgerDetail Usage(DateTime useDate, int amount, int balance) =>
        new LedgerDetail
        {
            UseDate = useDate,
            Amount = amount,
            Balance = balance,
            EntryStation = "博多",
            ExitStation = "天神",
            IsCharge = false,
            IsPointRedemption = false,
            IsBus = false
        };

    [Fact]
    public async Task BuildAndInsertAsync_EmptyDetails_ReturnsZero()
    {
        // Arrange - 空リスト
        var repoMock = new Mock<ILedgerRepository>();
        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object);
        var errors = new List<CsvImportError>();

        // Act
        var count = await builder.BuildAndInsertAsync(
            CardIdm,
            new DateTime(2024, 1, 15),
            new List<(int LineNumber, LedgerDetail Detail)>(),
            errors);

        // Assert
        count.Should().Be(0);
        errors.Should().BeEmpty();
        repoMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
        repoMock.Verify(
            r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()),
            Times.Never);
    }

    [Fact]
    public async Task BuildAndInsertAsync_SingleUsageSegment_CreatesOneLedger()
    {
        // Arrange - 通常利用 1 件
        var repoMock = new Mock<ILedgerRepository>();
        repoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(100);
        repoMock.Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object);
        var errors = new List<CsvImportError>();
        var detail = Usage(new DateTime(2024, 3, 1, 8, 0, 0), amount: 260, balance: 9740);

        // Act
        var count = await builder.BuildAndInsertAsync(
            CardIdm,
            new DateTime(2024, 3, 1),
            new List<(int LineNumber, LedgerDetail Detail)> { (LineNumber: 5, Detail: detail) },
            errors);

        // Assert
        count.Should().Be(1);
        errors.Should().BeEmpty();
        repoMock.Verify(r => r.InsertAsync(It.Is<Ledger>(
            l => l.CardIdm == CardIdm && l.Date == new DateTime(2024, 3, 1))),
            Times.Once);
        repoMock.Verify(
            r => r.InsertDetailsAsync(100, It.IsAny<IEnumerable<LedgerDetail>>()),
            Times.Once);
    }

    [Fact]
    public async Task BuildAndInsertAsync_GroupDateMinValue_UsesDetailUseDate()
    {
        // Arrange - groupDate が MinValue のときは detail.UseDate（最古）を Ledger.Date に採用
        var repoMock = new Mock<ILedgerRepository>();
        Ledger insertedLedger = null;
        repoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => insertedLedger = l)
            .ReturnsAsync(200);
        repoMock.Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object);
        var errors = new List<CsvImportError>();

        var earlier = Usage(new DateTime(2024, 5, 10, 8, 0, 0), amount: 260, balance: 9740);
        var later = Usage(new DateTime(2024, 5, 10, 18, 0, 0), amount: 260, balance: 9480);

        // Act
        var count = await builder.BuildAndInsertAsync(
            CardIdm,
            DateTime.MinValue, // 明示的に未指定
            new List<(int LineNumber, LedgerDetail Detail)>
            {
                (LineNumber: 10, Detail: earlier),
                (LineNumber: 11, Detail: later)
            },
            errors);

        // Assert
        count.Should().Be(2);
        errors.Should().BeEmpty();
        insertedLedger.Should().NotBeNull();
        insertedLedger.Date.Should().Be(new DateTime(2024, 5, 10, 8, 0, 0)); // 最古の UseDate
    }

    [Fact]
    public async Task BuildAndInsertAsync_InsertDetailsFails_AddsError()
    {
        // Arrange - InsertDetailsAsync が false を返す
        var repoMock = new Mock<ILedgerRepository>();
        repoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(300);
        repoMock.Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(false);

        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object);
        var errors = new List<CsvImportError>();
        var detail = Usage(new DateTime(2024, 6, 1, 8, 0, 0), amount: 260, balance: 9740);

        // Act
        var count = await builder.BuildAndInsertAsync(
            CardIdm,
            new DateTime(2024, 6, 1),
            new List<(int LineNumber, LedgerDetail Detail)> { (LineNumber: 20, Detail: detail) },
            errors);

        // Assert
        count.Should().Be(0);
        errors.Should().ContainSingle();
        errors[0].LineNumber.Should().Be(20);
        errors[0].Message.Should().Contain(CardIdm).And.Contain("挿入に失敗");
    }

    [Fact]
    public async Task BuildAndInsertAsync_RepositoryThrows_AddsError()
    {
        // Arrange - InsertAsync が例外を投げる
        var repoMock = new Mock<ILedgerRepository>();
        var boomMessage = "DB connection lost";
        repoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .ThrowsAsync(new InvalidOperationException(boomMessage));

        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object);
        var errors = new List<CsvImportError>();
        var detail = Usage(new DateTime(2024, 7, 1, 8, 0, 0), amount: 260, balance: 9740);

        // Act
        var count = await builder.BuildAndInsertAsync(
            CardIdm,
            new DateTime(2024, 7, 1),
            new List<(int LineNumber, LedgerDetail Detail)> { (LineNumber: 30, Detail: detail) },
            errors);

        // Assert
        count.Should().Be(0);
        errors.Should().ContainSingle();
        errors[0].LineNumber.Should().Be(30);
        errors[0].Message.Should().Contain(CardIdm).And.Contain("自動作成中にエラー").And.Contain(boomMessage);
    }
}
