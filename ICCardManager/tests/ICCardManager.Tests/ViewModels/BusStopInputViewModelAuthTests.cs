#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.Tests.Infrastructure.Timing;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2278: 警告エリアの「バス停名未入力」一覧から開いた記録済みの行の書き換えは、職員証の認証を求め、
/// 認証した職員の操作として操作ログに残す。返却フロー内の入力は従来どおり認証も操作ログも無い。
/// </summary>
/// <remarks>
/// 「一覧の経路は認証する」側と「返却フローの経路は認証しない」側を対で置く。前者だけだと、
/// すべての経路で認証を求める実装（返却のたびに職員証をもう一度求める）でも緑になる。
/// 監査ログの記録は <see cref="OperationLogger"/> が書き込む <see cref="IOperationLogRepository"/> のモックで見る
/// （OperationLogger のメソッドは virtual ではないため。db-write-conventions #1944）。
/// </remarks>
public class BusStopInputViewModelAuthTests : IDisposable
{
    private const int LedgerId = 7;
    private const string AuthIdm = "AUTH_STAFF_IDM";
    private const string AuthName = "天神 太郎";
    private const string SaveOperation = "バス停名の入力";
    private const string SkipOperation = "バス停名の入力のスキップ";

    private readonly Mock<ILedgerRepository> _ledgerRepoMock = new();
    private readonly Mock<ISettingsRepository> _settingsRepoMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock = new();
    private readonly Mock<IOperationLogRepository> _operationLogRepoMock = new();
    private readonly CurrentOperatorContext _operatorContext =
        new(new FixedSystemClock(new DateTime(2026, 11, 13, 10, 0, 0)));
    private readonly DbContext _dbContext;
    private readonly BusStopInputViewModel _viewModel;

    private readonly List<SQLiteTransaction> _detailTransactions = new();
    private readonly List<(OperationLog Log, SQLiteTransaction Transaction)> _insertedLogs = new();

    public BusStopInputViewModelAuthTests()
    {
        _ledgerRepoMock.Setup(r => r.GetBusStopSuggestionsAsync(It.IsAny<string>()))
            .ReturnsAsync(Enumerable.Empty<(string BusStops, int UsageCount, DateTime? LastUsedDate)>());
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<(int SequenceNumber, string BusStops)>>(), It.IsAny<SQLiteTransaction>()))
            .Callback<int, IEnumerable<(int SequenceNumber, string BusStops)>, SQLiteTransaction>(
                (_, _, tx) => _detailTransactions.Add(tx))
            .ReturnsAsync(true);
        _ledgerRepoMock.Setup(r => r.UpdateSummaryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);
        _settingsRepoMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _dialogServiceMock.Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);
        _staffAuthServiceMock.Setup(s => s.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync(() => new StaffAuthResult(AuthIdm, AuthName, _operatorContext));
        _operationLogRepoMock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .Callback<OperationLog, SQLiteTransaction>((log, tx) => _insertedLogs.Add((log, tx)))
            .ReturnsAsync(1);

        _dbContext = TestDbContextFactory.Create();
        _viewModel = new BusStopInputViewModel(
            _ledgerRepoMock.Object,
            _settingsRepoMock.Object,
            _dialogServiceMock.Object,
            _dbContext,
            _staffAuthServiceMock.Object,
            new OperationLogger(_operationLogRepoMock.Object, _operatorContext));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Ledger RecordedLedger(params string?[] busStops)
    {
        var details = busStops
            .Select((value, index) => new LedgerDetail
            {
                LedgerId = LedgerId,
                IsBus = true,
                Amount = 200,
                UseDate = new DateTime(2026, 11, 2),
                BusStops = value,
                SequenceNumber = index + 1,
            })
            .ToList();
        return new Ledger
        {
            Id = LedgerId,
            CardIdm = "0102030405060708",
            LenderIdm = "LENDER_IDM",
            StaffName = "博多 花子",
            Date = new DateTime(2026, 11, 2),
            // 記録済みの摘要は、保存時と同じく明細から生成したもの
            Summary = new SummaryGenerator(new AppSettings().DepartmentType).Generate(details),
            Details = details,
        };
    }

    /// <summary>警告エリアの未入力一覧から開く（IncompleteBusStopDialog → InitializeWithLedgerIdAsync）</summary>
    private async Task OpenFromIncompleteListAsync(Ledger ledger)
    {
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(LedgerId)).ReturnsAsync(ledger);
        await _viewModel.InitializeAsync(LedgerId);
    }

    private void VerifyNothingWritten()
    {
        _ledgerRepoMock.Verify(r => r.UpdateDetailBusStopsAsync(
            It.IsAny<int>(), It.IsAny<IEnumerable<(int SequenceNumber, string BusStops)>>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _ledgerRepoMock.Verify(r => r.UpdateSummaryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _insertedLogs.Should().BeEmpty("書き込んでいない操作を記録しない");
    }

    #region 未入力一覧からの保存

    [Fact]
    public async Task 未入力一覧から開いた行の保存は職員証の認証を求め認証した職員で操作ログを記録すること()
    {
        await OpenFromIncompleteListAsync(RecordedLedger((string?)null));
        _viewModel.BusUsages[0].BusStops = "天神～博多駅";

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(s => s.RequestAuthenticationAsync(SaveOperation), Times.Once);
        _viewModel.IsSaved.Should().BeTrue();

        _insertedLogs.Should().ContainSingle("保存した台帳行 1 件を記録する");
        var (log, logTransaction) = _insertedLogs[0];
        log.OperatorIdm.Should().Be(AuthIdm, "操作ログの操作者は認証した職員");
        log.OperatorName.Should().Be(AuthName);
        log.TargetTable.Should().Be(OperationLogger.Tables.Ledger);
        log.TargetId.Should().Be(LedgerId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        log.Action.Should().Be(OperationLogger.Actions.Update);
        log.BeforeData.Should().Contain("\"Summary\":\"バス（★）\"", "変更前は開いた時点の摘要");
        log.BeforeData.Should().NotContain("天神～博多駅", "変更前に入力後の値を写さない（#1959）");
        log.AfterData.Should().Contain("\"Summary\":\"バス（天神～博多駅）\"", "変更後は再生成した摘要");
        log.AfterData.Should().Contain("\"BusStops\":\"天神～博多駅\"", "変更後はバス停名まで写す");

        _detailTransactions.Should().ContainSingle();
        logTransaction.Should().BeSameAs(_detailTransactions[0], "操作ログは書き込みと同じトランザクションで記録する");
        _operatorContext.TryGetOperator(out _, out _).Should().BeFalse("認証は 1 操作に限る（#2260）");
    }

    [Fact]
    public async Task 未入力一覧から開いた行で認証をキャンセルしたら保存しないこと()
    {
        _staffAuthServiceMock.Setup(s => s.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync((StaffAuthResult?)null);
        var ledger = RecordedLedger((string?)null);
        await OpenFromIncompleteListAsync(ledger);
        _viewModel.BusUsages[0].BusStops = "天神～博多駅";

        await _viewModel.SaveAsync();

        VerifyNothingWritten();
        _viewModel.IsSaved.Should().BeFalse("認証しなければダイアログを閉じない");
        _viewModel.StatusMessage.Should().Be(BusStopInputViewModel.SaveAuthenticationCancelledMessage);
        _viewModel.BusUsages[0].BusStops.Should().Be("天神～博多駅", "認証し直せばそのまま保存できるよう入力を残す");
        ledger.Summary.Should().Be("バス（★）", "保存していない摘要をメモリ上でも書き換えない");
    }

    [Fact]
    public async Task 未入力一覧からの保存で確認ダイアログを取り消したら認証を求めないこと()
    {
        _dialogServiceMock.Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);
        await OpenFromIncompleteListAsync(RecordedLedger((string?)null));
        _viewModel.BusUsages[0].BusStops = string.Empty; // 未入力の警告で確認が出る

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(s => s.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never,
            "保存するかを決める前に職員証を求めない（払い戻し #2322 と同じ順序）");
        VerifyNothingWritten();
    }

    [Fact]
    public async Task 未入力一覧からの保存で台帳の書き込みが競合したら操作ログを記録しないこと()
    {
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<(int SequenceNumber, string BusStops)>>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(false);
        await OpenFromIncompleteListAsync(RecordedLedger((string?)null));
        _viewModel.BusUsages[0].BusStops = "天神～博多駅";

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(s => s.RequestAuthenticationAsync(SaveOperation), Times.Once);
        _insertedLogs.Should().BeEmpty("起きていない変更を記録しない");
        _viewModel.IsSaved.Should().BeFalse();
    }

    [Theory]
    [InlineData("★")]
    [InlineData(null)]
    public async Task 未入力一覧から開いた行を何も変えずに保存したら認証を求めず書き込まずに閉じること(string? recorded)
    {
        await OpenFromIncompleteListAsync(RecordedLedger(recorded));
        _viewModel.BusUsages[0].BusStops = string.Empty; // 未入力のまま（未入力の警告は「はい」）

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(s => s.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never,
            "台帳が変わらない保存に職員証を求めない（空欄（NULL）と★は同じ「未入力」）");
        VerifyNothingWritten();
        _viewModel.IsSaved.Should().BeTrue("一覧へ戻る");
        _viewModel.StatusMessage.Should().Be(BusStopInputViewModel.NoChangeSaveMessage);
    }

    [Fact]
    public async Task 未入力一覧から開いた行の保存済みの摘要が明細からの再生成と違えばバス停名が同じでも認証を求めること()
    {
        var ledger = RecordedLedger("★");
        ledger.Summary = "バス（★） 手入力の補足";
        await OpenFromIncompleteListAsync(ledger);
        _viewModel.BusUsages[0].BusStops = string.Empty;

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(s => s.RequestAuthenticationAsync(SaveOperation), Times.Once,
            "保存は摘要を再生成して書くので、摘要が変わる保存は書き換え");
        _insertedLogs.Should().ContainSingle();
    }

    #endregion

    #region 未入力一覧からのスキップ

    [Fact]
    public async Task 未入力一覧から開いた行のスキップで保存済みのバス停名を戻すときは認証を求め操作ログを記録すること()
    {
        await OpenFromIncompleteListAsync(RecordedLedger("天神～博多駅", null));

        await _viewModel.SkipAsync();

        _staffAuthServiceMock.Verify(s => s.RequestAuthenticationAsync(SkipOperation), Times.Once);
        _viewModel.IsSaved.Should().BeTrue();
        _insertedLogs.Should().ContainSingle();
        var (log, logTransaction) = _insertedLogs[0];
        log.OperatorIdm.Should().Be(AuthIdm);
        log.BeforeData.Should().Contain("\"BusStops\":\"天神～博多駅\"", "変更前は保存済みのバス停名");
        log.AfterData.Should().NotContain("天神～博多駅", "スキップは保存済みのバス停名も★へ戻す（#1156）");
        logTransaction.Should().BeSameAs(_detailTransactions.Single());
    }

    [Fact]
    public async Task 未入力一覧から開いた行のスキップで認証をキャンセルしたら書き換えないこと()
    {
        _staffAuthServiceMock.Setup(s => s.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync((StaffAuthResult?)null);
        await OpenFromIncompleteListAsync(RecordedLedger("天神～博多駅", null));

        await _viewModel.SkipAsync();

        VerifyNothingWritten();
        _viewModel.IsSaved.Should().BeFalse();
        _viewModel.StatusMessage.Should().Be(BusStopInputViewModel.SkipAuthenticationCancelledMessage);
        _viewModel.BusUsages[0].BusStops.Should().Be("天神～博多駅", "スキップしていないので入力欄を★へ置き換えない");
    }

    [Fact]
    public async Task 未入力一覧から開いた行がすべて未入力ならスキップは認証を求めず何も書かずに閉じること()
    {
        await OpenFromIncompleteListAsync(RecordedLedger("★", null));

        await _viewModel.SkipAsync();

        _staffAuthServiceMock.Verify(s => s.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never,
            "台帳が変わらない操作（Esc で閉じるだけ）に職員証を求めない");
        _ledgerRepoMock.Verify(r => r.UpdateDetailBusStopsAsync(
            It.IsAny<int>(), It.IsAny<IEnumerable<(int SequenceNumber, string BusStops)>>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _insertedLogs.Should().BeEmpty();
        _viewModel.IsSaved.Should().BeTrue("後で入力する。一覧へ戻る");
    }

    [Fact]
    public async Task 未入力一覧から開いた行の摘要が手で補足されていてもバス停名がすべて未入力ならスキップは認証を求めず摘要を書き換えないこと()
    {
        var ledger = RecordedLedger("★", null);
        ledger.Summary = "バス（★）※要確認"; // 明細からの再生成とは違う、手で補足した摘要
        await OpenFromIncompleteListAsync(ledger);

        await _viewModel.SkipAsync();

        _staffAuthServiceMock.Verify(s => s.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never,
            "スキップは「後で入力」で、摘要の再生成ではない（Esc で閉じるだけの操作に職員証を求めない）");
        VerifyNothingWritten();
        _viewModel.IsSaved.Should().BeTrue();
    }

    [Fact]
    public async Task 未入力一覧から開いた行の摘要が手で補足されていても保存済みのバス停名があればスキップは認証を求めること()
    {
        var ledger = RecordedLedger("天神～博多駅", null);
        ledger.Summary = "バス（天神～博多駅、★）※要確認";
        await OpenFromIncompleteListAsync(ledger);

        await _viewModel.SkipAsync();

        _staffAuthServiceMock.Verify(s => s.RequestAuthenticationAsync(SkipOperation), Times.Once,
            "保存済みのバス停名を★へ戻すのは書き換え");
        _insertedLogs.Should().ContainSingle();
    }

    [Fact]
    public async Task 未入力一覧から開いた未入力の行に入力してからスキップしても認証を求めず何も書かないこと()
    {
        await OpenFromIncompleteListAsync(RecordedLedger("★"));
        _viewModel.BusUsages[0].BusStops = "天神～博多駅"; // 入力欄だけの値（保存していない）

        await _viewModel.SkipAsync();

        _dialogServiceMock.Verify(d => d.ShowWarningConfirmation(It.IsAny<string>(), BusStopInputViewModel.SkipDiscardConfirmationTitle),
            Times.Once, "入力を破棄する確認は従来どおり出す");
        _staffAuthServiceMock.Verify(s => s.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never,
            "変更の有無は保存済みの値で決める（入力欄の値は台帳に無い）");
        VerifyNothingWritten();
        _viewModel.IsSaved.Should().BeTrue();
    }

    #endregion

    #region 返却フロー（対の表明）

    [Fact]
    public async Task 返却フローの入力の保存は認証を求めず操作ログも記録しないこと()
    {
        var ledger = RecordedLedger((string?)null);
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(LedgerId)).ReturnsAsync(ledger);
        await _viewModel.InitializeWithLedgersAsync(new[] { ledger });
        _viewModel.BusUsages[0].BusStops = "天神～博多駅";

        await _viewModel.SaveAsync();

        _viewModel.IsSaved.Should().BeTrue();
        _staffAuthServiceMock.Verify(s => s.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never,
            "返却の職員証タッチで操作者が確定している流れの中の入力");
        _insertedLogs.Should().BeEmpty();
        _detailTransactions.Should().ContainSingle("保存はする");
    }

    [Fact]
    public async Task 返却フローの入力のスキップは認証を求めず操作ログも記録しないこと()
    {
        var ledger = RecordedLedger("天神～博多駅");
        await _viewModel.InitializeWithDetailsAsync(ledger, ledger.Details);

        await _viewModel.SkipAsync();

        _viewModel.IsSaved.Should().BeTrue();
        _staffAuthServiceMock.Verify(s => s.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
        _insertedLogs.Should().BeEmpty();
        _detailTransactions.Should().ContainSingle("スキップは★で保存する");
    }

    [Fact]
    public async Task 未入力一覧から開いた後に返却フローとして初期化し直したら認証を求めないこと()
    {
        await OpenFromIncompleteListAsync(RecordedLedger((string?)null));
        var returned = RecordedLedger((string?)null);
        await _viewModel.InitializeWithDetailsAsync(returned, returned.Details);
        _viewModel.BusUsages[0].BusStops = "天神～博多駅";

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(s => s.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never,
            "経路は最後の初期化で決まる（前の初期化の経路を引きずらない）");
        _insertedLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task 未入力一覧から開いた後に同期版の返却フローの初期化をし直したら認証を求めないこと()
    {
        await OpenFromIncompleteListAsync(RecordedLedger((string?)null));
        var returned = RecordedLedger((string?)null);
        _viewModel.InitializeWithDetails(returned, returned.Details);
        _viewModel.BusUsages[0].BusStops = "天神～博多駅";

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(s => s.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never,
            "同期版の初期化も返却フローの経路へ戻す");
        _insertedLogs.Should().BeEmpty();
        _detailTransactions.Should().ContainSingle("保存はする");
    }

    #endregion
}
