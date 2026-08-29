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
        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object, DepartmentType.MayorOffice);
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

        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object, DepartmentType.MayorOffice);
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

    /// <summary>
    /// 明細は「新しい順」で <c>InsertDetailsAsync</c> へ渡されること
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1913: <c>SplitAtChargeBoundaries</c> は時系列昇順（古い→新しい）で返し、
    /// <c>InsertDetailsAsync</c> は渡された順にそのまま INSERT する。昇順のまま渡すと
    /// <c>LedgerDetail.SequenceNumber</c> の規約（FeliCa 互換で<b>小さい rowid ＝ 新しい</b>）が
    /// 反転し、以後の摘要再生成でブロック順が逆になる。
    /// </para>
    /// <para>
    /// <c>LendingService</c> の同型の挿入（1367 行付近）が既に <c>Reverse()</c> しているのに、
    /// CSV から新規 Ledger を作るこの経路だけが取り残されていた。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task BuildAndInsertAsync_明細は新しい順でInsertDetailsAsyncへ渡されること()
    {
        // Arrange - 同一日の利用 3 件（時系列昇順。残高は減っていく）
        var repoMock = new Mock<ILedgerRepository>();
        repoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(300);

        List<LedgerDetail> insertedDetails = null;
        repoMock.Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .Callback<int, IEnumerable<LedgerDetail>>((_, details) => insertedDetails = details.ToList())
            .ReturnsAsync(true);

        var useDate = new DateTime(2024, 3, 1);
        var first = Usage(useDate, amount: 260, balance: 9740);
        first.EntryStation = "博多";
        first.ExitStation = "天神";
        var second = Usage(useDate, amount: 210, balance: 9530);
        second.EntryStation = "薬院";
        second.ExitStation = "大橋";
        var third = Usage(useDate, amount: 230, balance: 9300);
        third.EntryStation = "姪浜";
        third.ExitStation = "西新";

        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object, DepartmentType.MayorOffice);
        var errors = new List<CsvImportError>();

        // Act
        var count = await builder.BuildAndInsertAsync(
            CardIdm,
            useDate,
            new List<(int LineNumber, LedgerDetail Detail)>
            {
                (LineNumber: 2, Detail: first),
                (LineNumber: 3, Detail: second),
                (LineNumber: 4, Detail: third)
            },
            errors);

        // Assert
        count.Should().Be(3);
        errors.Should().BeEmpty();
        insertedDetails.Should().NotBeNull();
        insertedDetails.Select(d => d.EntryStation).Should().Equal(
            new[] { "姪浜", "薬院", "博多" },
            "先に INSERT した明細ほど小さい rowid になるため、最新の明細から渡すこと（Issue #1913）");
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

        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object, DepartmentType.MayorOffice);
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

        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object, DepartmentType.MayorOffice);
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

        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object, DepartmentType.MayorOffice);
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
