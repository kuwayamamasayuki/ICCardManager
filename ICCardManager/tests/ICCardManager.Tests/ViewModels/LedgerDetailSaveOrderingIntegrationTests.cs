using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.Tests.Services;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #1913: 履歴詳細ダイアログの保存が rowid 規約を反転させないことを、
/// 実 SQLite で「保存 → 再読込 → 摘要再生成」まで通して検証する。
/// </summary>
/// <remarks>
/// <para>
/// モックで引数の並びを見るテスト（<c>LedgerDetailViewModelTests</c>）は保存側の並びしか
/// 表明できない。本 Issue の実害は<b>再読込のあと</b>に出る
/// （<c>SummaryGenerator.SortChronologically</c> は同一日付内で <c>SequenceNumber</c> 降順を
/// タイブレークに使うため、rowid が反転していると摘要のブロック順が逆になり、
/// バス停名の同期（Issue #1904）は先頭ブロックを最後の利用へ対応付ける）。
/// </para>
/// <para>
/// 実 DB を使う理由は、rowid の再採番が <c>ReplaceDetailsAsync</c> の DELETE + INSERT で
/// 初めて起きるため。モックでは <c>SequenceNumber</c> が振り直されない。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class LedgerDetailSaveOrderingIntegrationTests : IDisposable
{
    private const string TestCardIdm = "0102030405060708";

    private readonly DbContext _dbContext;
    private readonly LedgerRepository _repository;
    private readonly SummaryGenerator _summaryGenerator;
    private readonly LedgerDetailViewModel _viewModel;

    public LedgerDetailSaveOrderingIntegrationTests()
    {
        SummaryGenerator.ResetToDefaults();

        _dbContext = TestDbContextFactory.Create();
        _repository = new LedgerRepository(_dbContext);
        _summaryGenerator = new SummaryGenerator();

        var cacheServiceMock = new Mock<ICacheService>();
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan _) => factory());
        var cardRepository = new CardRepository(
            _dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        cardRepository.InsertAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H001"
        }).Wait();

        var operationLogger = new OperationLogger(
            Mock.Of<IOperationLogRepository>(),
            Mock.Of<ICurrentOperatorContext>());
        var splitService = new LedgerSplitService(
            _repository,
            _summaryGenerator,
            operationLogger,
            _dbContext,
            NullLogger<LedgerSplitService>.Instance);

        _viewModel = new LedgerDetailViewModel(
            _repository,
            _summaryGenerator,
            operationLogger,
            splitService,
            _dbContext,
            Mock.Of<IStaffAuthService>(),
            NullLogger<LedgerDetailViewModel>.Instance);
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        SummaryGenerator.ResetToDefaults();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 保存して読み直しても、摘要のブロック順（＝明細の時系列）が変わらないこと。
    /// </summary>
    [Fact]
    public async Task 保存して再読込しても摘要のブロック順が変わらないこと()
    {
        // Arrange: 同一日付の 3 区間。日付が同じなので順序の決定要因は SequenceNumber だけ
        var ledgerId = await CreateLedgerWithDetailsAsync();

        var before = await _repository.GetByIdAsync(ledgerId);
        var expectedSummary = _summaryGenerator.Generate(before!.Details.ToList());
        expectedSummary.Should().Be(
            "鉄道（博多～天神、薬院～大橋、姪浜～西新）",
            "前提: 保存前は時系列昇順のブロック順で摘要が生成される");

        await _viewModel.InitializeAsync(ledgerId);

        // Act: 分割線を入れて（＝変更を発生させて）保存する
        _viewModel.SplitAllCommand.Execute(null);
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert: rowid が再採番されたあとも、再読込 → 摘要再生成でブロック順が保たれる
        var reloaded = await _repository.GetByIdAsync(ledgerId);
        reloaded.Should().NotBeNull();
        _summaryGenerator.Generate(reloaded!.Details.ToList()).Should().Be(
            expectedSummary,
            "保存で rowid を再採番しても SequenceNumber 規約（小さい rowid ＝ 新しい）が" +
            "保たれること（Issue #1913）");

        // 規約そのものも直接表明する（摘要の書式が変わっても検出力を失わないため）
        var seqByStation = reloaded.Details.ToDictionary(d => d.EntryStation, d => d.SequenceNumber);
        seqByStation["姪浜"].Should().BeLessThan(
            seqByStation["薬院"], "最新の明細ほど小さい rowid になること");
        seqByStation["薬院"].Should().BeLessThan(
            seqByStation["博多"], "最新の明細ほど小さい rowid になること");
    }

    /// <summary>
    /// 時系列昇順（古い→新しい）の 3 区間を持つ利用履歴を作る。
    /// </summary>
    /// <remarks>
    /// 挿入は本番と同じく「新しい順」で行う（<c>LendingService</c> の Reverse 済みの並び）。
    /// </remarks>
    private async Task<int> CreateLedgerWithDetailsAsync()
    {
        var date = new DateTime(2026, 2, 10);
        var ledgerId = await _repository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = date,
            Summary = "鉄道（博多～天神、薬院～大橋、姪浜～西新）",
            Expense = 700,
            Balance = 9300,
            IsLentRecord = false
        });

        // 時系列昇順（古い→新しい）
        var chronological = new List<LedgerDetail>
        {
            new() { UseDate = date, EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 9740 },
            new() { UseDate = date, EntryStation = "薬院", ExitStation = "大橋", Amount = 210, Balance = 9530 },
            new() { UseDate = date, EntryStation = "姪浜", ExitStation = "西新", Amount = 230, Balance = 9300 }
        };

        await _repository.InsertDetailsAsync(ledgerId, chronological.AsEnumerable().Reverse());
        return ledgerId;
    }
}
