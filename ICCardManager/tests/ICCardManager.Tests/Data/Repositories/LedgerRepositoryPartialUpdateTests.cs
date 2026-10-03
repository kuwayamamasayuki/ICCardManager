using System;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #2212: 列を絞った履歴の更新（<see cref="LedgerRepository.UpdateSummaryAsync"/> /
/// <see cref="LedgerRepository.UpdateSummaryAndAmountsAsync"/>）が、対象の列だけを書き、
/// 他の列（他の PC が直した備考・同行者数など）を変えないことを実 DB で検証する。
/// </summary>
/// <remarks>
/// 全列の <c>UpdateAsync</c> へ退行しても、渡す値が DB と同じなら他の列は変わらず緑になる。
/// そのため検証は「渡す値を持たない」形（id と対象の列の値だけを渡す）で行い、他の列のうち日付・氏名・備考・
/// 貸出/返却日時・同行者数は既定値と異なる値で投入する（既定値のままだと、既定値で上書きする退行を見逃す。testing.md #2106）。
/// 貸出者・返却者（職員の外部キー）と貸出中フラグは既定値のまま。
/// </remarks>
public sealed class LedgerRepositoryPartialUpdateTests : IDisposable
{
    private const string TestCardIdm = "0102030405060708";

    private readonly DbContext _dbContext;
    private readonly LedgerRepository _repository;

    public LedgerRepositoryPartialUpdateTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _repository = new LedgerRepository(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>更新の対象外の列（貸出者・返却者・貸出中フラグを除く）を既定値と異なる値で投入する。</summary>
    private async Task<int> SeedLedgerAsync()
    {
        using (var lease = await _dbContext.LeaseConnectionAsync())
        using (var command = lease.Connection.CreateCommand())
        {
            command.CommandText =
                "INSERT OR IGNORE INTO ic_card (card_idm, card_type, card_number) VALUES (@idm, 'はやかけん', '001')";
            command.Parameters.AddWithValue("@idm", TestCardIdm);
            await command.ExecuteNonQueryAsync();
        }

        return await _repository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = new DateTime(2026, 2, 10),
            Summary = "鉄道（博多～天神）",
            Income = 1000,
            Expense = 260,
            Balance = 1740,
            StaffName = "博多 花子",
            Note = "領収書あり",
            LentAt = new DateTime(2026, 2, 10, 8, 30, 0),
            ReturnedAt = new DateTime(2026, 2, 10, 18, 0, 0),
            CompanionCount = 2,
        });
    }

    private static void ShouldKeepColumnsOtherThanSummaryAndAmounts(Ledger stored)
    {
        stored.CardIdm.Should().Be(TestCardIdm);
        stored.Date.Should().Be(new DateTime(2026, 2, 10));
        stored.StaffName.Should().Be("博多 花子");
        stored.Note.Should().Be("領収書あり", "他の PC が直した備考を巻き戻さない");
        stored.LentAt.Should().Be(new DateTime(2026, 2, 10, 8, 30, 0));
        stored.ReturnedAt.Should().Be(new DateTime(2026, 2, 10, 18, 0, 0));
        stored.IsLentRecord.Should().BeFalse();
        stored.CompanionCount.Should().Be(2, "他の PC が直した同行者数を巻き戻さない");
    }

    [Fact]
    public async Task UpdateSummaryAsync_摘要だけを書き他の列を変えないこと()
    {
        var id = await SeedLedgerAsync();

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            (await _repository.UpdateSummaryAsync(id, "鉄道（博多～天神 往復）", scope.Transaction)).Should().BeTrue();
            scope.Commit();
        }

        var stored = (await _repository.GetByIdAsync(id))!;
        stored.Summary.Should().Be("鉄道（博多～天神 往復）");
        stored.Income.Should().Be(1000);
        stored.Expense.Should().Be(260);
        stored.Balance.Should().Be(1740);
        ShouldKeepColumnsOtherThanSummaryAndAmounts(stored);
    }

    [Fact]
    public async Task UpdateSummaryAndAmountsAsync_摘要と金額だけを書き他の列を変えないこと()
    {
        var id = await SeedLedgerAsync();

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            (await _repository.UpdateSummaryAndAmountsAsync(id, "鉄道（博多～天神）、鉄道（天神～博多）", 0, 520, 480, scope.Transaction))
                .Should().BeTrue();
            scope.Commit();
        }

        var stored = (await _repository.GetByIdAsync(id))!;
        stored.Summary.Should().Be("鉄道（博多～天神）、鉄道（天神～博多）");
        stored.Income.Should().Be(0);
        stored.Expense.Should().Be(520);
        stored.Balance.Should().Be(480);
        ShouldKeepColumnsOtherThanSummaryAndAmounts(stored);
    }

    [Fact]
    public async Task UpdateSummaryAsync_コミットしなければ書き込みが残らないこと()
    {
        // 渡したトランザクションに参加していること（自前で確定させていないこと）
        var id = await SeedLedgerAsync();

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            (await _repository.UpdateSummaryAsync(id, "鉄道（博多～天神 往復）", scope.Transaction)).Should().BeTrue();
        }

        (await _repository.GetByIdAsync(id))!.Summary.Should().Be("鉄道（博多～天神）");
    }

    [Fact]
    public async Task UpdateSummaryAndAmountsAsync_コミットしなければ書き込みが残らないこと()
    {
        var id = await SeedLedgerAsync();

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            (await _repository.UpdateSummaryAndAmountsAsync(id, "鉄道（天神～博多）", 0, 520, 480, scope.Transaction))
                .Should().BeTrue();
        }

        var stored = (await _repository.GetByIdAsync(id))!;
        stored.Summary.Should().Be("鉄道（博多～天神）");
        stored.Expense.Should().Be(260);
    }

    [Fact]
    public async Task 対象の行が無ければfalseを返すこと()
    {
        // Issue #1753: 影響行数 0 は他の PC が削除・統合した競合
        using var scope = await _dbContext.BeginTransactionAsync();

        (await _repository.UpdateSummaryAsync(99999, "鉄道（博多～天神）", scope.Transaction)).Should().BeFalse();
        (await _repository.UpdateSummaryAndAmountsAsync(99999, "鉄道（博多～天神）", 0, 260, 740, scope.Transaction))
            .Should().BeFalse();
    }

    [Fact]
    public async Task トランザクションを渡さなければ例外とすること()
    {
        // 明細の置換と同じトランザクションで確定させる経路のためのメソッドで、単独の確定は想定しない
        var id = await SeedLedgerAsync();

        await _repository.Invoking(r => r.UpdateSummaryAsync(id, "x", null!))
            .Should().ThrowAsync<ArgumentNullException>().WithParameterName("transaction");
        await _repository.Invoking(r => r.UpdateSummaryAndAmountsAsync(id, "x", 0, 0, 0, null!))
            .Should().ThrowAsync<ArgumentNullException>().WithParameterName("transaction");
        (await _repository.GetByIdAsync(id))!.Summary.Should().Be("鉄道（博多～天神）");
    }
}
