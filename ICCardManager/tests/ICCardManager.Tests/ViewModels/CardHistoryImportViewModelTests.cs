using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Common.Exceptions;
using ICCardManager.Common.Messages;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.Tests.Infrastructure.Timing;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2293: カードの履歴からの取り込みダイアログの ViewModel。
/// </summary>
/// <remarks>
/// <para>
/// カードリーダーはモック（タッチは <see cref="CardHistoryImportViewModel.HandleCardReadAsync"/> を直接呼ぶか、
/// <see cref="ICardReader.CardRead"/> を発火させて再現する）。取り込む範囲と記録は実 DB 上の本物の
/// <see cref="LendingService"/> で決める — モックの計画を返すと、一覧に出した利用と記録した利用が同じ規則で
/// 決まっていることを確かめられない。
/// </para>
/// <para>
/// 操作者は本物の <see cref="CurrentOperatorContext"/> で受け渡し、認証した職員が操作ログの操作者になることを
/// DB の行で確かめる（<c>using var auth = …Enter()</c> のスコープが取り込みの呼び出しを覆っていること）。
/// </para>
/// </remarks>
public sealed class CardHistoryImportViewModelTests : IDisposable
{
    private const string TargetIdm = "07FE112233445566";
    private const string OtherIdm = "07FE000000000099";
    private const string CardName = "はやかけん H-001";
    private const string OperatorIdm = "FFFF000000000009";
    private const string OperatorName = "取込 担当";

    private static readonly DateTime Now = new(2026, 9, 20, 10, 0, 0);

    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly CardRepository _cardRepository;
    private readonly OperationLogRepository _operationLogRepository;
    private readonly CardLockManager _lockManager;
    private readonly CurrentOperatorContext _operatorContext;
    private readonly LendingService _lendingService;
    private readonly Mock<ICardReader> _cardReader = new();
    private readonly Mock<IStaffAuthService> _staffAuthService = new();
    private readonly StrongReferenceMessenger _messenger = new();
    private readonly List<CardReadingSuppressedMessage> _suppressionMessages = new();

    public CardHistoryImportViewModelTests()
    {
        _dbContext = TestDbContextFactory.Create();

        var cacheServiceMock = new Mock<ICacheService>();
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<IcCard>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan _) => factory());
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<Staff>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<Staff>>> factory, TimeSpan _) => factory());
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(), It.IsAny<Func<Task<AppSettings>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<AppSettings>> factory, TimeSpan _) => factory());
        var cacheOptions = Options.Create(new CacheOptions());

        _ledgerRepository = new LedgerRepository(_dbContext);
        _cardRepository = new CardRepository(_dbContext, cacheServiceMock.Object, cacheOptions, NullLogger<CardRepository>.Instance);
        var staffRepository = new StaffRepository(_dbContext, cacheServiceMock.Object, cacheOptions, NullLogger<StaffRepository>.Instance);
        var settingsRepository = new SettingsRepository(_dbContext, cacheServiceMock.Object, cacheOptions);
        _operationLogRepository = new OperationLogRepository(_dbContext);
        _lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);
        _operatorContext = new CurrentOperatorContext(new FixedSystemClock(Now));

        _lendingService = new LendingService(
            _dbContext,
            _cardRepository,
            staffRepository,
            _ledgerRepository,
            settingsRepository,
            new OperationLogger(_operationLogRepository, _operatorContext),
            new SummaryGenerator(DepartmentType.MayorOffice),
            _lockManager,
            Options.Create(new AppOptions { CardLockTimeoutSeconds = 5, RetouchWindowSeconds = 30 }),
            NullLogger<LendingService>.Instance,
            new FixedSystemClock(Now));

        _staffAuthService.Setup(s => s.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync(() => new StaffAuthResult(OperatorIdm, OperatorName, _operatorContext));

        _messenger.Register<CardReadingSuppressedMessage>(this, (_, m) => _suppressionMessages.Add(m));

        _cardRepository.InsertAsync(new IcCard
        {
            CardIdm = TargetIdm,
            CardType = "はやかけん",
            CardNumber = "H-001",
        }).GetAwaiter().GetResult();
        _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TargetIdm,
            Date = new DateTime(2026, 9, 1),
            Summary = "新規購入",
            Income = 5000,
            Balance = 5000,
        }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _lockManager.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region 抑制と待ち受け

    [Fact]
    public void Start_メイン画面のカード読み取りを抑制し_Cleanupで解くこと()
    {
        var vm = CreateViewModel();

        vm.Start();
        vm.Cleanup();

        _suppressionMessages.Select(m => (m.Value, m.Source)).Should().Equal(
            (true, CardReadingSource.CardHistoryImport),
            (false, CardReadingSource.CardHistoryImport));
        _cardReader.VerifyAdd(r => r.CardRead += It.IsAny<EventHandler<CardReadEventArgs>>(), Times.Once());
        _cardReader.VerifyRemove(r => r.CardRead -= It.IsAny<EventHandler<CardReadEventArgs>>(), Times.Once());
    }

    [Fact]
    public void Cleanup_を2回呼んでも解除は1回だけで_開く前なら抑制を送らないこと()
    {
        var notStarted = CreateViewModel();
        notStarted.Cleanup();
        _suppressionMessages.Should().BeEmpty("取得していない抑制を解く通知を送らない");

        var vm = CreateViewModel();
        vm.Start();
        vm.Cleanup();
        vm.Cleanup();
        _suppressionMessages.Count(m => !m.Value).Should().Be(1);
    }

    [Fact]
    public void Start_でタッチを促す案内を出すこと()
    {
        var vm = CreateViewModel();

        vm.Start();

        vm.Phase.Should().Be(CardHistoryImportPhase.WaitingForTouch);
        vm.IsWaitingForTouch.Should().BeTrue();
        vm.StatusMessage.Should().Be(CardHistoryImportViewModel.TouchPromptMessage);
        vm.ImportCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void カードリーダーのタッチイベントで読み取りへ進むこと()
    {
        SetupCard(balance: 4580, Usage(new DateTime(2026, 9, 15), 4580), Usage(new DateTime(2026, 9, 10), 4790));
        var vm = CreateViewModel();
        vm.Start();

        _cardReader.Raise(r => r.CardRead += null, new CardReadEventArgs { Idm = TargetIdm });

        vm.Phase.Should().Be(CardHistoryImportPhase.Ready, "SynchronousDispatcherService は同期で読み取りまで進める");
        vm.Rows.Should().HaveCount(2);
    }

    [Fact]
    public void 閉じた後のタッチは処理しないこと()
    {
        SetupCard(balance: 4580, Usage(new DateTime(2026, 9, 15), 4580));
        var vm = CreateViewModel();
        vm.Start();
        vm.Cleanup();

        _cardReader.Raise(r => r.CardRead += null, new CardReadEventArgs { Idm = TargetIdm });

        vm.Phase.Should().Be(CardHistoryImportPhase.WaitingForTouch);
        _cardReader.Verify(r => r.TryReadHistoryAsync(It.IsAny<string>()), Times.Never);
    }

    #endregion

    #region 読み取りと計画

    [Fact]
    public async Task 別のカードがタッチされたら読まずに対象のカードを案内して待ち続けること()
    {
        var vm = CreateViewModel();
        vm.Start();

        await vm.HandleCardReadAsync(OtherIdm);

        vm.Phase.Should().Be(CardHistoryImportPhase.WaitingForTouch);
        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().Be(CardHistoryImportViewModel.BuildWrongCardMessage(CardName));
        vm.StatusMessage.Should().Contain(CardName);
        _cardReader.Verify(r => r.TryReadHistoryAsync(It.IsAny<string>()), Times.Never);
        _cardReader.Verify(r => r.ReadBalanceAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task IDmの大文字小文字が違っても同じカードとみなすこと()
    {
        SetupCard(balance: 4790, Usage(new DateTime(2026, 9, 10), 4790));
        var vm = CreateViewModel();
        vm.Start();

        await vm.HandleCardReadAsync(TargetIdm.ToLowerInvariant());

        vm.Phase.Should().Be(CardHistoryImportPhase.Ready);
    }

    [Fact]
    public async Task 未記録の利用を古い順に一覧へ出し_取り込めるようにすること()
    {
        // カードからは新しい順に読まれる
        SetupCard(balance: 4370,
            Usage(new DateTime(2026, 9, 15), 4370),
            Usage(new DateTime(2026, 9, 10), 4580),
            Usage(new DateTime(2026, 8, 30), 5000));  // 導入行（9/1）より前
        var vm = CreateViewModel();
        vm.Start();

        await vm.HandleCardReadAsync(TargetIdm);

        vm.Phase.Should().Be(CardHistoryImportPhase.Ready);
        vm.Rows.Select(r => r.UseDate).Should().Equal(new DateTime(2026, 9, 10), new DateTime(2026, 9, 15));
        vm.RowCountText.Should().Be("2件");
        vm.CardBalanceText.Should().Be("4,370円");
        vm.ActualBalance.Should().Be(4370);
        vm.IsStatusError.Should().BeFalse();
        vm.ImportCommand.CanExecute(null).Should().BeTrue();
        (await GetUsageLedgersAsync()).Should().BeEmpty("一覧に出すだけで、まだ書かない");
    }

    [Fact]
    public async Task 履歴を読めなかったらタッチ待ちへ戻り_もう一度のタッチで読めること()
    {
        _cardReader.Setup(r => r.ReadBalanceAsync(TargetIdm)).ReturnsAsync(4790);
        _cardReader.SetupSequence(r => r.TryReadHistoryAsync(TargetIdm))
            .ReturnsAsync(CardReadResult<IReadOnlyList<LedgerDetail>>.Fail(CardReaderException.HistoryReadFailed("読み取り失敗（テスト）")))
            .ReturnsAsync(CardReadResult<IReadOnlyList<LedgerDetail>>.Ok(new List<LedgerDetail> { Usage(new DateTime(2026, 9, 10), 4790) }));
        var vm = CreateViewModel();
        vm.Start();

        await vm.HandleCardReadAsync(TargetIdm);

        vm.Phase.Should().Be(CardHistoryImportPhase.WaitingForTouch);
        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().Be(CardHistoryImportViewModel.BuildReadFailedMessage());
        vm.IsBusy.Should().BeFalse();

        await vm.HandleCardReadAsync(TargetIdm);

        vm.Phase.Should().Be(CardHistoryImportPhase.Ready);
        vm.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task 残額を読めなかったときも取り込まずにタッチ待ちへ戻ること()
    {
        _cardReader.Setup(r => r.ReadBalanceAsync(TargetIdm)).ThrowsAsync(new InvalidOperationException("カードが離れた（テスト）"));
        _cardReader.Setup(r => r.TryReadHistoryAsync(TargetIdm))
            .ReturnsAsync(CardReadResult<IReadOnlyList<LedgerDetail>>.Ok(new List<LedgerDetail> { Usage(new DateTime(2026, 9, 10), 4790) }));
        var vm = CreateViewModel();
        vm.Start();

        await vm.HandleCardReadAsync(TargetIdm);

        vm.Phase.Should().Be(CardHistoryImportPhase.WaitingForTouch,
            "残額が無いと取り込み後に食い違いが解消したかを判定できない");
        vm.ActualBalance.Should().BeNull();
    }

    [Fact]
    public async Task 一覧を出した後のタッチは無視すること()
    {
        // 認証ダイアログでの職員証のタッチも、このダイアログの購読に届く
        SetupCard(balance: 4790, Usage(new DateTime(2026, 9, 10), 4790));
        var vm = CreateViewModel();
        vm.Start();
        await vm.HandleCardReadAsync(TargetIdm);

        await vm.HandleCardReadAsync("FFFF000000000001");

        vm.Phase.Should().Be(CardHistoryImportPhase.Ready);
        vm.IsStatusError.Should().BeFalse("職員証のタッチを『別のカード』として案内しない");
        _cardReader.Verify(r => r.TryReadHistoryAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task 読み取りの最中に届いた2件目のタッチでは読み直さないこと()
    {
        var balance = new TaskCompletionSource<int?>();
        _cardReader.Setup(r => r.ReadBalanceAsync(TargetIdm)).Returns(balance.Task);
        _cardReader.Setup(r => r.TryReadHistoryAsync(TargetIdm))
            .ReturnsAsync(CardReadResult<IReadOnlyList<LedgerDetail>>.Ok(new List<LedgerDetail> { Usage(new DateTime(2026, 9, 10), 4790) }));
        var vm = CreateViewModel();
        vm.Start();

        var first = vm.HandleCardReadAsync(TargetIdm);
        // 2 件目の完了は、1 件目の読み取りを終わらせた後に待つ。再入を止める判定が無いと 2 件目も同じ読み取りを待つため、
        // 先に待つとテストが永久に終わらない（変異の測定で実際に止まった）。上限を付けて「失敗」として報告させる
        var second = vm.HandleCardReadAsync(TargetIdm);
        balance.SetResult(4790);
        var both = Task.WhenAll(first, second);
        (await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(both,
            "読み取りが終われば 2 件とも戻ること");

        _cardReader.Verify(r => r.ReadBalanceAsync(TargetIdm), Times.Once);
        vm.Phase.Should().Be(CardHistoryImportPhase.Ready);
    }

    [Fact]
    public async Task 未記録の利用が無ければ取り込めず_その旨を案内すること()
    {
        SetupCard(balance: 5000, Usage(new DateTime(2026, 8, 30), 5000));
        var vm = CreateViewModel();
        vm.Start();

        await vm.HandleCardReadAsync(TargetIdm);

        vm.Phase.Should().Be(CardHistoryImportPhase.Ready);
        vm.Rows.Should().BeEmpty();
        vm.StatusMessage.Should().Be(CardHistoryImportViewModel.BuildNothingToImportMessage(new DateTime(2026, 9, 1)));
        vm.StatusMessage.Should().Contain("2026/09/01 以降", "実際の下限（導入行の日付）を示す");
        vm.ImportCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task 貸出中のカードは理由を案内して取り込まないこと()
    {
        await _cardRepository.UpdateLentStatusAsync(TargetIdm, true, Now, null);
        SetupCard(balance: 4790, Usage(new DateTime(2026, 9, 10), 4790));
        var vm = CreateViewModel();
        vm.Start();

        await vm.HandleCardReadAsync(TargetIdm);

        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().Be(CardHistoryImportViewModel.BuildBlockedMessage(UsageImportBlockReason.CardIsLent, CardName));
        vm.ImportCommand.CanExecute(null).Should().BeFalse();
    }

    #endregion

    #region 取り込み

    [Fact]
    public async Task 取り込むと職員証の認証のうえ台帳へ記録し_操作ログの操作者が認証した職員になること()
    {
        SetupCard(balance: 4580, Usage(new DateTime(2026, 9, 15), 4580), Usage(new DateTime(2026, 9, 10), 4790));
        var vm = await CreateReadyViewModelAsync();

        await vm.ImportAsync();

        _staffAuthService.Verify(s => s.RequestAuthenticationAsync("カード履歴の取り込み"), Times.Once);
        vm.Phase.Should().Be(CardHistoryImportPhase.Completed);
        vm.IsCompleted.Should().BeTrue();
        vm.HasImported.Should().BeTrue();
        vm.ImportResult!.CreatedLedgers.Should().HaveCount(2);

        (await GetUsageLedgersAsync()).Select(l => (l.Date.Date, l.Balance)).Should().Equal(
            (new DateTime(2026, 9, 10), 4790), (new DateTime(2026, 9, 15), 4580));

        var logs = (await _operationLogRepository.GetByDateRangeAsync(new DateTime(2000, 1, 1), new DateTime(2100, 1, 1)))
            .Where(l => l.TargetTable == OperationLogger.Tables.Ledger).ToList();
        logs.Should().HaveCount(2);
        logs.Should().OnlyContain(l => l.OperatorIdm == OperatorIdm && l.OperatorName == OperatorName,
            "認証のスコープが取り込みの呼び出しを覆っている");
        _operatorContext.HasSession.Should().BeFalse("取り込みが終わったら認証のスコープを閉じる");

        vm.StatusMessage.Should().Be(CardHistoryImportViewModel.BuildCompletionMessage(2, 4580, 4580));
        vm.StatusMessage.Should().Contain("一致しました");
        vm.ImportCommand.CanExecute(null).Should().BeFalse("取り込んだ後にもう一度押させない");
    }

    [Fact]
    public async Task 取り込みの最中は処理中にして_EscやバツでダイアログをClosingさせないこと()
    {
        // BusyCloseGuard は DataContext の IsBusy で Esc・✕ を止める。取り込みの最中に処理中でないと、閉じた時点で
        // 呼び出し元は「取り込まなかった」として後処理を飛ばし、その後に確定した行が履歴に出ないまま警告も残る
        SetupCard(balance: 4790, Usage(new DateTime(2026, 9, 10), 4790));
        var vm = await CreateReadyViewModelAsync();
        var busyMessages = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            // IsBusy と BusyMessage のどちらが先に変わっても拾う
            if ((e.PropertyName == nameof(vm.IsBusy) || e.PropertyName == nameof(vm.BusyMessage)) && vm.IsBusy)
            {
                busyMessages.Add(vm.BusyMessage);
            }
        };

        await vm.ImportAsync();

        busyMessages.Should().Contain("取り込み中...", "記録している間は処理中（閉じるガードの対象）");
        vm.IsBusy.Should().BeFalse("終われば処理中を解き、「閉じる」で閉じられる");
        vm.Phase.Should().Be(CardHistoryImportPhase.Completed);
    }

    [Fact]
    public async Task 一覧を見ている間に他の操作で記録されたら新たに記録せず_その旨を案内し_完了として渡すこと()
    {
        var history = new List<LedgerDetail> { Usage(new DateTime(2026, 9, 10), 4790) };
        SetupCard(balance: 4790, history.ToArray());
        var vm = await CreateReadyViewModelAsync();
        (await _lendingService.ImportUnrecordedUsageAsync(TargetIdm, new List<LedgerDetail> { Usage(new DateTime(2026, 9, 10), 4790) }))
            .CreatedLedgers.Should().ContainSingle("前提: 他の操作で先に記録される");

        await vm.ImportAsync();

        vm.HasImported.Should().BeFalse();
        vm.Outcome.Completed.Should().BeTrue("呼び出し元が履歴を読み直して、その記録を見せる");
        vm.StatusMessage.Should().Contain("他の操作").And.Contain("一致しました");
    }

    [Fact]
    public async Task 認証が完了しなければ何も記録せず_もう一度押すよう案内すること()
    {
        _staffAuthService.Setup(s => s.RequestAuthenticationAsync(It.IsAny<string>())).ReturnsAsync((StaffAuthResult?)null);
        SetupCard(balance: 4790, Usage(new DateTime(2026, 9, 10), 4790));
        var vm = await CreateReadyViewModelAsync();

        await vm.ImportAsync();

        vm.Phase.Should().Be(CardHistoryImportPhase.Ready);
        vm.HasImported.Should().BeFalse();
        vm.StatusMessage.Should().Be(CardHistoryImportViewModel.AuthenticationCancelledMessage);
        vm.ImportCommand.CanExecute(null).Should().BeTrue("もう一度押せる");
        (await GetUsageLedgersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task 書き込みに失敗したら台帳に何も記録せず_もう一度押せるまま案内すること()
    {
        // 一過性の失敗（共有フォルダーの切断・DB ロック）は押し直せば通り得る。断られた場合（貸出中など）と違って押せるまま残す
        var failingLogs = new Mock<IOperationLogRepository>();
        failingLogs.Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .ThrowsAsync(new System.IO.IOException("ネットワーク切断（テスト）"));
        var failingService = new LendingService(
            _dbContext, _cardRepository,
            new StaffRepository(_dbContext, PassThroughCache(), Options.Create(new CacheOptions()), NullLogger<StaffRepository>.Instance),
            _ledgerRepository,
            new SettingsRepository(_dbContext, PassThroughCache(), Options.Create(new CacheOptions())),
            new OperationLogger(failingLogs.Object, _operatorContext),
            new SummaryGenerator(DepartmentType.MayorOffice),
            _lockManager,
            Options.Create(new AppOptions { CardLockTimeoutSeconds = 5, RetouchWindowSeconds = 30 }),
            NullLogger<LendingService>.Instance,
            new FixedSystemClock(Now));
        SetupCard(balance: 4790, Usage(new DateTime(2026, 9, 10), 4790));
        var vm = CreateViewModel(failingService);
        vm.Start();
        await vm.HandleCardReadAsync(TargetIdm);

        await vm.ImportAsync();

        vm.HasImported.Should().BeFalse();
        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().StartWith("カードの履歴を取り込めませんでした。").And.Contain("何も記録していません");
        vm.ImportCommand.CanExecute(null).Should().BeTrue("一過性の失敗なので、もう一度押せる");
        (await GetUsageLedgersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task 一覧を出した後に貸出されたら記録せずに理由を案内すること()
    {
        SetupCard(balance: 4790, Usage(new DateTime(2026, 9, 10), 4790));
        var vm = await CreateReadyViewModelAsync();
        await _cardRepository.UpdateLentStatusAsync(TargetIdm, true, Now, null);

        await vm.ImportAsync();

        vm.HasImported.Should().BeFalse();
        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().Be(CardHistoryImportViewModel.BuildBlockedMessage(UsageImportBlockReason.CardIsLent, CardName));
        (await GetUsageLedgersAsync()).Should().BeEmpty();
        vm.ImportCommand.CanExecute(null).Should().BeFalse("押し直しても同じ理由で断られるので、取り込めない状態にする");
    }

    [Fact]
    public async Task 取り込んでも食い違いが残るときは残る差額を案内すること()
    {
        // カードの残額は 4,000 円だが、カードに残る未記録の利用は 9/10 の 1 件（残額 4,790 円）だけ
        // （20 件より前に押し出された利用がある想定）
        SetupCard(balance: 4000, Usage(new DateTime(2026, 9, 10), 4790));
        var vm = await CreateReadyViewModelAsync();

        await vm.ImportAsync();

        vm.HasImported.Should().BeTrue();
        vm.StatusMessage.Should().Be(CardHistoryImportViewModel.BuildCompletionMessage(1, 4790, 4000));
        vm.StatusMessage.Should().Contain("790円").And.Contain("CSVインポートまたは履歴の追加");
    }

    [Fact]
    public void BuildCompletionMessage_残額が一致したら一致を_違えば差額を_不明なら確認を案内すること()
    {
        CardHistoryImportViewModel.BuildCompletionMessage(2, 4580, 4580)
            .Should().Contain("2件").And.Contain("4,580円").And.Contain("一致しました").And.NotContain("差があります");
        CardHistoryImportViewModel.BuildCompletionMessage(1, 4790, 4000)
            .Should().Contain("790円 の差があります").And.NotContain("一致しました");
        CardHistoryImportViewModel.BuildCompletionMessage(1, null, 4000)
            .Should().Contain("確認できませんでした").And.NotContain("一致しました");
        CardHistoryImportViewModel.BuildCompletionMessage(1, 4790, 4000)
            .Should().Contain("可能性があります", "差の原因は言い切らない（導入時の残額の誤りなど別の原因もあり得る）");
        CardHistoryImportViewModel.BuildCompletionMessage(2, 4580, 4580)
            .Should().Contain("利用の行の利用者は空欄", "チャージ・ポイント還元の行は空欄のままでよい");
    }

    [Fact]
    public void 案内の文言は利用者が次に取る操作で終わること()
    {
        var messages = new[]
        {
            CardHistoryImportViewModel.TouchPromptMessage,
            CardHistoryImportViewModel.AuthenticationCancelledMessage,
            CardHistoryImportViewModel.BuildWrongCardMessage(CardName),
            CardHistoryImportViewModel.BuildReadFailedMessage(),
            CardHistoryImportViewModel.BuildNothingToImportMessage(new DateTime(2026, 9, 1)),
            CardHistoryImportViewModel.BuildNothingToImportMessage(null),
            CardHistoryImportViewModel.BuildBlockedMessage(UsageImportBlockReason.CardIsLent, CardName),
            CardHistoryImportViewModel.BuildBlockedMessage(UsageImportBlockReason.NoBaseline, CardName),
            CardHistoryImportViewModel.BuildBlockedMessage(UsageImportBlockReason.CardNotInOperation, CardName),
            CardHistoryImportViewModel.BuildImportFailedMessage("他のPCがデータベースを使用中で、書き込みが競合しました。"),
            CardHistoryImportViewModel.BuildCompletionMessage(1, 4790, 4000),
        };

        foreach (var message in messages)
        {
            message.Should().MatchRegex("ください。$", "「どうすれば」で終わる（error-messages.md）");
            message.Should().NotMatchRegex("(?<!交通系)ICカード(?!リーダー)", "「交通系ICカード」と書く");
        }
    }

    #endregion

    #region ヘルパー

    private CardHistoryImportViewModel CreateViewModel(LendingService? lendingService = null)
    {
        var vm = new CardHistoryImportViewModel(
            _cardReader.Object,
            lendingService ?? _lendingService,
            _staffAuthService.Object,
            new SynchronousDispatcherService(),
            _messenger,
            NullLogger<CardHistoryImportViewModel>.Instance);
        vm.SetTarget(TargetIdm, CardName);
        return vm;
    }

    private async Task<CardHistoryImportViewModel> CreateReadyViewModelAsync()
    {
        var vm = CreateViewModel();
        vm.Start();
        await vm.HandleCardReadAsync(TargetIdm);
        vm.Phase.Should().Be(CardHistoryImportPhase.Ready, vm.StatusMessage);
        return vm;
    }

    private static ICacheService PassThroughCache()
    {
        var cache = new Mock<ICacheService>();
        cache.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<Staff>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<Staff>>> factory, TimeSpan _) => factory());
        cache.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(), It.IsAny<Func<Task<AppSettings>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<AppSettings>> factory, TimeSpan _) => factory());
        return cache.Object;
    }

    private void SetupCard(int balance, params LedgerDetail[] historyNewestFirst)
    {
        // リーダーは載っているカードを読む（IDm の大文字小文字の表記を問わない）
        _cardReader.Setup(r => r.ReadBalanceAsync(It.IsAny<string>())).ReturnsAsync(balance);
        _cardReader.Setup(r => r.TryReadHistoryAsync(It.IsAny<string>()))
            .ReturnsAsync(() => CardReadResult<IReadOnlyList<LedgerDetail>>.Ok(historyNewestFirst.ToList()));
    }

    private async Task<List<Ledger>> GetUsageLedgersAsync()
    {
        var ledgers = await _ledgerRepository.GetByDateRangeAsync(TargetIdm, new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));
        return ledgers.Where(l => !l.IsLentRecord && !l.IsInitialRecord).OrderBy(l => l.Date).ThenBy(l => l.Id).ToList();
    }

    private static LedgerDetail Usage(DateTime useDate, int balanceAfter) => new()
    {
        UseDate = useDate,
        EntryStation = "博多",
        ExitStation = "天神",
        Amount = 210,
        Balance = balanceAfter,
    };

    #endregion
}
