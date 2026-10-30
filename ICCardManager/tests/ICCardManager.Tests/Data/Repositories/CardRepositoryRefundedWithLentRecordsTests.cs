using System;
using System.Collections.Generic;
using System.Linq;
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
/// Issue #2322: 貸出中の記録が残る払戻済のカードを引く専用のクエリ
/// （<see cref="CardRepository.GetRefundedWithLentRecordsAsync"/>）を実 DB（インメモリ SQLite）で検証する。
/// </summary>
/// <remarks>
/// <para>
/// メイン画面の警告（<c>WarningType.RefundedCardLent</c>）の母集団そのもの。貸出中カード一覧を読み直すたび
/// （共有モードでは 15 秒ごと）に呼ばれるため、台帳を全件読まずに払戻済のカードから引く。
/// </para>
/// <para>
/// 「拾う側」（<c>is_lent</c> が 1 でも 0 でも、貸出中レコードが残る払戻済のカード）と「拾わない側」
/// （払戻済でない・貸出中レコードが無い・貸出中でない行だけがある・削除済み）を対で置く。
/// </para>
/// </remarks>
public sealed class CardRepositoryRefundedWithLentRecordsTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly CardRepository _repository;
    private readonly LedgerRepository _ledgerRepository;

    public CardRepositoryRefundedWithLentRecordsTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<IcCard>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan _) => factory());
        _repository = new CardRepository(
            _dbContext, cacheService.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        _ledgerRepository = new LedgerRepository(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task 貸出中の記録が残る払戻済のカードをis_lentによらず返し_それ以外は返さないこと()
    {
        // Arrange
        await SeedAsync("0000000000000001", refunded: true, isLent: true, lentRecord: true);    // 拾う
        await SeedAsync("0000000000000002", refunded: true, isLent: false, lentRecord: true);   // 拾う（is_lent = 0）
        await SeedAsync("0000000000000003", refunded: false, isLent: true, lentRecord: true);   // 払戻済でない
        await SeedAsync("0000000000000004", refunded: true, isLent: false, lentRecord: false);  // 貸出中レコードが無い
        await SeedAsync("0000000000000005", refunded: true, isLent: false, lentRecord: false, usageRecord: true); // 利用の行だけ
        await SeedAsync("0000000000000006", refunded: true, isLent: true, lentRecord: true, deleted: true);       // 削除済み

        // Act
        var cards = await _repository.GetRefundedWithLentRecordsAsync();

        // Assert
        cards.Select(c => c.CardIdm).Should().Equal("0000000000000001", "0000000000000002");
        cards[0].CardType.Should().Be("はやかけん", "警告の文言に使う列を読み出す");
        cards[0].CardNumber.Should().Be("H-0000000000000001");
        cards.Should().OnlyContain(c => c.IsRefunded);
    }

    [Fact]
    public async Task 対象が無ければ空を返すこと()
    {
        await SeedAsync("0000000000000003", refunded: false, isLent: true, lentRecord: true);

        var cards = await _repository.GetRefundedWithLentRecordsAsync();

        cards.Should().BeEmpty();
    }

    /// <summary>
    /// 台帳は card_idm を先頭に持つインデックスで引き、全件走査しないこと（EXPLAIN QUERY PLAN）
    /// </summary>
    [Fact]
    public async Task 台帳をインデックスで引き全件走査しないこと()
    {
        var plan = new List<string>();
        using (var lease = await _dbContext.LeaseConnectionAsync())
        using (var command = lease.Connection.CreateCommand())
        {
            command.CommandText = "EXPLAIN QUERY PLAN " + CardRepository.RefundedWithLentRecordsSql;
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(reader.GetOrdinal("detail")));
            }
        }

        plan.Should().Contain(d => d.Contains("SEARCH l USING") && d.Contains("card_idm=?"),
            $"台帳は card_idm のインデックスで引く（実際の計画: {string.Join(" / ", plan)}）");
        plan.Should().NotContain(d => d.StartsWith("SCAN l", StringComparison.Ordinal),
            "台帳を全件走査しない");
    }

    private async Task SeedAsync(
        string idm, bool refunded, bool isLent, bool lentRecord, bool usageRecord = false, bool deleted = false)
    {
        await _repository.InsertAsync(new IcCard { CardIdm = idm, CardType = "はやかけん", CardNumber = "H-" + idm });

        using (var lease = await _dbContext.LeaseConnectionAsync())
        using (var command = lease.Connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE ic_card SET is_refunded = @refunded, is_lent = @isLent, is_deleted = @deleted WHERE card_idm = @idm";
            command.Parameters.AddWithValue("@idm", idm);
            command.Parameters.AddWithValue("@refunded", refunded ? 1 : 0);
            command.Parameters.AddWithValue("@isLent", isLent ? 1 : 0);
            command.Parameters.AddWithValue("@deleted", deleted ? 1 : 0);
            await command.ExecuteNonQueryAsync();
        }

        var date = new DateTime(2025, 3, 18, 9, 0, 0);
        if (lentRecord)
        {
            await _ledgerRepository.InsertAsync(new Ledger
            {
                CardIdm = idm,
                Date = date,
                Summary = "（貸出中）",
                Balance = 1000,
                LentAt = date,
                IsLentRecord = true,
            });
        }

        if (usageRecord)
        {
            await _ledgerRepository.InsertAsync(new Ledger
            {
                CardIdm = idm,
                Date = date.Date,
                Summary = "鉄道（天神～博多）",
                Expense = 210,
                Balance = 790,
            });
        }
    }
}
