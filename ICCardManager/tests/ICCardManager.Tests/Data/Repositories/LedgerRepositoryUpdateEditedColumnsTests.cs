#nullable enable
using System;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #2375: 履歴の行編集の「変えた列だけを書く」更新（<see cref="LedgerRepository.UpdateEditedColumnsAsync"/>）が、
/// 指定した列だけを書き、他の列を変えないことを実 DB で検証する。
/// </summary>
/// <remarks>
/// 渡す <see cref="Ledger"/> は<b>すべての列を DB と異なる値</b>にする。指定していない列まで SET する退行（全列の更新）は、
/// 渡す値が DB と同じだと他の列が変わらず緑になるため（#2212 の <c>LedgerRepositoryPartialUpdateTests</c> と同じ考え方）。
/// </remarks>
public sealed class LedgerRepositoryUpdateEditedColumnsTests : IDisposable
{
    private const string TestCardIdm = "0102030405060708";
    private const string StaffXIdm = "1111111111111111";
    private const string StaffYIdm = "2222222222222222";

    private readonly DbContext _dbContext;
    private readonly LedgerRepository _repository;

    public LedgerRepositoryUpdateEditedColumnsTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _repository = new LedgerRepository(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<int> SeedLedgerAsync()
    {
        using (var lease = await _dbContext.LeaseConnectionAsync())
        using (var command = lease.Connection.CreateCommand())
        {
            command.CommandText =
                "INSERT OR IGNORE INTO ic_card (card_idm, card_type, card_number) VALUES (@idm, 'はやかけん', '001');" +
                "INSERT OR IGNORE INTO staff (staff_idm, name, number) VALUES (@x, '博多 花子', '001'), (@y, '天神 太郎', '002');";
            command.Parameters.AddWithValue("@idm", TestCardIdm);
            command.Parameters.AddWithValue("@x", StaffXIdm);
            command.Parameters.AddWithValue("@y", StaffYIdm);
            await command.ExecuteNonQueryAsync();
        }

        return await _repository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            LenderIdm = StaffXIdm,
            StaffName = "博多 花子",
            Date = new DateTime(2026, 2, 10),
            Summary = "鉄道（博多～天神）",
            Income = 1000,
            Expense = 260,
            Balance = 1740,
            Note = "領収書あり",
            LentAt = new DateTime(2026, 2, 10, 8, 30, 0),
            ReturnedAt = new DateTime(2026, 2, 10, 18, 0, 0),
            CompanionCount = 2,
        });
    }

    /// <summary>すべての列が DB（<see cref="SeedLedgerAsync"/>）と異なる値の Ledger</summary>
    private static Ledger CreateAllChanged(int id) => new()
    {
        Id = id,
        CardIdm = TestCardIdm,
        LenderIdm = StaffYIdm,
        StaffName = "天神 太郎",
        Date = new DateTime(2026, 2, 11),
        Summary = "鉄道（天神～博多）",
        Income = 2000,
        Expense = 300,
        Balance = 2700,
        Note = "備考を直した",
        LentAt = new DateTime(2026, 3, 1, 9, 0, 0),
        ReturnedAt = new DateTime(2026, 3, 1, 17, 0, 0),
        IsLentRecord = true,
        CompanionCount = 5,
    };

    private async Task<Ledger> UpdateAndReadAsync(int id, LedgerRowEditColumns columns)
    {
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            (await _repository.UpdateEditedColumnsAsync(CreateAllChanged(id), columns, scope.Transaction)).Should().BeTrue();
            scope.Commit();
        }

        return (await _repository.GetByIdAsync(id))!;
    }

    private static void ShouldKeepColumnsThatRowEditNeverWrites(Ledger stored)
    {
        stored.CardIdm.Should().Be(TestCardIdm);
        stored.LentAt.Should().Be(new DateTime(2026, 2, 10, 8, 30, 0));
        stored.ReturnedAt.Should().Be(new DateTime(2026, 2, 10, 18, 0, 0));
        stored.IsLentRecord.Should().BeFalse("貸出中フラグは行編集で編集しないので、この経路から書かない");
    }

    [Fact]
    public async Task 備考だけを指定すると備考だけを書き他の列を変えないこと()
    {
        var id = await SeedLedgerAsync();

        var stored = await UpdateAndReadAsync(id, LedgerRowEditColumns.Note);

        stored.Note.Should().Be("備考を直した");
        stored.LenderIdm.Should().Be(StaffXIdm, "利用者を指定していないので、渡した別の職員を書かない（#2375 の欠陥）");
        stored.StaffName.Should().Be("博多 花子");
        stored.Date.Should().Be(new DateTime(2026, 2, 10));
        stored.Summary.Should().Be("鉄道（博多～天神）");
        stored.Income.Should().Be(1000);
        stored.Expense.Should().Be(260);
        stored.Balance.Should().Be(1740);
        stored.CompanionCount.Should().Be(2);
        ShouldKeepColumnsThatRowEditNeverWrites(stored);
    }

    [Fact]
    public async Task 利用者を指定すると貸出者IDmと氏名を組で書くこと()
    {
        var id = await SeedLedgerAsync();

        var stored = await UpdateAndReadAsync(id, LedgerRowEditColumns.Staff);

        stored.LenderIdm.Should().Be(StaffYIdm);
        stored.StaffName.Should().Be("天神 太郎");
        stored.Note.Should().Be("領収書あり");
        stored.Summary.Should().Be("鉄道（博多～天神）");
        ShouldKeepColumnsThatRowEditNeverWrites(stored);
    }

    [Fact]
    public async Task 行編集で編集できる列をすべて指定するとすべて書き_それ以外の列は書かないこと()
    {
        var id = await SeedLedgerAsync();

        var stored = await UpdateAndReadAsync(
            id,
            LedgerRowEditColumns.Date | LedgerRowEditColumns.Summary | LedgerRowEditColumns.Income
            | LedgerRowEditColumns.Expense | LedgerRowEditColumns.Balance | LedgerRowEditColumns.Staff
            | LedgerRowEditColumns.Note | LedgerRowEditColumns.CompanionCount);

        stored.LenderIdm.Should().Be(StaffYIdm);
        stored.StaffName.Should().Be("天神 太郎");
        stored.Date.Should().Be(new DateTime(2026, 2, 11));
        stored.Summary.Should().Be("鉄道（天神～博多）");
        stored.Income.Should().Be(2000);
        stored.Expense.Should().Be(300);
        stored.Balance.Should().Be(2700);
        stored.Note.Should().Be("備考を直した");
        stored.CompanionCount.Should().Be(5);
        ShouldKeepColumnsThatRowEditNeverWrites(stored);
    }

    [Theory]
    [InlineData(LedgerRowEditColumns.Date)]
    [InlineData(LedgerRowEditColumns.Summary)]
    [InlineData(LedgerRowEditColumns.Income)]
    [InlineData(LedgerRowEditColumns.Expense)]
    [InlineData(LedgerRowEditColumns.Balance)]
    [InlineData(LedgerRowEditColumns.CompanionCount)]
    public async Task 指定した1列だけが変わること(LedgerRowEditColumns column)
    {
        var id = await SeedLedgerAsync();

        var stored = await UpdateAndReadAsync(id, column);

        stored.Date.Should().Be(column == LedgerRowEditColumns.Date ? new DateTime(2026, 2, 11) : new DateTime(2026, 2, 10));
        stored.Summary.Should().Be(column == LedgerRowEditColumns.Summary ? "鉄道（天神～博多）" : "鉄道（博多～天神）");
        stored.Income.Should().Be(column == LedgerRowEditColumns.Income ? 2000 : 1000);
        stored.Expense.Should().Be(column == LedgerRowEditColumns.Expense ? 300 : 260);
        stored.Balance.Should().Be(column == LedgerRowEditColumns.Balance ? 2700 : 1740);
        stored.CompanionCount.Should().Be(column == LedgerRowEditColumns.CompanionCount ? 5 : 2);
        stored.Note.Should().Be("領収書あり");
        stored.LenderIdm.Should().Be(StaffXIdm);
        ShouldKeepColumnsThatRowEditNeverWrites(stored);
    }

    [Fact]
    public async Task 対象の行が無ければfalseを返すこと()
    {
        // Issue #1753 / #2325: 影響行数 0 は他の PC が削除・統合した（返却で貸出中レコードが消えた）競合
        using var scope = await _dbContext.BeginTransactionAsync();

        (await _repository.UpdateEditedColumnsAsync(CreateAllChanged(99999), LedgerRowEditColumns.Note, scope.Transaction))
            .Should().BeFalse();
    }

    [Fact]
    public async Task コミットしなければ書き込みが残らないこと()
    {
        // 渡したトランザクションに参加していること（監査ログと同じトランザクションで確定させる）
        var id = await SeedLedgerAsync();

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            (await _repository.UpdateEditedColumnsAsync(CreateAllChanged(id), LedgerRowEditColumns.Note, scope.Transaction))
                .Should().BeTrue();
        }

        (await _repository.GetByIdAsync(id))!.Note.Should().Be("領収書あり");
    }

    [Fact]
    public async Task 書く列が無い指定と未定義の列とトランザクション無しは例外とすること()
    {
        var id = await SeedLedgerAsync();
        using var scope = await _dbContext.BeginTransactionAsync();

        await _repository.Invoking(r => r.UpdateEditedColumnsAsync(CreateAllChanged(id), LedgerRowEditColumns.None, scope.Transaction))
            .Should().ThrowAsync<ArgumentException>().WithParameterName("columns");
        await _repository.Invoking(r => r.UpdateEditedColumnsAsync(CreateAllChanged(id), (LedgerRowEditColumns)(1 << 20), scope.Transaction))
            .Should().ThrowAsync<ArgumentException>().WithParameterName("columns");
        await _repository.Invoking(r => r.UpdateEditedColumnsAsync(CreateAllChanged(id), LedgerRowEditColumns.Note, null!))
            .Should().ThrowAsync<ArgumentNullException>().WithParameterName("transaction");
    }
}
