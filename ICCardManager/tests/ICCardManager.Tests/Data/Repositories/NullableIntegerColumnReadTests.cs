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
/// Issue #2381: <c>NOT NULL</c> を持たない整数列（Migration_001 の <c>is_deleted</c> / <c>is_lent</c> /
/// <c>income</c> / <c>expense</c> / <c>is_lent_record</c> / <c>is_charge</c> / <c>is_bus</c> 等）に NULL が入っていても、
/// 一覧・台帳を読めて、列の <c>DEFAULT</c> と同じ値として読まれること。
/// </summary>
/// <remarks>
/// <para>
/// 修正前は <c>reader.GetInt32(n)</c> で直接読んでいたため、外部ツールで値を空にした行が 1 行あるだけで
/// その行を含む一覧・帳票・CSV 出力が <c>InvalidCastException</c> で丸ごと開けなくなっていた。
/// </para>
/// <para>
/// NULL にする列はテーブルの定義（<c>PRAGMA table_info</c>）から導出する（列名で列挙しない）。
/// 後から <c>NOT NULL</c> を付けずに整数列を足しても自動で対象になる。導出が空振りしていないことは、
/// Migration_001 の列が導出結果に含まれることで対に表明する。
/// </para>
/// <para>
/// 「正当な動作を塞いでいない側」として、値が入っている行（1 や 0 以外の金額）が従来どおり読めることを置く。
/// これが無いと、常に既定値を返す実装でも緑になる。
/// </para>
/// </remarks>
public class NullableIntegerColumnReadTests : IDisposable
{
    private const string CardIdm = "0102030405060708";
    private const string StaffIdm = "1112131415161718";

    private readonly DbContext _dbContext;
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;
    private readonly LedgerRepository _ledgerRepository;

    public NullableIntegerColumnReadTests()
    {
        _dbContext = TestDbContextFactory.Create();

        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<IcCard>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan _) => factory());
        cacheService.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<Staff>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<Staff>>> factory, TimeSpan _) => factory());

        _cardRepository = new CardRepository(
            _dbContext, cacheService.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        _staffRepository = new StaffRepository(
            _dbContext, cacheService.Object, Options.Create(new CacheOptions()), NullLogger<StaffRepository>.Instance);
        _ledgerRepository = new LedgerRepository(_dbContext, NullLogger<LedgerRepository>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region 交通系ICカード

    [Fact]
    public async Task 交通系ICカードのNULL許容の整数列がすべてNULLでも読めて列の既定値になること()
    {
        await InsertCardAsync();
        var nulled = SetAllNullableIntegerColumnsToNull("ic_card");
        nulled.Should().Contain(new[] { "is_deleted", "is_lent", "starting_page_number", "is_refunded" },
            "導出が空振りしていないこと（Migration_001 / 005 / 006 の列が対象に入っている）");

        var all = (await _cardRepository.GetAllIncludingDeletedAsync()).ToList();
        var byId = await _cardRepository.GetByIdmAsync(CardIdm, includeDeleted: true);

        all.Should().ContainSingle();
        foreach (var card in new[] { all[0], byId! })
        {
            card.IsDeleted.Should().BeFalse();
            card.IsLent.Should().BeFalse();
            card.StartingPageNumber.Should().Be(1);
            card.IsRefunded.Should().BeFalse();
            card.CarryoverIncomeTotal.Should().Be(0);
            card.CarryoverExpenseTotal.Should().Be(0);
            card.CarryoverFiscalYear.Should().BeNull();
        }
    }

    [Fact]
    public async Task 貸出状態がNULLの交通系ICカードがあっても一覧を読めて未貸出として並ぶこと()
    {
        await InsertCardAsync();
        Execute("UPDATE ic_card SET is_lent = NULL WHERE card_idm = @idm", CardIdm);

        var cards = (await _cardRepository.GetAllAsync()).ToList();

        cards.Should().ContainSingle().Which.IsLent.Should().BeFalse();
    }

    [Fact]
    public async Task 交通系ICカードのフラグに値が入っていれば従来どおり読めること()
    {
        await InsertCardAsync();
        Execute("UPDATE ic_card SET is_deleted = 1, is_lent = 1, starting_page_number = 7 WHERE card_idm = @idm", CardIdm);

        var card = await _cardRepository.GetByIdmAsync(CardIdm, includeDeleted: true);

        card!.IsDeleted.Should().BeTrue();
        card.IsLent.Should().BeTrue();
        card.StartingPageNumber.Should().Be(7);
    }

    #endregion

    #region 職員

    [Fact]
    public async Task 職員のNULL許容の整数列がすべてNULLでも読めて列の既定値になること()
    {
        await InsertStaffAsync();
        var nulled = SetAllNullableIntegerColumnsToNull("staff");
        nulled.Should().Contain("is_deleted", "導出が空振りしていないこと");

        var all = (await _staffRepository.GetAllIncludingDeletedAsync()).ToList();
        var byId = await _staffRepository.GetByIdmAsync(StaffIdm, includeDeleted: true);

        all.Should().ContainSingle().Which.IsDeleted.Should().BeFalse();
        byId!.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task 職員の削除フラグに値が入っていれば従来どおり読めること()
    {
        await InsertStaffAsync();
        Execute("UPDATE staff SET is_deleted = 1 WHERE staff_idm = @idm", StaffIdm);

        var staff = await _staffRepository.GetByIdmAsync(StaffIdm, includeDeleted: true);

        staff!.IsDeleted.Should().BeTrue();
    }

    #endregion

    #region 台帳

    [Fact]
    public async Task 台帳と明細のNULL許容の整数列がすべてNULLでも読めて列の既定値になること()
    {
        await InsertCardAsync();
        var ledgerId = await InsertLedgerWithDetailAsync();

        var nulledLedger = SetAllNullableIntegerColumnsToNull("ledger");
        // ledger_id は明細を台帳へ結ぶ列なので NULL にしない（NULL にすると明細がどの台帳にも属さなくなり、読み取りの対象から外れる）
        var nulledDetail = SetAllNullableIntegerColumnsToNull("ledger_detail", except: "ledger_id");
        nulledLedger.Should().Contain(new[] { "income", "expense", "is_lent_record" }, "導出が空振りしていないこと");
        nulledDetail.Should().Contain(new[] { "is_charge", "is_bus", "is_point_redemption", "amount", "balance" },
            "導出が空振りしていないこと");

        var from = new DateTime(2026, 4, 1);
        var to = new DateTime(2026, 4, 30);
        var byRange = (await _ledgerRepository.GetByDateRangeAsync(CardIdm, from, to)).ToList();
        var byId = await _ledgerRepository.GetByIdAsync(ledgerId);
        var paged = (await _ledgerRepository.GetPagedAsync(CardIdm, from, to, 1, 50)).Items.ToList();

        byRange.Should().ContainSingle();
        paged.Should().ContainSingle();
        foreach (var ledger in new[] { byRange[0], byId!, paged[0] })
        {
            ledger.Income.Should().Be(0);
            ledger.Expense.Should().Be(0);
            ledger.IsLentRecord.Should().BeFalse();
            ledger.Balance.Should().Be(790, "NOT NULL の列は対象外で、値はそのまま");
        }

        var detail = byId!.Details.Should().ContainSingle().Subject;
        detail.IsCharge.Should().BeFalse();
        detail.IsBus.Should().BeFalse();
        detail.IsPointRedemption.Should().BeFalse();
        detail.Amount.Should().BeNull();

        var detailsByLedger = await _ledgerRepository.GetDetailsByLedgerIdsAsync(new[] { ledgerId });
        detailsByLedger[ledgerId].Should().ContainSingle().Which.IsCharge.Should().BeFalse();

        var ledgerKeys = await _ledgerRepository.GetExistingLedgerKeysAsync(new[] { CardIdm });
        ledgerKeys.Should().ContainSingle().Which.Should().Be(
            (CardIdm, new DateTime(2026, 4, 10, 9, 0, 0), "鉄道（博多～天神）", 0, 0, 790));

        var detailKeys = await _ledgerRepository.GetExistingDetailKeysAsync(CardIdm, from);
        detailKeys.Should().ContainSingle().Which.IsCharge.Should().BeFalse();
    }

    [Fact]
    public async Task 台帳と明細に値が入っていれば従来どおり読めること()
    {
        await InsertCardAsync();
        var ledgerId = await InsertLedgerWithDetailAsync();
        Execute("UPDATE ledger SET income = 1000, expense = 210, is_lent_record = 1 WHERE card_idm = @idm", CardIdm);
        Execute("UPDATE ledger_detail SET is_charge = 1, is_bus = 1", null);

        var ledger = await _ledgerRepository.GetByIdAsync(ledgerId);
        var paged = (await _ledgerRepository.GetPagedAsync(
            CardIdm, new DateTime(2026, 4, 1), new DateTime(2026, 4, 30), 1, 50)).Items.Single();
        var ledgerKeys = await _ledgerRepository.GetExistingLedgerKeysAsync(new[] { CardIdm });
        var detailKeys = await _ledgerRepository.GetExistingDetailKeysAsync(CardIdm, new DateTime(2026, 4, 1));

        foreach (var l in new[] { ledger!, paged })
        {
            l.Income.Should().Be(1000);
            l.Expense.Should().Be(210);
            l.IsLentRecord.Should().BeTrue();
        }

        var detail = ledger!.Details.Should().ContainSingle().Subject;
        detail.IsCharge.Should().BeTrue();
        detail.IsBus.Should().BeTrue();
        ledgerKeys.Single().Income.Should().Be(1000);
        ledgerKeys.Single().Expense.Should().Be(210);
        detailKeys.Single().IsCharge.Should().BeTrue();
    }

    [Fact]
    public async Task 統合履歴の取消済みフラグがNULLでも読めて未取消になり値が入っていれば従来どおり読めること()
    {
        await InsertCardAsync();
        var ledgerId = await InsertLedgerWithDetailAsync();
        await _ledgerRepository.SaveMergeHistoryAsync(ledgerId, "統合A", "{}");
        await _ledgerRepository.SaveMergeHistoryAsync(ledgerId, "統合B", "{}");
        var nulled = SetAllNullableIntegerColumnsToNull("ledger_merge_history");
        nulled.Should().Contain("is_undone", "導出が空振りしていないこと");
        Execute("UPDATE ledger_merge_history SET is_undone = 1 WHERE description = '統合B'", null);

        var histories = await _ledgerRepository.GetMergeHistoriesAsync(undoneOnly: false);

        histories.Single(h => h.Description == "統合A").IsUndone.Should().BeFalse();
        histories.Single(h => h.Description == "統合B").IsUndone.Should().BeTrue();
    }

    #endregion

    #region 絞り込み・書き込みの条件も同じ解釈にそろっていること

    // 読み取りで NULL を既定値（偽）として扱った以上、SQL の条件（WHERE 列 = 0）も NULL を 0 として扱わないと、
    // 「未貸出・未削除と表示されるのに、削除・払い戻し・取り消しが何度やっても競合で失敗する」状態になる（#1759 の誤った名指し）。

    [Fact]
    public async Task 貸出状態と払戻状態がNULLの交通系ICカードは貸出可能な一覧に並び貸出も削除もできること()
    {
        await InsertCardAsync();
        Execute("UPDATE ic_card SET is_lent = NULL, is_refunded = NULL WHERE card_idm = @idm", CardIdm);

        (await _cardRepository.GetAvailableAsync(bypassCache: true)).Should().ContainSingle(c => c.CardIdm == CardIdm);
        (await _cardRepository.UpdateLentStatusAsync(CardIdm, true, new DateTime(2026, 4, 10, 9, 0, 0), null))
            .Should().BeTrue("払戻状態が NULL のカードは未払戻として貸し出せる");

        Execute("UPDATE ic_card SET is_lent = NULL WHERE card_idm = @idm", CardIdm);
        (await _cardRepository.DeleteAsync(CardIdm)).Should().Be(CardOperationResult.Success);
        (await _cardRepository.GetByIdmAsync(CardIdm, includeDeleted: true))!.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task 貸出状態がNULLの交通系ICカードは払い戻しできること()
    {
        await InsertCardAsync();
        Execute("UPDATE ic_card SET is_lent = NULL, is_refunded = NULL WHERE card_idm = @idm", CardIdm);

        var result = await _cardRepository.SetRefundedAsync(CardIdm, new DateTime(2026, 4, 10, 9, 0, 0), null);

        result.Should().Be(CardOperationResult.Success);
        (await _cardRepository.GetByIdmAsync(CardIdm))!.IsRefunded.Should().BeTrue();
    }

    [Fact]
    public async Task 削除フラグがNULLの交通系ICカードは通常の一覧と単票に並び編集できること()
    {
        await InsertCardAsync();
        Execute("UPDATE ic_card SET is_deleted = NULL WHERE card_idm = @idm", CardIdm);

        (await _cardRepository.GetAllAsync()).Should().ContainSingle(c => c.CardIdm == CardIdm);
        var card = await _cardRepository.GetByIdmAsync(CardIdm);
        card.Should().NotBeNull();

        card!.Note = "編集後";
        (await _cardRepository.UpdateAsync(card)).Should().BeTrue();
    }

    [Fact]
    public async Task 貸出中や削除済みの値が入っていれば従来どおり絞り込まれ削除も払い戻しもできないこと()
    {
        // 対の表明: 条件を丸ごと外した実装（何でも削除・払い戻しできる、削除済みも一覧に並ぶ）を落とす
        await InsertCardAsync();
        Execute("UPDATE ic_card SET is_lent = 1 WHERE card_idm = @idm", CardIdm);

        (await _cardRepository.GetAvailableAsync(bypassCache: true)).Should().BeEmpty();
        (await _cardRepository.DeleteAsync(CardIdm)).Should().Be(CardOperationResult.CardIsLent);
        (await _cardRepository.SetRefundedAsync(CardIdm, new DateTime(2026, 4, 10, 9, 0, 0), null))
            .Should().Be(CardOperationResult.CardIsLent);

        Execute("UPDATE ic_card SET is_lent = 0, is_deleted = 1 WHERE card_idm = @idm", CardIdm);
        (await _cardRepository.GetAllAsync()).Should().BeEmpty();
        (await _cardRepository.GetByIdmAsync(CardIdm)).Should().BeNull();
    }

    [Fact]
    public async Task 削除フラグがNULLの職員は通常の一覧と単票に並び編集も削除もできること()
    {
        await InsertStaffAsync();
        Execute("UPDATE staff SET is_deleted = NULL WHERE staff_idm = @idm", StaffIdm);

        (await _staffRepository.GetAllAsync()).Should().ContainSingle(s => s.StaffIdm == StaffIdm);
        (await _staffRepository.HasActiveStaffAsync()).Should().BeTrue();
        var staff = await _staffRepository.GetByIdmAsync(StaffIdm);
        staff.Should().NotBeNull();

        staff!.Note = "編集後";
        (await _staffRepository.UpdateAsync(staff)).Should().BeTrue();
        (await _staffRepository.DeleteAsync(StaffIdm)).Should().BeTrue();
        (await _staffRepository.GetByIdmAsync(StaffIdm, includeDeleted: true))!.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task 削除済みの職員は従来どおり一覧に並ばず削除もできないこと()
    {
        await InsertStaffAsync();
        Execute("UPDATE staff SET is_deleted = 1 WHERE staff_idm = @idm", StaffIdm);

        (await _staffRepository.GetAllAsync()).Should().BeEmpty();
        (await _staffRepository.HasActiveStaffAsync()).Should().BeFalse();
        (await _staffRepository.DeleteAsync(StaffIdm)).Should().BeFalse();
    }

    [Fact]
    public async Task 取消済みフラグがNULLの統合履歴は取り消し済みにでき取消済みなら競合になること()
    {
        await InsertCardAsync();
        var ledgerId = await InsertLedgerWithDetailAsync();
        await _ledgerRepository.SaveMergeHistoryAsync(ledgerId, "統合A", "{}");
        await _ledgerRepository.SaveMergeHistoryAsync(ledgerId, "統合B", "{}");
        Execute("UPDATE ledger_merge_history SET is_undone = NULL WHERE description = '統合A'", null);
        Execute("UPDATE ledger_merge_history SET is_undone = 1 WHERE description = '統合B'", null);
        var histories = await _ledgerRepository.GetMergeHistoriesAsync(undoneOnly: false);
        var idA = histories.Single(h => h.Description == "統合A").Id;
        var idB = histories.Single(h => h.Description == "統合B").Id;

        (await _ledgerRepository.MarkMergeHistoryUndoneAsync(idA)).Should().BeTrue("NULL は未取消として取り消せる");
        (await _ledgerRepository.MarkMergeHistoryUndoneAsync(idB)).Should().BeFalse("取消済みは従来どおり競合");

        (await _ledgerRepository.GetMergeHistoriesAsync(undoneOnly: true))
            .Should().Contain(h => h.Id == idA);
    }

    [Fact]
    public async Task 取消済みフラグがNULLの後の統合は未取消として前の統合の取り消しを止めること()
    {
        await InsertCardAsync();
        var ledgerId = await InsertLedgerWithDetailAsync();
        await _ledgerRepository.SaveMergeHistoryAsync(ledgerId, "統合A", "{}");
        await _ledgerRepository.SaveMergeHistoryAsync(ledgerId, "統合B", "{}");
        var histories = await _ledgerRepository.GetMergeHistoriesAsync(undoneOnly: false);
        var idA = histories.Single(h => h.Description == "統合A").Id;

        // 後の統合 B が NULL（一覧では未取消と表示される）→ A の取り消しは止まる
        Execute("UPDATE ledger_merge_history SET is_undone = NULL WHERE description = '統合B'", null);
        bool laterWhenNull;
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            laterWhenNull = await _ledgerRepository.HasLaterUndoableMergeOnSameTargetAsync(idA, ledgerId, scope.Transaction);
        }

        // 対の表明: B が取消済みなら止めない
        Execute("UPDATE ledger_merge_history SET is_undone = 1 WHERE description = '統合B'", null);
        bool laterWhenUndone;
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            laterWhenUndone = await _ledgerRepository.HasLaterUndoableMergeOnSameTargetAsync(idA, ledgerId, scope.Transaction);
        }

        laterWhenNull.Should().BeTrue();
        laterWhenUndone.Should().BeFalse();
    }

    /// <summary>
    /// 交通系ICカードの削除フラグで絞る経路のそれぞれが、削除フラグが NULL のカードを「削除されていない」として
    /// 対象に入れること（一覧に並ぶ・更新が成功する）。対の表明として、削除済み（1）なら対象に入らないこと。
    /// </summary>
    /// <remarks>
    /// 経路ごとに 1 行ずつ置くのは、条件を 1 か所だけ <c>is_deleted = 0</c> へ戻した退行をその経路の行で検出するため。
    /// 1 つの経路のテストだけでは、ほかの経路の条件が戻っても緑のままになる。
    /// </remarks>
    [Theory]
    [InlineData("GetAllAsync")]
    [InlineData("GetByIdmAsync")]
    [InlineData("GetAvailableAsync")]
    [InlineData("GetLentAsync")]
    [InlineData("GetRefundedWithLentRecordsAsync")]
    [InlineData("UpdateAsync")]
    [InlineData("UpdateCarryoverInfoAsync")]
    [InlineData("UpdateLentStatusAsync")]
    [InlineData("DeleteAsync")]
    [InlineData("SetRefundedAsync")]
    public async Task 削除フラグがNULLの交通系ICカードはどの経路でも削除されていないカードとして扱われること(string path)
    {
        (await RunCardPathAsync(path, isDeletedSql: "NULL")).Should().BeTrue("削除フラグが NULL のカードは未削除として扱う");
        (await RunCardPathAsync(path, isDeletedSql: "1")).Should().BeFalse("削除済みのカードは従来どおり対象外");
    }

    /// <summary>
    /// 貸出中の記録のフラグ（<c>is_lent_record</c>）で絞る集計のそれぞれで、フラグが NULL の行が 0 の行と同じに扱われること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// NULL の行は帳票（<c>ReportDataBuilder</c> の <c>!IsLentRecord</c>）では通常の行として印字されるので、
    /// 集計・月末残高・残高チェーンのシードが同じ行を除くと、同じ帳票の中で「受入 − 払出 = 残額」が崩れる。
    /// </para>
    /// <para>
    /// 期待値は「フラグ 0 のときの結果」と比べる（集計ごとの期待値を書き写さない）。あわせて「フラグ 1 のときの結果が
    /// 0 のときと異なる」ことを表明し、その集計が実際にこの行をフラグで絞っている（入力が条件を通っている）ことを確かめる。
    /// </para>
    /// <para>
    /// 「シード」の 2 行は、前日の 1 行（残額 1,000 円）だけがフラグを変え、当日の 2 行（ポイント還元 +210 と利用 −210）が
    /// 残高の循環（Issue #1004 の形）をなす。当日の最終残高は前日の残高（シード）からしか決まらないので、
    /// シードの条件だけを戻すと結果が変わる。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("最終利用日", false)]
    [InlineData("利用状況", false)]
    [InlineData("職員別の月次利用", false)]
    [InlineData("月末残高", false)]
    [InlineData("指定日より前の残高", false)]
    [InlineData("明細を持たない行の最新日", false)]
    [InlineData("月末残高", true)]
    [InlineData("指定日より前の残高", true)]
    public async Task 貸出中の記録のフラグがNULLの行は集計で通常の行として扱われること(string query, bool seedScenario)
    {
        await InsertCardAsync();
        await InsertStaffAsync();

        var whenZero = await RunLedgerQueryAsync(query, seedScenario, "0");
        var whenNull = await RunLedgerQueryAsync(query, seedScenario, "NULL");
        var whenLent = await RunLedgerQueryAsync(query, seedScenario, "1");

        whenLent.Should().NotBe(whenZero, "この集計が対象の行を貸出中の記録のフラグで絞っていること（入力が条件を通っていること）");
        whenNull.Should().Be(whenZero, "フラグが NULL の行は 0 の行と同じに扱う");
    }

    private async Task<bool> RunCardPathAsync(string path, string isDeletedSql)
    {
        Execute("DELETE FROM ledger_detail", idm: null, requireRows: false);
        Execute("DELETE FROM ledger", idm: null, requireRows: false);
        Execute("DELETE FROM ic_card", idm: null, requireRows: false);
        await InsertCardAsync();
        Execute($"UPDATE ic_card SET is_deleted = {isDeletedSql} WHERE card_idm = @idm", CardIdm);

        switch (path)
        {
            case "GetAllAsync":
                return (await _cardRepository.GetAllAsync()).Any(c => c.CardIdm == CardIdm);
            case "GetByIdmAsync":
                return await _cardRepository.GetByIdmAsync(CardIdm) != null;
            case "GetAvailableAsync":
                return (await _cardRepository.GetAvailableAsync(bypassCache: true)).Any(c => c.CardIdm == CardIdm);
            case "GetLentAsync":
                Execute("UPDATE ic_card SET is_lent = 1 WHERE card_idm = @idm", CardIdm);
                return (await _cardRepository.GetLentAsync(bypassCache: true)).Any(c => c.CardIdm == CardIdm);
            case "GetRefundedWithLentRecordsAsync":
                Execute("UPDATE ic_card SET is_refunded = 1 WHERE card_idm = @idm", CardIdm);
                await _ledgerRepository.InsertAsync(new Ledger
                {
                    CardIdm = CardIdm,
                    Date = new DateTime(2026, 4, 10, 9, 0, 0),
                    Summary = "（貸出中）",
                    Balance = 1000,
                    IsLentRecord = true
                }, transaction: null);
                return (await _cardRepository.GetRefundedWithLentRecordsAsync()).Any(c => c.CardIdm == CardIdm);
            case "UpdateAsync":
                return await _cardRepository.UpdateAsync(new IcCard
                {
                    CardIdm = CardIdm,
                    CardType = "はやかけん",
                    CardNumber = "H001",
                    Note = "編集後"
                });
            case "UpdateCarryoverInfoAsync":
                using (var scope = await _dbContext.BeginTransactionAsync())
                {
                    var updated = await _cardRepository.UpdateCarryoverInfoAsync(
                        CardIdm, new CarryoverInfo(1, 0, 0, null), new CarryoverInfo(7, 45000, 0, 2025), scope.Transaction);
                    scope.Commit();
                    return updated;
                }

            case "UpdateLentStatusAsync":
                return await _cardRepository.UpdateLentStatusAsync(CardIdm, true, new DateTime(2026, 4, 10, 9, 0, 0), null);
            case "DeleteAsync":
                return await _cardRepository.DeleteAsync(CardIdm) == CardOperationResult.Success;
            case "SetRefundedAsync":
                return await _cardRepository.SetRefundedAsync(CardIdm, new DateTime(2026, 4, 10, 9, 0, 0), null)
                    == CardOperationResult.Success;
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, "未知の経路");
        }
    }

    private async Task<string> RunLedgerQueryAsync(string query, bool seedScenario, string flagSql)
    {
        Execute("DELETE FROM ledger_detail", idm: null, requireRows: false);
        Execute("DELETE FROM ledger", idm: null, requireRows: false);

        int flaggedId;
        if (seedScenario)
        {
            flaggedId = await InsertPlainLedgerAsync(new DateTime(2026, 4, 9, 9, 0, 0), "鉄道（博多～天神）", 0, 210, 1000);
            // 当日: ポイント還元（790 → 1,000）を先に、利用（1,000 → 790）を後に INSERT する（id 順と時系列が食い違う）
            await InsertPlainLedgerAsync(new DateTime(2026, 4, 10, 9, 0, 0), "ポイント還元", 210, 0, 1000);
            await InsertPlainLedgerAsync(new DateTime(2026, 4, 10, 9, 0, 0), "鉄道（天神～博多）", 0, 210, 790);
        }
        else
        {
            flaggedId = await InsertPlainLedgerAsync(new DateTime(2026, 4, 10, 9, 0, 0), "鉄道（博多～天神）", 0, 210, 790);
        }

        Execute($"UPDATE ledger SET is_lent_record = {flagSql} WHERE id = {flaggedId}", idm: null);

        var from = new DateTime(2026, 4, 1);
        var to = new DateTime(2026, 4, 30, 23, 59, 59);
        switch (query)
        {
            case "最終利用日":
                return Dump(await _ledgerRepository.GetAllLastUsageDatesAsync());
            case "利用状況":
                return Dump(await _ledgerRepository.GetUsageStatsByCardAsync(from, to));
            case "職員別の月次利用":
                return Dump(await _ledgerRepository.GetMonthlyUsageByLenderAsync(from, to));
            case "月末残高":
                return Dump(await _ledgerRepository.GetMonthEndBalancesByCardAsync(from, to));
            case "指定日より前の残高":
                return Dump(await _ledgerRepository.GetBalancesBeforeAsync(new DateTime(2026, 5, 1)));
            case "明細を持たない行の最新日":
                return Dump(await _ledgerRepository.GetLatestLedgerDateWithoutDetailsAsync(CardIdm));
            default:
                throw new ArgumentOutOfRangeException(nameof(query), query, "未知の集計");
        }
    }

    private Task<int> InsertPlainLedgerAsync(DateTime date, string summary, int income, int expense, int balance)
        => _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = CardIdm,
            LenderIdm = StaffIdm,
            StaffName = "博多 太郎",
            Date = date,
            Summary = summary,
            Income = income,
            Expense = expense,
            Balance = balance
        }, transaction: null);

    /// <summary>集計の結果を、比較できる文字列にする（行・辞書の要素は公開プロパティを並べる）。</summary>
    private static string Dump(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case string s:
                return s;
            case System.Collections.IDictionary dict:
                return "{" + string.Join(", ", dict.Keys.Cast<object>().Select(k => $"{Dump(k)}={Dump(dict[k])}")) + "}";
            case System.Collections.IEnumerable items:
                return "[" + string.Join(", ", items.Cast<object>().Select(Dump)) + "]";
        }

        var type = value.GetType();
        if (type.IsPrimitive || value is DateTime || value is decimal)
        {
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
        }

        if (type.IsGenericType && type.FullName!.StartsWith("System.ValueTuple", StringComparison.Ordinal))
        {
            return "(" + string.Join(", ", type.GetFields().Select(f => Dump(f.GetValue(value)))) + ")";
        }

        return type.Name + "(" + string.Join(", ", type.GetProperties().Select(p => $"{p.Name}={Dump(p.GetValue(value))}")) + ")";
    }

    #endregion

    #region ヘルパー

    private Task<bool> InsertCardAsync() => _cardRepository.InsertAsync(new IcCard
    {
        CardIdm = CardIdm,
        CardType = "はやかけん",
        CardNumber = "H001"
    });

    private Task<bool> InsertStaffAsync() => _staffRepository.InsertAsync(new Staff
    {
        StaffIdm = StaffIdm,
        Name = "博多 太郎"
    });

    private async Task<int> InsertLedgerWithDetailAsync()
    {
        var ledgerId = await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = CardIdm,
            Date = new DateTime(2026, 4, 10, 9, 0, 0),
            Summary = "鉄道（博多～天神）",
            Income = 0,
            Expense = 210,
            Balance = 790
        }, transaction: null);
        await _ledgerRepository.InsertDetailAsync(new LedgerDetail
        {
            LedgerId = ledgerId,
            UseDate = new DateTime(2026, 4, 10, 9, 0, 0),
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 210,
            Balance = 790
        }, transaction: null);
        return ledgerId;
    }

    /// <summary>
    /// テーブルの定義から「NOT NULL も主キーも持たない INTEGER 列」を導出し、全行のその列を NULL にする。
    /// </summary>
    /// <returns>NULL にした列名</returns>
    private List<string> SetAllNullableIntegerColumnsToNull(string table, string? except = null)
    {
        using var lease = _dbContext.LeaseConnection();
        var columns = new List<string>();
        using (var pragma = lease.Connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info({table})";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                var type = reader.GetString(2);
                var notNull = reader.GetInt64(3) != 0;
                var isPrimaryKey = reader.GetInt64(5) != 0;
                if (string.Equals(type, "INTEGER", StringComparison.OrdinalIgnoreCase)
                    && !notNull && !isPrimaryKey && name != except)
                {
                    columns.Add(name);
                }
            }
        }

        using var update = lease.Connection.CreateCommand();
        update.CommandText = $"UPDATE {table} SET {string.Join(", ", columns.Select(c => $"{c} = NULL"))}";
        update.ExecuteNonQuery().Should().BeGreaterThan(0, $"{table} に NULL にする行があること");
        return columns;
    }

    private void Execute(string sql, string? idm, bool requireRows = true)
    {
        using var lease = _dbContext.LeaseConnection();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        if (idm != null)
        {
            command.Parameters.AddWithValue("@idm", idm);
        }

        var affected = command.ExecuteNonQuery();
        if (requireRows)
        {
            affected.Should().BeGreaterThan(0);
        }
    }

    #endregion
}
