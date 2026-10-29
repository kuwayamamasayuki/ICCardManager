using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.Tests.Infrastructure.Timing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2302: 統合後に統合先の明細を編集してから統合を取り消すと、取り消しが止まらずに
/// 別の内容の明細を統合元へ戻していた。
/// </summary>
/// <remarks>
/// <para>
/// 明細の保存（<c>ReplaceDetailsAsync</c> の DELETE + INSERT）は、統合先の明細が表の末尾にあると
/// <b>同じ統合先のまま同じ id を別の内容で振り直す</b>（<c>ledger_detail.id</c> は AUTOINCREMENT なしの
/// INTEGER PRIMARY KEY で、<c>max(id)+1</c> を再利用する。02 §3.4）。取り消しのガードは
/// 「id と所属（ledger_id）」と影響行数だけで判定していたため、この形を通してしまい、
/// 統合先の摘要・金額は統合前へ書き戻しつつ、編集後の明細を統合元へ移していた。
/// 修正後は、統合した時点の明細の内容（<see cref="LedgerDetailSnapshot"/>）を Undo データに残し、
/// 取り消しの前に照合する。
/// </para>
/// <para>
/// 欠陥は「id が再利用される」ことが前提なので、各テストは<b>再利用が実際に起きたこと</b>を前提として表明する
/// （db-write-conventions.md「回帰テスト自身が故障の起点を作れているかを表明する」）。
/// ロールバックはモックでは観測できないため、実リポジトリ・実 DB で行を表明する。
/// </para>
/// </remarks>
public class LedgerMergeUnmergeDetailContentGuardTests : IDisposable
{
    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "テスト職員";

    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly RecordingLogger<LedgerRepository> _repositoryLogger = new();
    private readonly CurrentOperatorContext _operatorContext =
        new(new FixedSystemClock(new DateTime(2026, 10, 23, 9, 0, 0)));
    private readonly LedgerMergeService _service;

    public LedgerMergeUnmergeDetailContentGuardTests()
    {
        _dbContext = TestDbContextFactory.Create();

        var cacheServiceMock = new Mock<ICacheService>();
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan _) => factory());
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<Staff>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<Staff>>> factory, TimeSpan _) => factory());

        _ledgerRepository = new LedgerRepository(_dbContext, _repositoryLogger);
        var cardRepository = new CardRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        var staffRepository = new StaffRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<StaffRepository>.Instance);

        _service = new LedgerMergeService(
            _ledgerRepository,
            new SummaryGenerator(),
            new OperationLogger(new OperationLogRepository(_dbContext), _operatorContext),
            _dbContext,
            NullLogger<LedgerMergeService>.Instance);

        staffRepository.InsertAsync(new Staff { StaffIdm = TestStaffIdm, Name = TestStaffName }).GetAwaiter().GetResult();
        cardRepository.InsertAsync(new IcCard { CardIdm = TestCardIdm, CardType = "はやかけん", CardNumber = "H001" })
            .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Ledger CreateLedger(int balance, string summary) => new()
    {
        CardIdm = TestCardIdm,
        LenderIdm = TestStaffIdm,
        Date = new DateTime(2026, 4, 1, 9, 0, 0),
        Summary = summary,
        Expense = 210,
        Balance = balance,
        StaffName = TestStaffName,
    };

    private static LedgerDetail CreateDetail(int ledgerId, string entry, string exit, int amount, int balance) => new()
    {
        LedgerId = ledgerId,
        UseDate = new DateTime(2026, 4, 1, 9, 0, 0),
        EntryStation = entry,
        ExitStation = exit,
        Amount = amount,
        Balance = balance,
    };

    /// <summary>
    /// 統合先（薬院→博多）と統合元（博多→薬院）を統合し、(統合先 ID, 統合履歴 ID, 統合時の明細 id) を返す。
    /// 新しい DB なので、2 件の明細は <c>ledger_detail</c> の末尾にある（直近に返却した行を統合する典型的な運用と同じ形）。
    /// </summary>
    private async Task<(int TargetId, int HistoryId, List<int> MergedDetailIds)> MergeTwoLedgersAsync()
    {
        var targetId = await _ledgerRepository.InsertAsync(CreateLedger(balance: 2186, summary: "鉄道（薬院～博多）"));
        await _ledgerRepository.InsertDetailAsync(CreateDetail(targetId, "薬院", "博多", amount: 210, balance: 2186));
        var sourceId = await _ledgerRepository.InsertAsync(CreateLedger(balance: 1976, summary: "鉄道（博多～薬院）"));
        await _ledgerRepository.InsertDetailAsync(CreateDetail(sourceId, "博多", "薬院", amount: 210, balance: 1976));

        var merge = await _service.MergeAsync(new List<int> { targetId, sourceId });
        merge.Success.Should().BeTrue($"前提の統合は成功するべき: {merge.ErrorMessage}");
        var historyId = (await _service.GetUndoableMergeHistoriesAsync()).Single().Id;
        var mergedIds = (await _ledgerRepository.GetByIdAsync(targetId))!.Details.Select(d => d.SequenceNumber).ToList();
        mergedIds.Should().HaveCount(2, "前提: 統合先は 2 件の明細を持つ");
        return (targetId, historyId, mergedIds);
    }

    /// <summary>
    /// 統合先の明細を作り直す（明細の保存と同じ <c>ReplaceDetailsAsync</c>）。作り直した明細が統合時と同じ id を
    /// 再利用したこと（＝欠陥の前提）を表明する。
    /// </summary>
    private async Task ReplaceTargetDetailsReusingIdsAsync(int targetId, List<int> mergedIds, params LedgerDetail[] details)
    {
        (await _ledgerRepository.ReplaceDetailsAsync(targetId, details)).Should().BeTrue("前提の明細の保存は成功するべき");
        var reassigned = (await _ledgerRepository.GetByIdAsync(targetId))!.Details.Select(d => d.SequenceNumber).ToList();
        reassigned.Should().BeEquivalentTo(mergedIds,
            "前提: 統合先の明細が表の末尾にあり件数も同じなら、作り直した明細は統合時と同じ id を再利用する（欠陥の起点）");
    }

    private async Task<long> CountLedgersAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ledger WHERE card_idm = @idm";
        command.Parameters.AddWithValue("@idm", TestCardIdm);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// 受け入れ条件: 統合 → 統合先の明細を編集（末尾・件数同じ。統合元から来た明細の降車駅と金額を直す）→ 取り消し、
    /// の順で操作すると、取り消しは競合として中止され、台帳は変更されない。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_統合後に統合先の明細を編集して同じidが振り直されていたら中止し何も変えないこと()
    {
        var (targetId, historyId, mergedIds) = await MergeTwoLedgersAsync();
        await ReplaceTargetDetailsReusingIdsAsync(targetId, mergedIds,
            CreateDetail(targetId, "薬院", "博多", amount: 210, balance: 2186),
            CreateDetail(targetId, "博多", "天神", amount: 260, balance: 1926));
        var editedBefore = await _ledgerRepository.GetByIdAsync(targetId);

        var result = await _service.UnmergeAsync(historyId);

        result.Success.Should().BeFalse("編集後の別の内容の明細を統合元へ戻してはならない");
        result.ErrorMessage.Should().Contain("編集", "統合後の編集が原因であり得ることを案内するべき");
        result.ErrorMessage.Should().NotBe(LedgerMergeService.StackedMergeUnmergeErrorMessage,
            "統合を重ねていないのに「新しい方の統合を先に取り消す」と案内しないべき");
        _repositoryLogger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("内容が統合時と一致しない"),
            "中止の理由（内容の不一致）をログに残すべき");
        (await CountLedgersAsync()).Should().Be(1, "統合元を復活させないべき");
        var target = await _ledgerRepository.GetByIdAsync(targetId);
        target!.Summary.Should().Be(editedBefore!.Summary, "統合先の摘要を統合前へ書き戻さないべき");
        target.Expense.Should().Be(editedBefore.Expense);
        target.Balance.Should().Be(editedBefore.Balance);
        target.Details.Select(d => (d.SequenceNumber, d.ExitStation, d.Amount)).Should().BeEquivalentTo(
            editedBefore.Details.Select(d => (d.SequenceNumber, d.ExitStation, d.Amount)), "明細は 1 件も動かさないべき");
        (await _service.GetUndoableMergeHistoriesAsync()).Should().ContainSingle(h => h.Id == historyId,
            "中止した統合は取り消し済みにしないべき（04 §11.5: 元に戻せない履歴は一覧に残る）");
    }

    /// <summary>
    /// 統合先自身の明細（統合元から来た明細ではない方）だけを編集した場合も中止すること。
    /// 統合先の明細は取り消しで移動しないが、書き戻す統合先の金額と明細が食い違う。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_統合先自身の明細だけを編集していても中止すること()
    {
        var (targetId, historyId, mergedIds) = await MergeTwoLedgersAsync();
        await ReplaceTargetDetailsReusingIdsAsync(targetId, mergedIds,
            CreateDetail(targetId, "天神", "博多", amount: 260, balance: 2186),
            CreateDetail(targetId, "博多", "薬院", amount: 210, balance: 1976));

        var result = await _service.UnmergeAsync(historyId);

        result.Success.Should().BeFalse("統合先の明細が統合時と違えば、統合前の金額へ書き戻すと明細と食い違う");
        (await CountLedgersAsync()).Should().Be(1, "統合元を復活させないべき");
    }

    /// <summary>
    /// 対の正常系: 統合後に何も編集していなければ、取り消しは従来どおり成功する。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_統合後に何も編集していなければ従来どおり取り消せること()
    {
        var (targetId, historyId, _) = await MergeTwoLedgersAsync();

        var result = await _service.UnmergeAsync(historyId);

        result.Success.Should().BeTrue(result.ErrorMessage);
        (await CountLedgersAsync()).Should().Be(2, "統合元が復活するべき");
        var target = await _ledgerRepository.GetByIdAsync(targetId);
        target!.Summary.Should().Be("鉄道（薬院～博多）");
        target.Details.Should().ContainSingle().Which.ExitStation.Should().Be("博多");
    }

    /// <summary>
    /// 対の正常系: 明細を作り直しても、各 id へ統合時と同じ内容が振り直されたなら、同じ記録なので取り消せる。
    /// 「明細を作り直したら一律に中止」ではなく「id の指す内容が変わったら中止」であることを固定する。
    /// 画面（利用履歴詳細）からの保存は並べ替えで id が入れ替わることが多く、その場合は中止になる（次のテスト）。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_同じidへ同じ内容が振り直されただけなら取り消せること()
    {
        var (targetId, historyId, mergedIds) = await MergeTwoLedgersAsync();
        var current = (await _ledgerRepository.GetByIdAsync(targetId))!.Details
            .OrderBy(d => d.SequenceNumber) // id の小さい順に INSERT し直す（同じ id を同じ内容へ振り直す）
            .Select(d => CreateDetail(targetId, d.EntryStation, d.ExitStation, d.Amount!.Value, d.Balance!.Value))
            .ToArray();
        await ReplaceTargetDetailsReusingIdsAsync(targetId, mergedIds, current);

        var result = await _service.UnmergeAsync(historyId);

        result.Success.Should().BeTrue(result.ErrorMessage);
        (await CountLedgersAsync()).Should().Be(2, "統合元が復活するべき");
    }

    /// <summary>
    /// 対の正常系: 統合後にバス停名だけを入力した（id を保ったまま書き換える唯一の経路）なら取り消せる。
    /// バス停名は照合に含めない（同じ利用の記録のまま。入力した値はそのまま統合元へ移る）。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_統合後にバス停名だけを入力していても取り消せること()
    {
        var (targetId, historyId, mergedIds) = await MergeTwoLedgersAsync();
        (await _ledgerRepository.UpdateDetailBusStopsAsync(targetId, new[] { (mergedIds.Max(), "天神～博多駅前") }))
            .Should().BeTrue("前提のバス停名の入力は成功するべき");

        var result = await _service.UnmergeAsync(historyId);

        result.Success.Should().BeTrue(result.ErrorMessage);
        (await CountLedgersAsync()).Should().Be(2, "統合元が復活するべき");
    }
    /// <summary>
    /// レビュー指摘: 統合後の明細の編集で古い利用を 1 件「足して」保存すると、既存の 2 件は同じ id・同じ内容を取り直し、
    /// 追加分だけが新しい id になる。Undo データのキーにある明細だけを照合すると通ってしまい、追加した明細が
    /// 統合先に残ったまま金額・摘要だけが統合前へ戻る。件数の一致で中止すること。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_統合後に統合先へ明細を足して保存していたら中止すること()
    {
        var (targetId, historyId, mergedIds) = await MergeTwoLedgersAsync();
        var existing = (await _ledgerRepository.GetByIdAsync(targetId))!.Details
            .OrderBy(d => d.SequenceNumber)
            .Select(d => CreateDetail(targetId, d.EntryStation, d.ExitStation, d.Amount!.Value, d.Balance!.Value))
            .ToList();
        var added = CreateDetail(targetId, "天神", "薬院", amount: 210, balance: 2396);
        added.UseDate = new DateTime(2026, 3, 31, 9, 0, 0);
        (await _ledgerRepository.ReplaceDetailsAsync(targetId, existing.Append(added).ToList())).Should().BeTrue();
        var after = (await _ledgerRepository.GetByIdAsync(targetId))!.Details.Select(d => d.SequenceNumber).ToList();
        after.Should().HaveCount(3);
        after.Should().Contain(mergedIds, "前提: 既存の明細は統合時と同じ id を取り直し、内容の照合だけでは通る形");

        var result = await _service.UnmergeAsync(historyId);

        result.Success.Should().BeFalse("足した明細を統合先に残したまま統合前の金額へ書き戻してはならない");
        _repositoryLogger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("件数が統合時と一致しない"),
            "中止の理由（件数の不一致）をログに残すべき");
        (await CountLedgersAsync()).Should().Be(1, "統合元を復活させないべき");
        (await _ledgerRepository.GetByIdAsync(targetId))!.Details.Should().HaveCount(3, "明細は動かさないべき");
    }

    /// <summary>
    /// レビュー指摘: 同じ統合先へ後から別の統合（H2）を重ね、H2 を残したまま先の統合（H1）を取り消すと、
    /// H2 由来の明細が統合先に残ったまま H1 の前の金額へ書き戻される。H1 は中止し、H2 を取り消した後なら H1 も取り消せること。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_後から重ねた統合を残したまま先の統合を取り消そうとすると中止し後の統合から順なら取り消せること()
    {
        var (targetId, firstHistoryId, _) = await MergeTwoLedgersAsync();
        var thirdId = await _ledgerRepository.InsertAsync(CreateLedger(balance: 1766, summary: "鉄道（薬院～天神）"));
        await _ledgerRepository.InsertDetailAsync(CreateDetail(thirdId, "薬院", "天神", amount: 210, balance: 1766));
        var merge2 = await _service.MergeAsync(new List<int> { targetId, thirdId });
        merge2.Success.Should().BeTrue($"前提の 2 回目の統合は成功するべき: {merge2.ErrorMessage}");
        var secondHistoryId = (await _service.GetUndoableMergeHistoriesAsync()).Single(h => h.Id != firstHistoryId).Id;

        var first = await _service.UnmergeAsync(firstHistoryId);

        first.Success.Should().BeFalse("後の統合の明細が統合先に残ったまま、先の統合の前へ書き戻してはならない");
        first.ErrorMessage.Should().Be(LedgerMergeService.StackedMergeUnmergeErrorMessage,
            "後の統合を先に取り消せば直ることを案内するべき");
        (await CountLedgersAsync()).Should().Be(1, "何も復活させないべき");

        (await _service.UnmergeAsync(secondHistoryId)).Success.Should().BeTrue("後の統合は取り消せるべき");
        var retried = await _service.UnmergeAsync(firstHistoryId);
        retried.Success.Should().BeTrue($"後の統合を取り消した後なら先の統合も取り消せるべき: {retried.ErrorMessage}");
        (await CountLedgersAsync()).Should().Be(3, "3 行とも統合前に戻るべき");
        (await _ledgerRepository.GetByIdAsync(targetId))!.Details.Should().ContainSingle();
    }
    /// <summary>
    /// 実際の画面経路（<see cref="LedgerDetailSaveService"/>）で何も変えずに保存し直した場合。
    /// 保存は明細を新しい順にして作り直すため、統合時の並び（統合先の明細が小さい id）と逆になり、
    /// 同じ id へ別の明細の内容が入る。取り消しは中止される（安全側）。台帳は変更されないこと。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_利用履歴詳細から何も変えずに保存し直すとidが入れ替わり中止すること()
    {
        var (targetId, historyId, mergedIds) = await MergeTwoLedgersAsync();
        var merged = (await _ledgerRepository.GetByIdAsync(targetId))!;
        var contentById = merged.Details.ToDictionary(d => d.SequenceNumber, d => (d.ExitStation, d.Balance));
        var saveService = new LedgerDetailSaveService(
            _dbContext, _ledgerRepository,
            new OperationLogger(new OperationLogRepository(_dbContext), _operatorContext),
            NullLogger<LedgerDetailSaveService>.Instance);

        var saved = await saveService.SaveAsync(LedgerCloner.Clone(merged)!, merged, merged.Details.ToList(), summaryChanged: false);

        saved.Should().Be(LedgerDetailSaveResult.Saved, "前提の保存は成功するべき");
        var resaved = (await _ledgerRepository.GetByIdAsync(targetId))!;
        resaved.Details.Select(d => d.SequenceNumber).Should().BeEquivalentTo(mergedIds, "前提: 同じ id を再利用する");
        resaved.Details.Any(d => contentById[d.SequenceNumber] != (d.ExitStation, d.Balance)).Should().BeTrue(
            "前提: 画面からの保存は新しい順に作り直すため、同じ id へ別の明細の内容が入る");

        var result = await _service.UnmergeAsync(historyId);

        result.Success.Should().BeFalse("id の指す内容が統合時と違うので、統合元へ戻す明細を特定できない");
        (await CountLedgersAsync()).Should().Be(1, "統合元を復活させないべき");
        (await _ledgerRepository.GetByIdAsync(targetId))!.Details.Should().HaveCount(2, "明細は統合先に残ったままであるべき");
    }
    /// <summary>
    /// レビュー指摘 3 回目: 後から重ねた統合の統合元が明細を持たない行（履歴の「追加」で手入力した行など）だと、
    /// 統合先の明細の件数も内容も変わらないため、明細の照合では重ねた統合を検出できない。先の統合を取り消すと
    /// 統合先が先の統合の前の金額へ戻り、後の統合で合算した行の払出が台帳から消える（後の統合も件数不一致で取り消せなくなる）。
    /// 統合履歴そのもので前提条件として確かめ、先の統合の取り消しは中止・台帳は不変、後→先の順なら両方戻ること。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_明細を持たない行を後から重ねた統合を残したまま先の統合を取り消すと中止し後から順なら両方戻ること()
    {
        var (targetId, firstHistoryId, _) = await MergeTwoLedgersAsync();
        var noDetailId = await _ledgerRepository.InsertAsync(CreateLedger(balance: 1766, summary: "鉄道（薬院～天神）"));
        var merge2 = await _service.MergeAsync(new List<int> { targetId, noDetailId });
        merge2.Success.Should().BeTrue($"前提の 2 回目の統合は成功するべき: {merge2.ErrorMessage}");
        var secondHistoryId = (await _service.GetUndoableMergeHistoriesAsync()).Single(h => h.Id != firstHistoryId).Id;
        var stacked = (await _ledgerRepository.GetByIdAsync(targetId))!;
        stacked.Details.Should().HaveCount(2, "前提: 明細を持たない行の統合では統合先の明細の件数が変わらない（明細の照合では検出できない形）");
        stacked.Expense.Should().Be(630, "前提: 2 回目の統合で 3 行分の払出が合算されている");

        var first = await _service.UnmergeAsync(firstHistoryId);

        first.Success.Should().BeFalse("後の統合で合算した払出を台帳から消してはならない");
        first.ErrorMessage.Should().Be(LedgerMergeService.StackedMergeUnmergeErrorMessage);
        (await CountLedgersAsync()).Should().Be(1, "何も復活させないべき");
        var unchanged = (await _ledgerRepository.GetByIdAsync(targetId))!;
        unchanged.Expense.Should().Be(630, "統合先の金額を書き戻さないべき");
        unchanged.Summary.Should().Be(stacked.Summary);

        (await _service.UnmergeAsync(secondHistoryId)).Success.Should().BeTrue("後の統合は取り消せるべき");
        var retried = await _service.UnmergeAsync(firstHistoryId);
        retried.Success.Should().BeTrue($"後の統合を取り消した後なら先の統合も取り消せるべき: {retried.ErrorMessage}");
        (await CountLedgersAsync()).Should().Be(3, "3 行とも統合前に戻るべき");
        (await _ledgerRepository.GetByIdAsync(targetId))!.Expense.Should().Be(210, "統合先は統合前の払出に戻るべき");
    }
    /// <summary>
    /// レビュー指摘 4 回目（形 3: 恒久的な穴）: 後の統合の履歴の保存に失敗した（#1954 の HasPostCommitFailure）・まだ保存されていない
    /// 状態では、統合履歴による前提条件は効かない。後の統合の統合元が明細を持たない行だと明細の件数・内容も変わらない。
    /// 統合直後の金額との照合で中止し、後の統合で合算した払出を台帳から消さないこと。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_後の統合の履歴が無くても統合先の金額が変わっていれば中止し台帳を変えないこと()
    {
        var (targetId, firstHistoryId, _) = await MergeTwoLedgersAsync();
        var noDetailId = await _ledgerRepository.InsertAsync(CreateLedger(balance: 1766, summary: "鉄道（薬院～天神）"));
        // 統合履歴を保存しない統合（統合のコミット後に履歴の保存が失敗した、または他 PC で保存前の状態）
        var updatedTarget = (await _ledgerRepository.GetByIdAsync(targetId))!;
        updatedTarget.Expense = 630;
        (await _ledgerRepository.MergeLedgersAsync(targetId, new[] { noDetailId }, updatedTarget)).Should().BeTrue();
        (await _service.GetUndoableMergeHistoriesAsync()).Should().ContainSingle("前提: 後の統合の履歴は存在しない");
        (await _ledgerRepository.GetByIdAsync(targetId))!.Details.Should().HaveCount(2, "前提: 明細の件数は変わらない");

        var result = await _service.UnmergeAsync(firstHistoryId);

        result.Success.Should().BeFalse("後の統合で合算した払出を黙って消してはならない");
        result.ErrorMessage.Should().NotBe(LedgerMergeService.StackedMergeUnmergeErrorMessage,
            "後の統合の履歴が無いので、一覧から取り消す案内はできない");
        result.ErrorMessage.Should().Contain("別の統合が重なった", "原因として別の統合も挙げるべき");
        (await CountLedgersAsync()).Should().Be(1, "何も復活させないべき");
        (await _ledgerRepository.GetByIdAsync(targetId))!.Expense.Should().Be(630, "統合先の金額を書き戻さないべき");
        _repositoryLogger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("金額"),
            "中止の理由（金額の不一致）をログに残すべき");
    }

    /// <summary>
    /// レビュー指摘 4 回目: 統合後に統合先の行の金額だけを行編集した場合も中止すること（以前は統合前の値で黙って上書きしていた）。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_統合後に統合先の金額だけを行編集していたら中止すること()
    {
        var (targetId, historyId, _) = await MergeTwoLedgersAsync();
        var edited = (await _ledgerRepository.GetByIdAsync(targetId))!;
        edited.Expense = 400;
        (await _ledgerRepository.UpdateAsync(edited)).Should().BeTrue("前提の行編集は成功するべき");

        var result = await _service.UnmergeAsync(historyId);

        result.Success.Should().BeFalse("統合後に直した金額を統合前の値で黙って上書きしてはならない");
        (await CountLedgersAsync()).Should().Be(1);
        (await _ledgerRepository.GetByIdAsync(targetId))!.Expense.Should().Be(400, "行編集した金額は残るべき");
    }

    /// <summary>
    /// 対の正常系: 統合後にバス停名を入力して摘要だけが書き換わった場合は取り消せる（摘要は金額の照合に含めない）。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_統合後に摘要だけが書き換わっていても取り消せること()
    {
        var (targetId, historyId, mergedIds) = await MergeTwoLedgersAsync();
        (await _ledgerRepository.UpdateDetailBusStopsAsync(targetId, new[] { (mergedIds.Max(), "天神～博多駅前") })).Should().BeTrue();
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            (await _ledgerRepository.UpdateSummaryAsync(targetId, "鉄道（薬院～博多）、バス（天神～博多駅前）", scope.Transaction))
                .Should().BeTrue("前提: バス停名の入力と同じく摘要を書き換える");
            scope.Commit();
        }

        var result = await _service.UnmergeAsync(historyId);

        result.Success.Should().BeTrue(result.ErrorMessage);
        (await CountLedgersAsync()).Should().Be(2, "統合元が復活するべき");
    }
}
