using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #2255: 繰越情報だけを書き換える専用の UPDATE（<see cref="CardRepository.UpdateCarryoverInfoAsync"/>）を
/// 実 DB（インメモリ SQLite）の行で検証する。
/// </summary>
/// <remarks>
/// <para>
/// 「書き換える側」（4 列が書き込まれる・年度の NULL を扱える）と「書き換えない側」（他の列に触れない・
/// 読んだ値から変わっていたら書かない・削除済みは書かない）を対で置く。後者が無いと、WHERE 句を
/// <c>card_idm</c> だけにした実装（2 台の PC が続けて復旧したとき後勝ちで黙って上書きする）でも緑になる。
/// </para>
/// </remarks>
public sealed class CardRepositoryCarryoverInfoTests : IDisposable
{
    private const string TestCardIdm = "07FE112233445566";

    private readonly DbContext _dbContext;
    private readonly CardRepository _repository;

    public CardRepositoryCarryoverInfoTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<IcCard>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan _) => factory());
        _repository = new CardRepository(
            _dbContext, cacheService.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task 読んだ値のままなら_繰越情報の4列を書き換えること()
    {
        // Arrange: 消失した状態（既定値 1 / 0 / 0 / NULL）
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));
        var replacement = new CarryoverInfo(7, 45000, 37500, 2025);

        // Act
        var updated = await UpdateInTransactionAsync(new CarryoverInfo(1, 0, 0, null), replacement);

        // Assert
        updated.Should().BeTrue();
        var card = await _repository.GetByIdmAsync(TestCardIdm);
        CarryoverInfo.From(card!).Should().Be(replacement);
    }

    [Fact]
    public async Task 繰越情報以外の列には触れないこと()
    {
        // Arrange: 種別・管理番号・備考・貸出状態を持つカード
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null), note: "引き出し2段目");
        var lentAt = new DateTime(2026, 9, 1, 9, 30, 0);
        (await _repository.UpdateLentStatusAsync(TestCardIdm, true, lentAt, null)).Should().BeTrue();

        // Act
        (await UpdateInTransactionAsync(new CarryoverInfo(1, 0, 0, null), new CarryoverInfo(7, 45000, 37500, 2025)))
            .Should().BeTrue();

        // Assert
        var card = (await _repository.GetByIdmAsync(TestCardIdm))!;
        card.CardType.Should().Be("はやかけん");
        card.CardNumber.Should().Be("H-001");
        card.Note.Should().Be("引き出し2段目");
        card.IsLent.Should().BeTrue("SET 句は繰越情報の 4 列だけ（Issue #1726）");
        card.LastLentAt.Should().Be(lentAt);
    }

    [Fact]
    public async Task 対象年度がNULLのカードも_読んだ値と一致すれば書き換えること()
    {
        // 「carryover_fiscal_year = @expected」と書くと NULL = NULL は偽になり、
        // 年度が未設定のカード（消失したカードの典型）を一度も復旧できない。
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));

        var updated = await UpdateInTransactionAsync(new CarryoverInfo(1, 0, 0, null), new CarryoverInfo(5, 0, 0, null));

        updated.Should().BeTrue();
        (await _repository.GetByIdmAsync(TestCardIdm))!.StartingPageNumber.Should().Be(5);
    }

    [Fact]
    public async Task 繰越情報の列がNULLのカードも_既定値として読んだ値と一致すれば書き換えること()
    {
        // 3 列は NOT NULL ではなく、読み取りは NULL を既定値（1 / 0）として読む。DB を直接修正した等で
        // NULL が入ったカードは一覧に既定値で出るので、比較も同じ解釈でないと二度と復旧できない
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));
        using (var lease = await _dbContext.LeaseConnectionAsync())
        using (var command = lease.Connection.CreateCommand())
        {
            command.CommandText = @"UPDATE ic_card SET starting_page_number = NULL, carryover_income_total = NULL,
carryover_expense_total = NULL WHERE card_idm = @idm";
            command.Parameters.AddWithValue("@idm", TestCardIdm);
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }

        var current = CarryoverInfo.From((await _repository.GetByIdmAsync(TestCardIdm))!);
        current.Should().Be(new CarryoverInfo(1, 0, 0, null), "前提: NULL は既定値として読まれる");

        var updated = await UpdateInTransactionAsync(current, new CarryoverInfo(7, 45000, 37500, 2025));

        updated.Should().BeTrue();
        CarryoverInfo.From((await _repository.GetByIdmAsync(TestCardIdm))!).Should().Be(new CarryoverInfo(7, 45000, 37500, 2025));
    }

    [Fact]
    public async Task 対象年度をNULLへ書き換えられること()
    {
        await SeedCardAsync(new CarryoverInfo(3, 0, 0, 2024));

        var updated = await UpdateInTransactionAsync(new CarryoverInfo(3, 0, 0, 2024), new CarryoverInfo(3, 0, 0, null));

        updated.Should().BeTrue();
        (await _repository.GetByIdmAsync(TestCardIdm))!.CarryoverFiscalYear.Should().BeNull();
    }

    [Theory]
    [InlineData(2, 0, 0, null)]      // 開始ページ番号が変わっていた
    [InlineData(1, 1000, 0, null)]   // 繰越累計受入が変わっていた
    [InlineData(1, 0, 1000, null)]   // 繰越累計払出が変わっていた
    [InlineData(1, 0, 0, 2025)]      // 対象年度が変わっていた（NULL → 値）
    public async Task 読んだ値から変わっていたら_falseを返し書き換えないこと(
        int page, int income, int expense, int? fiscalYear)
    {
        // Arrange: DB は (page, income, expense, fiscalYear)、呼び出し元は既定値を読んだつもりでいる
        // （他のパソコンが先に復旧した等）
        var current = new CarryoverInfo(page, income, expense, fiscalYear);
        await SeedCardAsync(current);

        // Act
        var updated = await UpdateInTransactionAsync(new CarryoverInfo(1, 0, 0, null), new CarryoverInfo(9, 9, 9, 2020));

        // Assert
        updated.Should().BeFalse("見ていない値を黙って上書きしない");
        CarryoverInfo.From((await _repository.GetByIdmAsync(TestCardIdm))!).Should().Be(current);
    }

    [Fact]
    public async Task 対象年度が値からNULLへ変わっていたら_falseを返すこと()
    {
        // IS 比較の向きの対（値 → NULL）。読んだ値が 2024、DB は NULL
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));

        var updated = await UpdateInTransactionAsync(new CarryoverInfo(1, 0, 0, 2024), new CarryoverInfo(7, 0, 0, 2025));

        updated.Should().BeFalse();
        (await _repository.GetByIdmAsync(TestCardIdm))!.StartingPageNumber.Should().Be(1);
    }

    [Fact]
    public async Task 削除済みのカードは_falseを返し書き換えないこと()
    {
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));
        (await _repository.DeleteAsync(TestCardIdm)).Should().Be(CardOperationResult.Success);

        var updated = await UpdateInTransactionAsync(new CarryoverInfo(1, 0, 0, null), new CarryoverInfo(7, 0, 0, null));

        updated.Should().BeFalse();
        (await _repository.GetByIdmAsync(TestCardIdm, includeDeleted: true))!.StartingPageNumber.Should().Be(1);
    }

    [Fact]
    public async Task トランザクションを巻き戻せば_書き換えも残らないこと()
    {
        // 監査ログと同じトランザクションで書く前提（CardManagementService）。渡した tx を使っていなければ
        // autocommit で確定し、巻き戻しても残る。
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            (await _repository.UpdateCarryoverInfoAsync(
                TestCardIdm, new CarryoverInfo(1, 0, 0, null), new CarryoverInfo(7, 45000, 0, 2025), scope.Transaction))
                .Should().BeTrue();
            scope.Rollback();
        }

        CarryoverInfo.From((await _repository.GetByIdmAsync(TestCardIdm))!).Should().Be(new CarryoverInfo(1, 0, 0, null));
    }

    private async Task<bool> UpdateInTransactionAsync(CarryoverInfo expected, CarryoverInfo replacement)
    {
        using var scope = await _dbContext.BeginTransactionAsync();
        var updated = await _repository.UpdateCarryoverInfoAsync(TestCardIdm, expected, replacement, scope.Transaction);
        scope.Commit();
        return updated;
    }

    private async Task SeedCardAsync(CarryoverInfo carryover, string? note = null)
    {
        (await _repository.InsertAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            Note = note,
            StartingPageNumber = carryover.StartingPageNumber,
            CarryoverIncomeTotal = carryover.CarryoverIncomeTotal,
            CarryoverExpenseTotal = carryover.CarryoverExpenseTotal,
            CarryoverFiscalYear = carryover.CarryoverFiscalYear,
        })).Should().BeTrue();
    }
}
