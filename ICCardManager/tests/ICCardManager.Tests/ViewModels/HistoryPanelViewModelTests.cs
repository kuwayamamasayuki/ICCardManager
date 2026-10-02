using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// 履歴パネル（<see cref="HistoryPanelViewModel"/>）の単体テスト（Issue #2159 で <c>MainViewModelTests</c> から移設）。
/// </summary>
/// <remarks>
/// <para>
/// 履歴パネルは <see cref="MainViewModel"/> から抽出した子の ViewModel で、警告エリア・残高ダッシュボード・
/// 貸出中一覧・処理中オーバーレイはメイン画面が持つ。ここではメイン画面を組み立てず、
/// 記録用のホスト（<see cref="RecordingHistoryPanelHost"/>）で「履歴パネルが親へ何を頼んだか」を表明する。
/// カードリーダー・タイマー・共有モード監視は履歴パネルの依存に無い。
/// </para>
/// <para>
/// 親との実配線（不整合警告が親の警告エリアに届く・貸出中一覧が再読込される）と、親のフロー
/// （カードタッチ・返却後処理・定期更新・警告クリック）を通る表明は <c>MainViewModelTests</c> に残している。
/// </para>
/// </remarks>
public class HistoryPanelViewModelTests : IDisposable
{
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<IToastNotificationService> _toastMock;
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock;
    private readonly Mock<INavigationService> _navigationServiceMock;
    private readonly Mock<OperationLogger> _operationLoggerMock;

    /// <summary>
    /// 監査ログが実際に書かれたかを表明するためのモック（Issue #1944）。
    /// </summary>
    /// <remarks>
    /// <c>OperationLogger</c> のログ記録メソッドは <c>virtual</c> ではないため
    /// <c>Mock&lt;OperationLogger&gt;</c> の実体は本物の実装を実行する。したがって
    /// 「ログが残ったか」は本物が書き込む <see cref="IOperationLogRepository"/> の側で観測する
    /// （<c>.claude/rules/development-conventions.md</c> Issue #1760）。
    /// </remarks>
    private readonly Mock<IOperationLogRepository> _operationLogRepositoryMock;
    private readonly LedgerMergeService _ledgerMergeService;
    private readonly LedgerConsistencyChecker _ledgerConsistencyChecker;
    private readonly DbContext _dbContext;
    private readonly RecordingHistoryPanelHost _host;
    private readonly HistoryPanelViewModel _history;

    public void Dispose()
    {
        _dbContext?.Dispose();
        GC.SuppressFinalize(this);
    }

    public HistoryPanelViewModelTests()
    {
        _cardRepositoryMock = new Mock<ICardRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _toastMock = new Mock<IToastNotificationService>();
        _staffAuthServiceMock = new Mock<IStaffAuthService>();
        _navigationServiceMock = new Mock<INavigationService>();

        _operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        _operationLoggerMock = new Mock<OperationLogger>(
            _operationLogRepositoryMock.Object, Mock.Of<ICurrentOperatorContext>());

        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        // Issue #1059: GetDetailsByLedgerIdsAsyncのデフォルト戻り値を設定
        _ledgerRepositoryMock.Setup(r => r.GetDetailsByLedgerIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, List<LedgerDetail>>());

        // 履歴一覧の読み込みが通る既定値（未設定だと GetPagedAsync は既定のタプル (null, 0)、
        // GetMergeHistoriesAsync は null を返す）。個々のテストの Setup が後勝ちで上書きする
        _ledgerRepositoryMock.Setup(r => r.GetPagedAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<Ledger>(), 0));
        _ledgerRepositoryMock.Setup(r => r.GetMergeHistoriesAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>());

        _ledgerConsistencyChecker = new LedgerConsistencyChecker(_ledgerRepositoryMock.Object);

        _ledgerMergeService = new LedgerMergeService(
            _ledgerRepositoryMock.Object,
            new SummaryGenerator(),
            _operationLoggerMock.Object,
            _dbContext,
            NullLogger<LedgerMergeService>.Instance);

        _host = new RecordingHistoryPanelHost();
        _history = CreateHistoryPanel();
    }

    /// <summary>
    /// 履歴パネルを生成し、記録用ホスト <see cref="_host"/> へ接続する。
    /// </summary>
    private HistoryPanelViewModel CreateHistoryPanel(ILogger<HistoryPanelViewModel>? logger = null)
    {
        var history = new HistoryPanelViewModel(
            _ledgerRepositoryMock.Object,
            _cardRepositoryMock.Object,
            _dbContext,
            _staffAuthServiceMock.Object,
            _ledgerMergeService,
            _navigationServiceMock.Object,
            _operationLoggerMock.Object,
            _ledgerConsistencyChecker,
            _toastMock.Object,
            logger);
        history.AttachHost(_host);
        return history;
    }

    #region 履歴行編集の自動計算の起点（Issue #1740）

    /// <summary>
    /// Issue #1740: 2行目以降を編集する場合、直前行の残高が自動計算の起点として供給されること。
    /// </summary>
    [Fact]
    public void FindPreviousBalanceForEdit_2行目以降は直上行の残高を返すこと()
    {
        // Arrange
        _history.HistoryLedgers.Add(new LedgerDto { Id = 1, Balance = 2000 });
        _history.HistoryLedgers.Add(new LedgerDto { Id = 2, Balance = 5000 });
        _history.HistoryLedgers.Add(new LedgerDto { Id = 3, Balance = 4790 });

        // Act & Assert
        _history.FindPreviousBalanceForEdit(_history.HistoryLedgers[1]).Should().Be(2000);
        _history.FindPreviousBalanceForEdit(_history.HistoryLedgers[2]).Should().Be(5000);
    }

    /// <summary>
    /// Issue #1740: 先頭行には直前行が無いため null を返し、自動計算を無効化させること。
    /// ここで 0 を返すと「0 + 受入 - 払出」で残高が破壊される（本Issueの不具合そのもの）。
    /// </summary>
    [Fact]
    public void FindPreviousBalanceForEdit_先頭行はnullを返すこと()
    {
        // Arrange
        _history.HistoryLedgers.Add(new LedgerDto { Id = 1, Balance = 2000 });
        _history.HistoryLedgers.Add(new LedgerDto { Id = 2, Balance = 5000 });

        // Act & Assert
        _history.FindPreviousBalanceForEdit(_history.HistoryLedgers[0]).Should().BeNull();
    }

    /// <summary>
    /// Issue #1740 / Issue #1155: 1ページ目の先頭に挿入される繰越行（Id=0）が直前行になる場合、
    /// その残高が起点として供給されること。表示期間の最初の実データ行も自動計算できる。
    /// </summary>
    [Fact]
    public void FindPreviousBalanceForEdit_繰越行が直前にある場合はその残高を返すこと()
    {
        // Arrange: BuildCarryoverRowAsync が生成する繰越行は Id = 0
        _history.HistoryLedgers.Add(new LedgerDto { Id = 0, Balance = 7500, Summary = "前年度より繰越" });
        _history.HistoryLedgers.Add(new LedgerDto { Id = 42, Balance = 7290 });

        // Act & Assert
        _history.FindPreviousBalanceForEdit(_history.HistoryLedgers[1]).Should().Be(7500);
    }

    /// <summary>
    /// Issue #1740: 一覧に存在しない行が渡された場合も 0 に丸めず null を返すこと。
    /// </summary>
    [Fact]
    public void FindPreviousBalanceForEdit_一覧に無い行はnullを返すこと()
    {
        // Arrange
        _history.HistoryLedgers.Add(new LedgerDto { Id = 1, Balance = 2000 });

        // Act & Assert
        _history.FindPreviousBalanceForEdit(new LedgerDto { Id = 999, Balance = 100 }).Should().BeNull();
        _history.FindPreviousBalanceForEdit(null).Should().BeNull();
    }

    /// <summary>
    /// Issue #1740: 繰越額の取得と繰越行の生成を分離しても、生成結果が従来と一致すること。
    /// 分離したのは、残高チェーンの並べ替えシードと繰越行が同じ値を必要とするため。
    /// </summary>
    [Fact]
    public void BuildCarryoverRow_繰越額が無い場合はnullを返すこと()
    {
        // Act & Assert
        _history.BuildCarryoverRow("0102030405060708", 2026, 5, null).Should().BeNull();
    }

    /// <summary>
    /// Issue #1740: 繰越額があれば、その残高を持つ表示専用行（Id=0）を生成すること。
    /// </summary>
    [Fact]
    public void BuildCarryoverRow_繰越額から表示専用の繰越行を生成すること()
    {
        // Act
        var row = _history.BuildCarryoverRow("0102030405060708", 2026, 5, 7500);

        // Assert
        row.Should().NotBeNull();
        row!.Id.Should().Be(0, "DBに実体を持たない合成行");
        row.Balance.Should().Be(7500);
        row.IsCarryoverRow.Should().BeTrue();
    }

    #endregion

    #region 残高不整合ハイライト（Issue #1052）

    [Fact]
    public void ApplyBalanceInconsistencyMarkers_不整合IDに一致するDtoにフラグとメッセージが設定されること()
    {
        // Arrange
        _history.HistoryLedgers.Add(new LedgerDto { Id = 1, Balance = 1000 });
        _history.HistoryLedgers.Add(new LedgerDto { Id = 2, Balance = 800 });
        _history.HistoryLedgers.Add(new LedgerDto { Id = 3, Balance = 600 });

        // internalフィールドへ直接アクセスできないため、リフレクションで設定
        var field = typeof(HistoryPanelViewModel).GetField("_balanceInconsistencies",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.SetValue(_history, new Dictionary<int, (int ExpectedBalance, int ActualBalance, bool IsInitialBalanceCorrection)>
        {
            { 2, (900, 800, false) }
        });

        // Act
        _history.ApplyBalanceInconsistencyMarkers();

        // Assert
        _history.HistoryLedgers[0].HasBalanceInconsistency.Should().BeFalse();
        _history.HistoryLedgers[1].HasBalanceInconsistency.Should().BeTrue();
        _history.HistoryLedgers[1].BalanceInconsistencyMessage.Should().Contain("期待値 900円");
        _history.HistoryLedgers[1].BalanceInconsistencyMessage.Should().Contain("実際 800円");
        _history.HistoryLedgers[2].HasBalanceInconsistency.Should().BeFalse();
    }

    #region 導入時残高の誤りの検知と案内（Issue #2007）

    /// <summary>
    /// Issue #2007 の形状: 導入行（新規購入）の残高だけが誤り、以後はカード由来で正しい。
    /// 全期間チェックはこの形を <c>InitialBalanceCorrection</c> として返す。
    /// </summary>
    internal static List<Ledger> CreateInitialBalanceErrorLedgers(string cardIdm) => new()
    {
        new Ledger { Id = 1, CardIdm = cardIdm, Date = new DateTime(2025, 4, 1), Summary = "新規購入", Income = 5000, Expense = 0, Balance = 5000 },
        new Ledger { Id = 2, CardIdm = cardIdm, Date = new DateTime(2025, 4, 2), Summary = "鉄道（天神～博多）", Income = 0, Expense = 210, Balance = 2790 },
        new Ledger { Id = 3, CardIdm = cardIdm, Date = new DateTime(2025, 4, 3), Summary = "鉄道（博多～天神）", Income = 0, Expense = 260, Balance = 2530 }
    };

    /// <summary>
    /// 警告文言は「残高の不整合が N 件」ではなく、導入時の残額が原因であることを名指しする。
    /// 従来の文言では、ハイライトされる 2 行目（正しい行）を直す誘導になっていた。
    /// </summary>
    [Fact]
    public async Task CheckAllCardsConsistencyAsync_導入時残高の誤りなら警告文言で原因を名指しすること()
    {
        const string cardIdm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { new IcCard { CardIdm = cardIdm, CardType = "はやかけん", CardNumber = "5042" } });
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(CreateInitialBalanceErrorLedgers(cardIdm));

        await _history.CheckAllCardsConsistencyAsync();

        var warning = _host.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.BalanceInconsistency).Which;
        warning.DisplayText.Should().Contain("導入時の残額")
            .And.Contain("はやかけん 5042")
            .And.NotContain("不整合が", "原因を名指しできるときは件数の汎用文言を使わない");
    }

    /// <summary>
    /// 対の表明: 通常の不整合（導入行以外で切れている）では従来どおり件数の文言のまま。
    /// これが無いと、全不整合を「導入時の残額」と決めつける実装でも緑になる。
    /// </summary>
    [Fact]
    public async Task CheckAllCardsConsistencyAsync_通常の不整合では従来の件数文言のままであること()
    {
        const string cardIdm = "0102030405060708";
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { new IcCard { CardIdm = cardIdm, CardType = "はやかけん", CardNumber = "5042" } });
        var ledgers = CreateInitialBalanceErrorLedgers(cardIdm);
        ledgers[0].Balance = 3000; ledgers[0].Income = 3000;   // 導入行は正しい
        ledgers[2].Balance = 2000;                              // 3 行目で切れる（本来 2,530）
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);

        await _history.CheckAllCardsConsistencyAsync();

        _host.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.BalanceInconsistency)
            .Which.DisplayText.Should().Contain("残高の不整合が1件あります").And.NotContain("導入時");
    }

    /// <summary>
    /// ハイライトは切れた側（2 行目）ではなく導入行に付け、逆算した金額と対処を添える。
    /// 2 行目はカード由来の正しい行なので、ここを強調すると正しい行を書き換える誘導になる。
    /// </summary>
    [Fact]
    public void BuildInconsistencyMarkers_導入時残高の誤りなら導入行だけをハイライト対象にすること()
    {
        var result = _ledgerConsistencyChecker.CheckConsistency(
            CreateInitialBalanceErrorLedgers("0102030405060708"), "0102030405060708", DateTime.Today);
        result.InitialBalanceCorrection.Should().NotBeNull("前提: この形状は導入時残高の誤りとして検知される");

        var markers = HistoryPanelViewModel.BuildInconsistencyMarkers(result);

        markers.Should().ContainKey(1).WhoseValue.Should().Be((3000, 5000, true), "期待値＝逆算した残高 / 実際＝記録されている残高 / 訂正案由来");
        markers.Should().NotContainKey(2, "チェーンが切れた側の行は正しい行なので強調しない");
    }

    [Fact]
    public void BuildInconsistencyMarkers_通常の不整合では切れた行をそのままハイライト対象にすること()
    {
        var ledgers = CreateInitialBalanceErrorLedgers("0102030405060708");
        ledgers[0].Balance = 3000; ledgers[0].Income = 3000;
        ledgers[2].Balance = 2000;
        var result = _ledgerConsistencyChecker.CheckConsistency(ledgers, "0102030405060708", DateTime.Today);

        var markers = HistoryPanelViewModel.BuildInconsistencyMarkers(result);

        markers.Should().ContainKey(3).WhoseValue.Should().Be((2530, 2000, false));
        markers.Should().NotContainKey(1);
    }

    [Fact]
    public void ApplyBalanceInconsistencyMarkers_導入行のメッセージは逆算した残高と対処を含むこと()
    {
        _history.HistoryLedgers.Add(new LedgerDto { Id = 1, Summary = "新規購入", Income = 5000, Balance = 5000 });
        _history.HistoryLedgers.Add(new LedgerDto { Id = 2, Summary = "鉄道（天神～博多）", Expense = 210, Balance = 2790 });
        var field = typeof(HistoryPanelViewModel).GetField("_balanceInconsistencies",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.SetValue(_history, new Dictionary<int, (int ExpectedBalance, int ActualBalance, bool IsInitialBalanceCorrection)> { { 1, (3000, 5000, true) } });

        _history.ApplyBalanceInconsistencyMarkers();

        var message = _history.HistoryLedgers[0].BalanceInconsistencyMessage;
        message.Should().Contain("導入時の残額")
            .And.Contain("5,000円")
            .And.Contain("3,000円")
            .And.Contain("受入と残額", "新規購入は受入欄も一緒に直す必要がある")
            .And.MatchRegex("してください。?$", "行動指示で終わる（error-messages.md）");
        _history.HistoryLedgers[1].HasBalanceInconsistency.Should().BeFalse();
    }

    [Fact]
    public void ApplyBalanceInconsistencyMarkers_受入欄が空欄の導入行では残額だけを直すよう案内すること()
    {
        _history.HistoryLedgers.Add(new LedgerDto { Id = 1, Summary = SummaryGenerator.GetMidYearCarryoverSummary(5), Income = 0, Balance = 8000 });
        var field = typeof(HistoryPanelViewModel).GetField("_balanceInconsistencies",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.SetValue(_history, new Dictionary<int, (int ExpectedBalance, int ActualBalance, bool IsInitialBalanceCorrection)> { { 1, (7500, 8000, true) } });

        _history.ApplyBalanceInconsistencyMarkers();

        _history.HistoryLedgers[0].BalanceInconsistencyMessage.Should().Contain("残額を 7,500円")
            .And.NotContain("受入と残額");
    }

    /// <summary>
    /// 対の表明: 文言の分岐は摘要ではなくマーカーのフラグで決まる。導入行の摘要を持つ行に
    /// 通常経路（前行からの前方計算）でマーカーが付いたとき、その期待値を「逆算した残高」と
    /// 偽って案内してはならない（コードレビュー指摘）。
    /// </summary>
    [Fact]
    public void ApplyBalanceInconsistencyMarkers_訂正案由来でなければ導入行の摘要でも通常の文言にすること()
    {
        _history.HistoryLedgers.Add(new LedgerDto { Id = 5, Summary = "新規購入", Income = 3000, Balance = 3000 });
        var field = typeof(HistoryPanelViewModel).GetField("_balanceInconsistencies",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.SetValue(_history, new Dictionary<int, (int ExpectedBalance, int ActualBalance, bool IsInitialBalanceCorrection)> { { 5, (2800, 3000, false) } });

        _history.ApplyBalanceInconsistencyMarkers();

        _history.HistoryLedgers[0].BalanceInconsistencyMessage.Should().Contain("期待値 2,800円")
            .And.NotContain("導入時の残額");
    }

    /// <summary>
    /// 警告クリックで複数月を表示したとき、期間ラベルが開始月だけにならないこと（コードレビュー指摘）。
    /// 通常の暦月表示は従来どおり開始月だけ。
    /// </summary>
    [Fact]
    public void FormatHistoryPeriod_開始月と終了月が異なれば範囲で表示し同じ月なら開始月だけを表示すること()
    {
        HistoryPanelViewModel.FormatHistoryPeriod(new DateTime(2025, 4, 1), new DateTime(2026, 9, 3))
            .Should().Be("2025年4月～2026年9月");
        HistoryPanelViewModel.FormatHistoryPeriod(new DateTime(2026, 9, 1), new DateTime(2026, 9, 3))
            .Should().Be("2026年9月");
    }

    #region Issue #2030: 表示期間を矢印で前後の月へ移動する

    [Fact]
    public void GetAdjacentHistoryMonth_開始月の前後の月の1日を返し年をまたげること()
    {
        var today = new DateTime(2026, 9, 15);

        HistoryPanelViewModel.GetAdjacentHistoryMonth(new DateTime(2026, 3, 1), -1, today, 2020)
            .Should().Be(new DateTime(2026, 2, 1));
        HistoryPanelViewModel.GetAdjacentHistoryMonth(new DateTime(2026, 3, 1), 1, today, 2020)
            .Should().Be(new DateTime(2026, 4, 1));
        HistoryPanelViewModel.GetAdjacentHistoryMonth(new DateTime(2026, 1, 1), -1, today, 2020)
            .Should().Be(new DateTime(2025, 12, 1), "1 月の前は前年の 12 月");
        HistoryPanelViewModel.GetAdjacentHistoryMonth(new DateTime(2025, 12, 1), 1, today, 2020)
            .Should().Be(new DateTime(2026, 1, 1), "12 月の次は翌年の 1 月");
    }

    [Fact]
    public void GetAdjacentHistoryMonth_月中の日付や月末でも月単位で移動すること()
    {
        var today = new DateTime(2026, 9, 15);

        // 3/31 の前月を AddMonths で直接求めると 2/28 になり、月の 1 日へ丸めないと期間の開始がずれる
        HistoryPanelViewModel.GetAdjacentHistoryMonth(new DateTime(2026, 3, 31), -1, today, 2020)
            .Should().Be(new DateTime(2026, 2, 1));
        HistoryPanelViewModel.GetAdjacentHistoryMonth(new DateTime(2026, 1, 31), 1, today, 2020)
            .Should().Be(new DateTime(2026, 2, 1));
    }

    [Fact]
    public void GetAdjacentHistoryMonth_次の月へは今月まで進め未来の月へは進めないこと()
    {
        var today = new DateTime(2026, 9, 15);

        HistoryPanelViewModel.GetAdjacentHistoryMonth(new DateTime(2026, 8, 1), 1, today, 2020)
            .Should().Be(new DateTime(2026, 9, 1), "今月へは進める");
        HistoryPanelViewModel.GetAdjacentHistoryMonth(new DateTime(2026, 9, 1), 1, today, 2020)
            .Should().BeNull("利用日（ledger.date）が未来の月の行は無い");
        HistoryPanelViewModel.GetAdjacentHistoryMonth(new DateTime(2026, 9, 1), -1, today, 2020)
            .Should().Be(new DateTime(2026, 8, 1), "今月の表示でも前の月へは戻れる");
    }

    [Fact]
    public void GetAdjacentHistoryMonth_前の月へは最古の年の1月まで戻れそれより前へは戻れないこと()
    {
        var today = new DateTime(2026, 9, 15);

        HistoryPanelViewModel.GetAdjacentHistoryMonth(new DateTime(2020, 2, 1), -1, today, 2020)
            .Should().Be(new DateTime(2020, 1, 1), "最古の年の 1 月へは戻れる");
        HistoryPanelViewModel.GetAdjacentHistoryMonth(new DateTime(2020, 1, 1), -1, today, 2020)
            .Should().BeNull("月選択ポップアップで選べない年へは矢印でも移動しない");
        HistoryPanelViewModel.GetAdjacentHistoryMonth(new DateTime(2020, 1, 1), 1, today, 2020)
            .Should().Be(new DateTime(2020, 2, 1), "最古の月の表示でも次の月へは進める");
    }

    [Fact]
    public void GetAdjacentHistoryMonth_下限より前の月を表示中でも次の月へは進めること()
    {
        // 警告クリック（#2007）は導入行の日付から表示するため、ポップアップの最古の年より前を表示し得る。
        // 下限を前向きの移動にまで効かせると、その状態から ◀ も ▶ も押せなくなる
        var today = new DateTime(2026, 9, 15);

        HistoryPanelViewModel.GetAdjacentHistoryMonth(new DateTime(2018, 4, 1), 1, today, 2020)
            .Should().Be(new DateTime(2018, 5, 1));
        HistoryPanelViewModel.GetAdjacentHistoryMonth(new DateTime(2018, 4, 1), -1, today, 2020)
            .Should().BeNull();
    }

    [Fact]
    public async Task HistoryGoToPreviousMonth_前の月の暦月へ期間を移し1ページ目から読み込み直すこと()
    {
        const string cardIdm = "0102030405060708";
        _history.HistoryCard = new CardDto { CardIdm = cardIdm, CardNumber = "5042" };
        await _history.HistorySetThisMonth();
        _history.HistoryCurrentPage = 3;

        await _history.HistoryGoToPreviousMonthCommand.ExecuteAsync(null);

        var lastMonth = DateTime.Today.AddMonths(-1);
        var expectedFrom = new DateTime(lastMonth.Year, lastMonth.Month, 1);
        var expectedTo = new DateTime(lastMonth.Year, lastMonth.Month, DateTime.DaysInMonth(lastMonth.Year, lastMonth.Month));
        _history.HistoryFromDate.Should().Be(expectedFrom);
        _history.HistoryToDate.Should().Be(expectedTo);
        _history.HistoryPeriodDisplay.Should().Be(HistoryPanelViewModel.FormatHistoryPeriod(expectedFrom, expectedTo));
        _history.HistoryCurrentPage.Should().Be(1, "月を変えたら 1 ページ目から表示する");
        _history.HistorySelectedYear.Should().Be(lastMonth.Year, "月選択ポップアップの初期値も移動先に揃える");
        _history.HistorySelectedMonth.Should().Be(lastMonth.Month);
        _ledgerRepositoryMock.Verify(r => r.GetPagedAsync(cardIdm, expectedFrom, expectedTo, 1, It.IsAny<int>()),
            Times.Once, "移動先の月で履歴を読み込み直す");
    }

    [Fact]
    public async Task HistoryGoToNextMonth_次の月へ進み今月に着いたら次の月へは進めなくなること()
    {
        const string cardIdm = "0102030405060708";
        _history.HistoryCard = new CardDto { CardIdm = cardIdm, CardNumber = "5042" };
        await _history.HistorySetLastMonth();
        _history.HistoryGoToNextMonthCommand.CanExecute(null).Should().BeTrue("先月の表示からは今月へ進める");

        await _history.HistoryGoToNextMonthCommand.ExecuteAsync(null);

        var today = DateTime.Today;
        _history.HistoryFromDate.Should().Be(new DateTime(today.Year, today.Month, 1));
        _history.HistoryGoToNextMonthCommand.CanExecute(null).Should().BeFalse("今月より先の月は表示しない");
        _history.HistoryGoToPreviousMonthCommand.CanExecute(null).Should().BeTrue("今月の表示でも前の月へは戻れる");
    }

    [Fact]
    public async Task HistoryFromDate_変わると矢印の実行可否の再評価をボタンへ通知すること()
    {
        // CanExecute は問い合わせのたびに評価されるが、ボタンの有効・無効は CanExecuteChanged を
        // 受けたときにしか更新されない。通知が無いと今月へ着いても ▶ が押せる見た目のまま残る
        var nextRaised = 0;
        var previousRaised = 0;
        _history.HistoryGoToNextMonthCommand.CanExecuteChanged += (_, _) => nextRaised++;
        _history.HistoryGoToPreviousMonthCommand.CanExecuteChanged += (_, _) => previousRaised++;

        await _history.HistorySetLastMonth();

        nextRaised.Should().BeGreaterThan(0);
        previousRaised.Should().BeGreaterThan(0);
    }

    [Fact]
    public void HistoryGoToPreviousMonth_月選択ポップアップの最古の年の1月では実行できないこと()
    {
        // 下限は ViewModel が持つ年リスト（HistoryAvailableYears）の最古の年から決まることを、実物の年リストで確かめる
        var oldestYear = _history.HistoryAvailableYears.Min();
        _history.HistoryFromDate = new DateTime(oldestYear, 1, 1);

        _history.HistoryGoToPreviousMonthCommand.CanExecute(null).Should().BeFalse();
        _history.HistoryGoToNextMonthCommand.CanExecute(null).Should().BeTrue();

        _history.HistoryFromDate = new DateTime(oldestYear, 2, 1);
        _history.HistoryGoToPreviousMonthCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task HistoryGoToNextMonth_範囲表示中は開始月を基準に移動し暦月表示に戻ること()
    {
        // 警告クリック（#2007）は「導入行の月～今月」の範囲を表示する。矢印はラベル先頭の月を基準にする
        const string cardIdm = "0102030405060708";
        _history.HistoryCard = new CardDto { CardIdm = cardIdm, CardNumber = "5042" };
        var today = DateTime.Today;
        var rangeStart = new DateTime(today.Year, today.Month, 1).AddMonths(-14);
        _history.HistoryFromDate = rangeStart;
        _history.HistoryToDate = today;

        await _history.HistoryGoToNextMonthCommand.ExecuteAsync(null);

        var expectedFrom = rangeStart.AddMonths(1);
        var expectedTo = expectedFrom.AddMonths(1).AddDays(-1);
        _history.HistoryFromDate.Should().Be(expectedFrom);
        _history.HistoryToDate.Should().Be(expectedTo);
        _history.HistoryPeriodDisplay.Should().Be(HistoryPanelViewModel.FormatHistoryPeriod(expectedFrom, expectedFrom),
            "範囲表記ではなく 1 か月の表記に戻る");
        _ledgerRepositoryMock.Verify(r => r.GetPagedAsync(cardIdm, expectedFrom, expectedTo, 1, It.IsAny<int>()),
            Times.Once, "移動先の月で履歴を読み込み直す");
    }

    [Fact]
    public async Task HistoryGoToNextMonth_今月表示で直接実行しても期間を変えず読み込まないこと()
    {
        // AsyncRelayCommand.ExecuteAsync は CanExecute を確かめない。ボタンの無効化だけに頼らず、
        // 実行時にも境界を確かめていることを固定する（評価から実行までに日付が変わり得る）
        const string cardIdm = "0102030405060708";
        _history.HistoryCard = new CardDto { CardIdm = cardIdm, CardNumber = "5042" };
        await _history.HistorySetThisMonth();
        var fromBefore = _history.HistoryFromDate;
        var toBefore = _history.HistoryToDate;
        _ledgerRepositoryMock.Invocations.Clear();

        await _history.HistoryGoToNextMonthCommand.ExecuteAsync(null);

        _history.HistoryFromDate.Should().Be(fromBefore);
        _history.HistoryToDate.Should().Be(toBefore);
        _ledgerRepositoryMock.Verify(r => r.GetPagedAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Never, "未来の月は読み込まない");
    }

    [Fact]
    public async Task HistoryGoToNextMonth_年リストに無い年へ進んだら年リストへ降順を保って補うこと()
    {
        // 警告クリック（#2007）で年リストの最古の年より前を表示してから ▶ で進むと、年リストに無い年へ着く。
        // 補わないと月選択ポップアップの年が空欄になる
        const string cardIdm = "0102030405060708";
        _history.HistoryCard = new CardDto { CardIdm = cardIdm, CardNumber = "5042" };
        var oldestYear = _history.HistoryAvailableYears.Min();
        var outsideYear = oldestYear - 2;
        _history.HistoryFromDate = new DateTime(outsideYear, 4, 1);
        _history.HistoryToDate = DateTime.Today;

        await _history.HistoryGoToNextMonthCommand.ExecuteAsync(null);

        _history.HistorySelectedYear.Should().Be(outsideYear);
        _history.HistoryAvailableYears.Should().Contain(outsideYear);
        _history.HistoryAvailableYears.Should().BeInDescendingOrder("ポップアップの年は新しい順に並ぶ");
        _history.HistoryAvailableYears.Should().OnlyHaveUniqueItems();
    }

    #endregion


    /// <summary>
    /// 行編集を開く前に、その行が導入行で導入時残高の誤りが検知されているときだけ提案を求める。
    /// 通常の行では全期間チェック（6 年分の読み取り）を走らせない。
    /// </summary>
    [Fact]
    public async Task ResolveInitialBalanceCorrectionForEditAsync_導入行なら全期間チェックの提案を返し利用行なら問い合わせないこと()
    {
        const string cardIdm = "0102030405060708";
        _history.HistoryCard = new CardDto { CardIdm = cardIdm, CardNumber = "5042" };
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(CreateInitialBalanceErrorLedgers(cardIdm));

        var forInitial = await _history.ResolveInitialBalanceCorrectionForEditAsync(
            new LedgerDto { Id = 1, CardIdm = cardIdm, Summary = "新規購入" });
        var forUsage = await _history.ResolveInitialBalanceCorrectionForEditAsync(
            new LedgerDto { Id = 2, CardIdm = cardIdm, Summary = "鉄道（天神～博多）" });

        forInitial.Should().NotBeNull();
        forInitial!.SuggestedBalance.Should().Be(3000);
        forUsage.Should().BeNull();
        _ledgerRepositoryMock.Verify(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()), Times.Once,
            "問い合わせるのは導入行を開くときだけ");
    }

    #endregion

    [Fact]
    public void ApplyBalanceInconsistencyMarkers_空のDictionaryでは何も変更されないこと()
    {
        // Arrange
        _history.HistoryLedgers.Add(new LedgerDto { Id = 1, Balance = 1000 });

        // Act（_balanceInconsistenciesは初期状態で空）
        _history.ApplyBalanceInconsistencyMarkers();

        // Assert
        _history.HistoryLedgers[0].HasBalanceInconsistency.Should().BeFalse();
    }

    // 履歴統合の職員認証ゲート（SEQ-AUTH-01）
    [Fact]
    public async Task MergeHistoryLedgers_認証キャンセル時_統合を実行しない()
    {
        // Arrange: 認証以外は「統合が最後まで成功する」状態にしてから、認証だけをキャンセルさせる。
        // Issue #2104: 確認ダイアログを未設定（既定で false）のままにすると、認証ゲートを外しても
        // 確認ダイアログの「いいえ」で止まるため、ゲートの有無を区別できなかった。
        ArrangeMergeableCheckedLedgers();
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync((StaffAuthResult)null);

        // Act
        await _history.MergeHistoryLedgersCommand.ExecuteAsync(null);

        // Assert: 認証を要求し、キャンセルされたため確認ダイアログ・統合処理へ進まない
        _staffAuthServiceMock.Verify(
            s => s.RequestAuthenticationAsync("履歴の統合"), Times.Once);
        _navigationServiceMock.Verify(
            n => n.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
            "認証をキャンセルしたら確認ダイアログを出さないこと");
        _ledgerRepositoryMock.Verify(
            r => r.MergeLedgersAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<int>>(), It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()),
            Times.Never,
            "認証をキャンセルしたら統合を実行しないこと");
    }

    /// <summary>
    /// Issue #2104: 上の認証キャンセルのテストと対になる表明。同じ準備で認証が通れば統合まで進むこと。
    /// これが無いと、統合を無条件に止める実装でも認証キャンセルのテストは緑になる。
    /// </summary>
    [Fact]
    public async Task MergeHistoryLedgers_認証が通れば統合を実行すること()
    {
        ArrangeMergeableCheckedLedgers();

        await _history.MergeHistoryLedgersCommand.ExecuteAsync(null);

        _navigationServiceMock.Verify(
            n => n.ShowConfirmation(It.IsAny<string>(), "履歴の統合"), Times.Once);
        _ledgerRepositoryMock.Verify(
            r => r.MergeLedgersAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<int>>(), It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()),
            Times.Once);
    }

    /// <summary>
    /// Issue #1954: 統合は確定したが取り消し情報を保存できなかったとき、
    /// 「取り消せない」ことを警告として案内し、<b>再実行を促さない</b>こと（Issue #1725）。
    /// </summary>
    [Fact]
    public async Task MergeHistoryLedgers_取り消し情報の保存に失敗_統合完了として案内し再実行を促さないこと()
    {
        ArrangeMergeableCheckedLedgers();
        // コミット後の後処理（Undo 情報の保存）だけを失敗させる
        _ledgerRepositoryMock
            .Setup(r => r.SaveMergeHistoryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("simulated undo-save failure"));

        await _history.MergeHistoryLedgersCommand.ExecuteAsync(null);

        // 統合は確定しているのでエラーとしては案内しない
        _navigationServiceMock.Verify(
            n => n.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        // 「元に戻せる」と誤って案内しない
        _navigationServiceMock.Verify(
            n => n.ShowInformation(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        _navigationServiceMock.Verify(
            n => n.ShowWarning(
                It.Is<string>(m =>
                    m.Contains("統合は完了") &&
                    m.Contains("取り消し情報") &&
                    !m.Contains("再度お試しください")),
                It.IsAny<string>()),
            Times.Once,
            "統合は記録済み・取り消しはできない、と案内する（再実行を促さない）");
    }

    /// <summary>
    /// 対の表明: 後処理まで成功した通常の統合では、従来どおり「元に戻せる」案内を出すこと。
    /// これが無いと、統合を常に警告として案内する実装でも上のテストが緑になる。
    /// </summary>
    [Fact]
    public async Task MergeHistoryLedgers_後処理まで成功_元に戻せる案内を出すこと()
    {
        ArrangeMergeableCheckedLedgers();

        await _history.MergeHistoryLedgersCommand.ExecuteAsync(null);

        _navigationServiceMock.Verify(
            n => n.ShowInformation(It.Is<string>(m => m.Contains("統合を元に戻す")), "統合完了"),
            Times.Once);
        _navigationServiceMock.Verify(
            n => n.ShowWarning(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Issue #1954 / #1727: 取り消し情報の保存が失敗する原因（共有フォルダーの切断・DB ロック）は
    /// 一覧再読込・ダッシュボード更新も同じように失敗させる。**その状況でこそ**「統合は完了・
    /// やり直し不要」の案内が届かなければ意味がないので、再読込を失敗させた状態で表明する。
    /// </summary>
    /// <remarks>
    /// この 2 件が無いと、通知を再読込の後ろへ戻した実装（＝#1954 の初版）でも
    /// 上の 2 件は緑になる（再読込を成功させるモックが欠陥を覆い隠すため）。
    /// </remarks>
    [Fact]
    public async Task MergeHistoryLedgers_取り消し情報の保存に失敗し再読込も失敗_それでも案内が届くこと()
    {
        ArrangeMergeableCheckedLedgers(reloadFails: true);
        _ledgerRepositoryMock
            .Setup(r => r.SaveMergeHistoryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("simulated undo-save failure"));

        Func<Task> act = () => _history.MergeHistoryLedgersCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync("再読込の失敗が非同期コマンドの外へ抜けると誰も観測しない");
        _navigationServiceMock.Verify(
            n => n.ShowWarning(
                It.Is<string>(m => m.Contains("統合は完了") && !m.Contains("再度お試しください")),
                It.IsAny<string>()),
            Times.Once,
            "再読込が同じ原因で失敗しても、統合が確定した事実は必ず伝える（#1727）");
        _navigationServiceMock.Verify(
            n => n.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
            "再読込の失敗で二重のダイアログを出さない");
    }

    /// <summary>
    /// 対の表明: 統合そのものが失敗したときも、再読込の失敗で案内を落とさないこと（#1727）。
    /// </summary>
    [Fact]
    public async Task MergeHistoryLedgers_統合に失敗し再読込も失敗_それでもエラー案内が届くこと()
    {
        ArrangeMergeableCheckedLedgers(reloadFails: true);
        // 統合対象の 1 件が他 PC に削除された状態（MergeAsync は Success=false を返す）
        _ledgerRepositoryMock.Setup(r => r.GetByIdAsync(2)).ReturnsAsync((Ledger)null);

        Func<Task> act = () => _history.MergeHistoryLedgersCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync();
        _navigationServiceMock.Verify(
            n => n.ShowError(It.IsAny<string>(), "統合エラー"), Times.Once,
            "再読込が同じ原因で失敗しても、統合が失敗した事実は必ず伝える（#1727）");
        _navigationServiceMock.Verify(
            n => n.ShowWarning(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Issue #1954 の 4 件で共通の前提: 隣接する 2 件をチェックし、認証と確認を通過させ、
    /// リポジトリの統合本体を成功させる。
    /// </summary>
    /// <param name="reloadFails">
    /// true なら統合後の画面更新（`RefreshDashboardAsync` が使うカード一覧の取得）を失敗させる。
    /// 取り消し情報の保存が失敗する原因と同じ原因で画面更新も失敗する状況の再現（#1727）。
    /// </param>
    private void ArrangeMergeableCheckedLedgers(bool reloadFails = false)
    {
        const string cardIdm = "0102030405060708";
        _history.HistoryLedgers.Add(new LedgerDto { Id = 1, CardIdm = cardIdm, IsChecked = true });
        _history.HistoryLedgers.Add(new LedgerDto { Id = 2, CardIdm = cardIdm, IsChecked = true });

        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync(new StaffAuthResult { Idm = "AABBCCDDEEFF0011", StaffName = "田中太郎" });
        _navigationServiceMock
            .Setup(n => n.ShowConfirmation(It.IsAny<string>(), "履歴の統合"))
            .Returns(true);

        foreach (var id in new[] { 1, 2 })
        {
            var ledgerId = id;
            _ledgerRepositoryMock
                .Setup(r => r.GetByIdAsync(ledgerId))
                .ReturnsAsync(new Ledger
                {
                    Id = ledgerId,
                    CardIdm = cardIdm,
                    Date = new DateTime(2026, 4, 1),
                    Summary = $"鉄道（A駅～B駅{ledgerId}）",
                    Expense = 210,
                    Balance = 2000 - (ledgerId * 210),
                    Details = new List<LedgerDetail>()
                });
        }

        _ledgerRepositoryMock
            .Setup(r => r.MergeLedgersAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<int>>(), It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);

        if (reloadFails)
        {
            // 共有フォルダーの切断・DB ロックは、Undo 情報の保存と画面更新を同じように失敗させる。
            // 一覧の再読込（GetPagedAsync）とダッシュボード更新（メイン画面への要求）の**両方**を落とす
            // ― 成功分岐と失敗分岐で走る後処理が違うため、片方だけだと一方の経路で故障が起きない。
            // 一覧の再読込を実際に走らせるには HistoryCard が要る（null なら早期 return する）。
            _history.HistoryCard = new CardDto { CardIdm = cardIdm, CardNumber = "A-1" };
            _ledgerRepositoryMock
                .Setup(r => r.GetPagedAsync(
                    It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                    It.IsAny<int>(), It.IsAny<int>()))
                .ThrowsAsync(new InvalidOperationException("simulated reload failure"));
            _host.RefreshDashboardFailure = new InvalidOperationException("simulated reload failure");
        }
    }

    [Fact]
    public void ApplyBalanceInconsistencyMarkers_複数の不整合がある場合にすべてマーキングされること()
    {
        // Arrange
        _history.HistoryLedgers.Add(new LedgerDto { Id = 1, Balance = 1000 });
        _history.HistoryLedgers.Add(new LedgerDto { Id = 2, Balance = 800 });
        _history.HistoryLedgers.Add(new LedgerDto { Id = 3, Balance = 500 });

        var field = typeof(HistoryPanelViewModel).GetField("_balanceInconsistencies",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.SetValue(_history, new Dictionary<int, (int ExpectedBalance, int ActualBalance, bool IsInitialBalanceCorrection)>
        {
            { 1, (1100, 1000, false) },
            { 3, (600, 500, false) }
        });

        // Act
        _history.ApplyBalanceInconsistencyMarkers();

        // Assert
        _history.HistoryLedgers[0].HasBalanceInconsistency.Should().BeTrue();
        _history.HistoryLedgers[1].HasBalanceInconsistency.Should().BeFalse();
        _history.HistoryLedgers[2].HasBalanceInconsistency.Should().BeTrue();
    }

    [Fact]
    public void ApplyBalanceInconsistencyMarkers_不整合解消時にフラグがリセットされること()
    {
        // Arrange: 事前にハイライトが適用されている状態
        _history.HistoryLedgers.Add(new LedgerDto
        {
            Id = 1,
            Balance = 1000,
            HasBalanceInconsistency = true,
            BalanceInconsistencyMessage = "残高不整合: 期待値 1,100円 / 実際 1,000円"
        });
        _history.HistoryLedgers.Add(new LedgerDto { Id = 2, Balance = 800 });

        // _balanceInconsistenciesを空にして（不整合が解消された状態を模擬）
        var field = typeof(HistoryPanelViewModel).GetField("_balanceInconsistencies",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.SetValue(_history, new Dictionary<int, (int ExpectedBalance, int ActualBalance, bool IsInitialBalanceCorrection)>());

        // Act
        _history.ApplyBalanceInconsistencyMarkers();

        // Assert: フラグがリセットされていること
        _history.HistoryLedgers[0].HasBalanceInconsistency.Should().BeFalse();
        _history.HistoryLedgers[0].BalanceInconsistencyMessage.Should().BeEmpty();
        _history.HistoryLedgers[1].HasBalanceInconsistency.Should().BeFalse();
    }

    [Fact]
    public void CloseHistory_残高不整合ハイライトデータがクリアされること()
    {
        // Arrange
        var field = typeof(HistoryPanelViewModel).GetField("_balanceInconsistencies",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.SetValue(_history, new Dictionary<int, (int ExpectedBalance, int ActualBalance, bool IsInitialBalanceCorrection)>
        {
            { 1, (1000, 900, false) }
        });

        // Act
        _history.CloseHistory();

        // Assert
        var value = (Dictionary<int, (int, int, bool)>)field.GetValue(_history);
        value.Should().BeEmpty();
    }

    #endregion

    #region 読み込みの再入（Issue #2202）

    // Issue #2202: DB の待ちの間に UI スレッドが空くようになったため、読み込みの途中で別の読み込み
    // （共有モードの 15 秒ごとの再読込・返却後の再読込・月送りの連打）が始まり得る。
    // 以前は SQL が UI スレッドの上で同期的に終わるので、読み込みは割り込まれずに走り切っていた。

    private static List<Ledger> CreateLoadLedgers(string cardIdm, params int[] ids) =>
        ids.Select(id => new Ledger
        {
            Id = id,
            CardIdm = cardIdm,
            Date = DateTime.Today,
            Summary = $"鉄道（博多～天神）#{id}",
            Expense = 210,
            Balance = 10000 - id * 210,
        }).ToList();

    /// <summary>
    /// 先に始めた読み込みが後から終わっても、後に始めた読み込みの結果だけが一覧に残る（古い結果で上書きしない）。
    /// </summary>
    [Fact]
    public async Task LoadHistoryLedgersAsync_先に始めた読み込みが後から終わっても_後の読み込みの結果だけが残ること()
    {
        const string cardIdm = "0102030405060708";
        _history.HistoryCard = new CardDto { CardIdm = cardIdm, CardNumber = "5042" };
        var first = new TaskCompletionSource<(IEnumerable<Ledger>, int)>();
        _ledgerRepositoryMock.SetupSequence(r => r.GetPagedAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(first.Task)
            .ReturnsAsync((CreateLoadLedgers(cardIdm, 11, 12), 2));

        var older = _history.LoadHistoryLedgersAsync();
        await _history.LoadHistoryLedgersAsync();
        first.SetResult((CreateLoadLedgers(cardIdm, 1, 2, 3), 3));
        await older;

        _history.HistoryLedgers.Select(d => d.Id).Should().BeEquivalentTo(new[] { 11, 12 },
            "後に始めた読み込みの結果だけが残る（先に始めた読み込みの古い結果で上書きしない）");
        _history.HistoryTotalCount.Should().Be(2, "件数表示も後の読み込みに由来する");
    }

    /// <summary>
    /// 先に始めた読み込みが待っている間に後の読み込みも始まり、先に始めた方が先に終わっても、行が二重に並ばない。
    /// </summary>
    /// <remarks>
    /// 取得の前に一覧を空にする形だと、2 つの読み込みがそれぞれ空にしてから待ち、両方が行を足して二重に並ぶ。
    /// </remarks>
    [Fact]
    public async Task LoadHistoryLedgersAsync_2つの読み込みが重なっても_行が二重に並ばないこと()
    {
        const string cardIdm = "0102030405060708";
        _history.HistoryCard = new CardDto { CardIdm = cardIdm, CardNumber = "5042" };
        var first = new TaskCompletionSource<(IEnumerable<Ledger>, int)>();
        var second = new TaskCompletionSource<(IEnumerable<Ledger>, int)>();
        _ledgerRepositoryMock.SetupSequence(r => r.GetPagedAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(first.Task)
            .Returns(second.Task);

        var older = _history.LoadHistoryLedgersAsync();
        var newer = _history.LoadHistoryLedgersAsync();
        // 先に始めた方から終える（追い越された読み込みは最新の読み込みが終わるまで戻らないので、
        // 両方の取得を終えてから待つ）
        first.SetResult((CreateLoadLedgers(cardIdm, 1, 2), 2));
        second.SetResult((CreateLoadLedgers(cardIdm, 1, 2), 2));
        await older;
        await newer;

        _history.HistoryLedgers.Should().HaveCount(2, "同じ行が二重に並ばない");
        _history.HistoryLedgers.Select(d => d.Id).Should().BeEquivalentTo(new[] { 1, 2 });
    }

    /// <summary>
    /// 対の表明: 読み込みが重ならなければ、毎回の結果がそのまま一覧に反映される（世代の判定で正当な読み込みを捨てない）。
    /// </summary>
    [Fact]
    public async Task LoadHistoryLedgersAsync_読み込みが重ならなければ_毎回の結果が反映されること()
    {
        const string cardIdm = "0102030405060708";
        _history.HistoryCard = new CardDto { CardIdm = cardIdm, CardNumber = "5042" };
        _ledgerRepositoryMock.SetupSequence(r => r.GetPagedAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((CreateLoadLedgers(cardIdm, 1, 2), 2))
            .ReturnsAsync((CreateLoadLedgers(cardIdm, 21), 1));

        await _history.LoadHistoryLedgersAsync();
        _history.HistoryLedgers.Select(d => d.Id).Should().BeEquivalentTo(new[] { 1, 2 });

        await _history.LoadHistoryLedgersAsync();
        _history.HistoryLedgers.Select(d => d.Id).Should().BeEquivalentTo(new[] { 21 });
        _history.HistoryTotalCount.Should().Be(1);
    }

    /// <summary>
    /// 追い越された読み込みの呼び出し元は、最新の読み込みが終わるまで戻らない。
    /// </summary>
    /// <remarks>
    /// 呼び出し元は「await が戻ったら一覧は読み込み済み」を前提にしている（返却確認の最終ページへの移動・
    /// 「保存して次へ」の隣の行の選択）。追い越された読み込みが何も反映せずにすぐ戻ると、古い一覧を見て進む。
    /// </remarks>
    [Fact]
    public async Task LoadHistoryLedgersAsync_追い越された読み込みは_最新の読み込みが終わるまで戻らないこと()
    {
        const string cardIdm = "0102030405060708";
        _history.HistoryCard = new CardDto { CardIdm = cardIdm, CardNumber = "5042" };
        var first = new TaskCompletionSource<(IEnumerable<Ledger>, int)>();
        var second = new TaskCompletionSource<(IEnumerable<Ledger>, int)>();
        _ledgerRepositoryMock.SetupSequence(r => r.GetPagedAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(first.Task)
            .Returns(second.Task);

        var older = _history.LoadHistoryLedgersAsync();
        var newer = _history.LoadHistoryLedgersAsync();
        first.SetResult((CreateLoadLedgers(cardIdm, 1), 1));

        older.IsCompleted.Should().BeFalse("追い越された読み込みは、最新の読み込みが終わるまで呼び出し元へ戻らない");

        second.SetResult((CreateLoadLedgers(cardIdm, 31, 32), 2));
        await older;
        await newer;
        _history.HistoryLedgers.Select(d => d.Id).Should().BeEquivalentTo(new[] { 31, 32 },
            "呼び出し元へ戻った時点で、一覧は最新の読み込みの結果になっている");
    }

    /// <summary>
    /// 最初の取得より後の待ち（最新の残額の取得）で追い越されても、古い残額で上書きしない。
    /// </summary>
    [Fact]
    public async Task LoadHistoryLedgersAsync_残額の取得で追い越されても_古い残額で上書きしないこと()
    {
        const string cardIdm = "0102030405060708";
        _history.HistoryCard = new CardDto { CardIdm = cardIdm, CardNumber = "5042" };
        var olderBalance = new TaskCompletionSource<Ledger>();
        // 最新の残額は「明日より前」で取る（直前残高のシードは当月 1 日より前で取るので、日付で見分ける）
        _ledgerRepositoryMock.SetupSequence(r => r.GetLatestBeforeDateAsync(
                cardIdm, It.Is<DateTime>(d => d > DateTime.Today)))
            .Returns(olderBalance.Task)
            .ReturnsAsync(new Ledger { CardIdm = cardIdm, Balance = 500 });

        var older = _history.LoadHistoryLedgersAsync();
        await _history.LoadHistoryLedgersAsync();
        _history.HistoryCurrentBalance.Should().Be(500, "前提: 後の読み込みの残額が反映されていること");

        olderBalance.SetResult(new Ledger { CardIdm = cardIdm, Balance = 9999 });
        await older;

        _history.HistoryCurrentBalance.Should().Be(500, "先に始めた読み込みの古い残額で上書きしない");
    }

    /// <summary>
    /// 読み込みを待つ間に履歴を閉じたら、閉じた一覧へ行を詰め直さず、表示し直さない。
    /// </summary>
    [Fact]
    public async Task ShowCardHistoryAsync_読み込みを待つ間に閉じられたら_行を詰め直さず表示し直さないこと()
    {
        const string cardIdm = "0102030405060708";
        var pending = new TaskCompletionSource<(IEnumerable<Ledger>, int)>();
        _ledgerRepositoryMock.Setup(r => r.GetPagedAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(pending.Task);

        var show = _history.ShowCardHistoryAsync(new IcCard { CardIdm = cardIdm, CardType = "はやかけん", CardNumber = "5042" });
        _history.CloseHistory();
        pending.SetResult((CreateLoadLedgers(cardIdm, 1, 2), 2));
        await show;

        _history.HistoryLedgers.Should().BeEmpty("閉じた一覧へ前のカードの行を詰め直さない");
        _history.IsHistoryVisible.Should().BeFalse("閉じた履歴を表示し直さない");
    }

    /// <summary>
    /// チェックを引き継がない読み込み（統合・削除の直後）を、引き継ぐ読み込み（定期の再読込）が追い越しても、
    /// チェックを戻さない。
    /// </summary>
    [Fact]
    public async Task LoadHistoryLedgersAsync_引き継がない読み込みを追い越した読み込みは_チェックを戻さないこと()
    {
        const string cardIdm = "0102030405060708";
        _history.HistoryCard = new CardDto { CardIdm = cardIdm, CardNumber = "5042" };
        var afterMerge = new TaskCompletionSource<(IEnumerable<Ledger>, int)>();
        _ledgerRepositoryMock.SetupSequence(r => r.GetPagedAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((CreateLoadLedgers(cardIdm, 1, 2), 2))
            .Returns(afterMerge.Task)
            .ReturnsAsync((CreateLoadLedgers(cardIdm, 1, 2), 2));

        await _history.LoadHistoryLedgersAsync();
        _history.HistoryLedgers[0].IsChecked = true;
        _history.HistoryLedgers[1].IsChecked = true;

        var nonPreserving = _history.LoadHistoryLedgersAsync(preserveCheckedRows: false);
        await _history.LoadHistoryLedgersAsync(preserveCheckedRows: true);
        afterMerge.SetResult((CreateLoadLedgers(cardIdm, 1, 2), 2));
        await nonPreserving;

        _history.HistoryLedgers.Should().HaveCount(2);
        _history.HistoryLedgers.Should().OnlyContain(d => !d.IsChecked,
            "チェックを引き継がない読み込みが始まった後は、それを追い越した読み込みもチェックを戻さない");
    }

    /// <summary>
    /// 対の表明: 引き継ぐ読み込みどうしなら、追い越しがあってもチェックは残る（引き継ぎを一律に止めていないこと）。
    /// </summary>
    [Fact]
    public async Task LoadHistoryLedgersAsync_引き継ぐ読み込みどうしなら_チェックが残ること()
    {
        const string cardIdm = "0102030405060708";
        _history.HistoryCard = new CardDto { CardIdm = cardIdm, CardNumber = "5042" };
        var pendingRefresh = new TaskCompletionSource<(IEnumerable<Ledger>, int)>();
        _ledgerRepositoryMock.SetupSequence(r => r.GetPagedAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((CreateLoadLedgers(cardIdm, 1, 2), 2))
            .Returns(pendingRefresh.Task)
            .ReturnsAsync((CreateLoadLedgers(cardIdm, 1, 2), 2));

        await _history.LoadHistoryLedgersAsync();
        _history.HistoryLedgers[0].IsChecked = true;

        var older = _history.LoadHistoryLedgersAsync(preserveCheckedRows: true);
        await _history.LoadHistoryLedgersAsync(preserveCheckedRows: true);
        pendingRefresh.SetResult((CreateLoadLedgers(cardIdm, 1, 2), 2));
        await older;

        _history.HistoryLedgers.Where(d => d.IsChecked).Select(d => d.Id).Should().Equal(new[] { 1 });
    }

    /// <summary>
    /// 整合性チェックが DB を待つ間に別のカードの履歴へ切り替わっても、検査したカードの警告として出す
    /// （表示中のカードの警告を、別のカードの検査結果で立てない）。
    /// </summary>
    [Fact]
    public async Task ShowBalanceInconsistencyAsync_検査を待つ間に別のカードへ切り替わっても_検査したカードの警告として出すこと()
    {
        const string cardA = "0A0A0A0A0A0A0A0A";
        const string cardB = "0B0B0B0B0B0B0B0B";
        var displayPeriodCheck = new TaskCompletionSource<IEnumerable<Ledger>>();
        var brokenChain = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = cardA, Date = DateTime.Today.AddDays(-2), Summary = "役務費によりチャージ", Income = 1000, Balance = 1000 },
            new Ledger { Id = 2, CardIdm = cardA, Date = DateTime.Today.AddDays(-1), Summary = "鉄道（博多～天神）", Expense = 100, Balance = 500 },
        };
        _ledgerRepositoryMock.SetupSequence(r => r.GetByDateRangeAsync(cardA, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(brokenChain)
            .Returns(displayPeriodCheck.Task);

        var show = _history.ShowBalanceInconsistencyAsync(new IcCard { CardIdm = cardA, CardType = "はやかけん", CardNumber = "001" });

        // 表示期間の検査を待っている間に、別のカードの履歴が開かれる（待機中のカードタッチ等）
        _history.HistoryCard = new CardDto { CardIdm = cardB, CardType = "nimoca", CardNumber = "002" };
        displayPeriodCheck.SetResult(brokenChain);
        await show;

        _host.WarningMessages.Should().NotContain(w => w.CardIdm == cardB,
            "カード A の検査結果でカード B の警告を立てない");
        _host.WarningMessages.Should().Contain(w => w.CardIdm == cardA && w.Type == WarningType.BalanceInconsistency,
            "検査したカード A の警告として出す");
    }

    #endregion

    #region 全カード残高整合性チェック（Issue #1058）

    [Fact]
    public async Task CheckAllCardsConsistencyAsync_不整合のあるカードに警告が追加されること()
    {
        // Arrange: カード1件を返す
        var card = new IcCard
        {
            CardIdm = "0101020304050607",
            CardType = "はやかけん",
            CardNumber = "5042",
            IsDeleted = false
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { card });

        // 不整合のあるLedgerデータ: 2件目の残高が不正
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = card.CardIdm, Date = new DateTime(2026, 2, 27), Income = 0, Expense = 210, Balance = 1736 },
            new Ledger { Id = 2, CardIdm = card.CardIdm, Date = new DateTime(2026, 3, 2), Income = 0, Expense = 210, Balance = 1426 }
            // 期待値: 1736 - 210 = 1526 ≠ 1426
        };
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(
                card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);

        // Act
        await _history.CheckAllCardsConsistencyAsync();

        // Assert
        _host.WarningMessages.Should().ContainSingle(w =>
            w.Type == WarningType.BalanceInconsistency &&
            w.CardIdm == card.CardIdm);
        _host.WarningMessages.First(w => w.Type == WarningType.BalanceInconsistency)
            .DisplayText.Should().Contain("1件");
    }

    [Fact]
    public async Task CheckAllCardsConsistencyAsync_整合性のあるカードには警告が追加されないこと()
    {
        // Arrange
        var card = new IcCard
        {
            CardIdm = "0101020304050607",
            CardType = "はやかけん",
            CardNumber = "5042",
            IsDeleted = false
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { card });

        // 整合性のあるLedgerデータ
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = card.CardIdm, Date = new DateTime(2026, 2, 27), Income = 0, Expense = 210, Balance = 1736 },
            new Ledger { Id = 2, CardIdm = card.CardIdm, Date = new DateTime(2026, 3, 2), Income = 0, Expense = 210, Balance = 1526 }
            // 期待値: 1736 - 210 = 1526 ✓
        };
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(
                card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);

        // Act
        await _history.CheckAllCardsConsistencyAsync();

        // Assert
        _host.WarningMessages.Should().NotContain(w =>
            w.Type == WarningType.BalanceInconsistency);
    }

    [Fact]
    public async Task CheckAllCardsConsistencyAsync_削除済みカードはスキップされること()
    {
        // Arrange
        var deletedCard = new IcCard
        {
            CardIdm = "0101020304050607",
            CardType = "はやかけん",
            CardNumber = "5042",
            IsDeleted = true
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { deletedCard });

        // Act
        await _history.CheckAllCardsConsistencyAsync();

        // Assert: 削除済みカードに対してはチェックが実行されない
        _ledgerRepositoryMock.Verify(
            r => r.GetByDateRangeAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()),
            Times.Never);
        _host.WarningMessages.Should().NotContain(w =>
            w.Type == WarningType.BalanceInconsistency);
    }

    [Fact]
    public async Task CheckAllCardsConsistencyAsync_払戻済みカードはスキップされること()
    {
        // Issue #1947: 除去側（RefreshDashboardAsync）は残額ダッシュボードの母集団に
        // 居ないカードの BalanceInconsistency 警告を取り除くため、生成側が払戻済みカードで
        // 警告を立てると「出してすぐ黙って消える」状態になる（6 年保存台帳の不整合が
        // 誰の操作にも紐づかず消える）。生成側と除去側の判定条件を揃える。
        var refundedCard = new IcCard
        {
            CardIdm = "0101020304050607",
            CardType = "はやかけん",
            CardNumber = "5042",
            IsRefunded = true
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { refundedCard });

        // Act
        await _history.CheckAllCardsConsistencyAsync();

        // Assert
        _ledgerRepositoryMock.Verify(
            r => r.GetByDateRangeAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()),
            Times.Never);
        _host.WarningMessages.Should().NotContain(w =>
            w.Type == WarningType.BalanceInconsistency);
    }

    [Fact]
    public async Task CheckAllCardsConsistencyAsync_貸出中のカードはスキップされないこと()
    {
        // 対の表明。母集団を IsAvailableForLending（!IsLent を含む）にした実装でも
        // 上の 2 件（削除済み・払戻済み）は緑になるため、これが無いと絞りすぎを検出できない。
        var lentCard = new IcCard
        {
            CardIdm = "0101020304050607",
            CardType = "はやかけん",
            CardNumber = "5042",
            IsLent = true
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { lentCard });

        // Act
        await _history.CheckAllCardsConsistencyAsync();

        // Assert: 貸出中でも整合性チェックは実行される
        // （実 LedgerConsistencyChecker が _ledgerRepositoryMock を読むので、その呼び出しで観測する）
        _ledgerRepositoryMock.Verify(
            r => r.GetByDateRangeAsync(lentCard.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()),
            Times.Once);
    }

    [Fact]
    public async Task CheckAllCardsConsistencyAsync_既存の不整合警告が更新されること()
    {
        // Arrange: 既存の警告がある状態
        _host.WarningMessages.Add(new WarningItem
        {
            DisplayText = "⚠️ 残高の不整合が3件あります（はやかけん 5042）",
            Type = WarningType.BalanceInconsistency,
            CardIdm = "0101020304050607"
        });

        var card = new IcCard
        {
            CardIdm = "0101020304050607",
            CardType = "はやかけん",
            CardNumber = "5042",
            IsDeleted = false
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { card });

        // 整合性が取れているデータ（不整合が解消された状態）
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(
                card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>());

        // Act
        await _history.CheckAllCardsConsistencyAsync();

        // Assert: 既存の警告が削除されていること
        _host.WarningMessages.Should().NotContain(w =>
            w.Type == WarningType.BalanceInconsistency);
    }

    #endregion

    #region 繰越行表示テスト（Issue #1155）

    [Fact]
    public async Task BuildCarryoverRowAsync_4月_前年度繰越行が生成されること()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        _ledgerRepositoryMock.Setup(r => r.GetCarryoverBalanceAsync(cardIdm, 2025))
            .ReturnsAsync(5000);

        // Act
        var result = await _history.BuildCarryoverRowAsync(cardIdm, 2026, 4);

        // Assert
        result.Should().NotBeNull();
        result.IsCarryoverRow.Should().BeTrue();
        result.Summary.Should().Be(SummaryGenerator.GetCarryoverFromPreviousYearSummary());
        result.Income.Should().Be(5000);
        result.Balance.Should().Be(5000);
        result.Expense.Should().Be(0);
        result.Date.Should().Be(new DateTime(2026, 4, 1));
        result.StaffName.Should().BeNull();
    }

    [Fact]
    public async Task BuildCarryoverRowAsync_4月以外_前月繰越行が生成されること()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var previousLedger = new Ledger { Balance = 3000 };
        _ledgerRepositoryMock.Setup(r => r.GetLatestBeforeDateAsync(cardIdm, new DateTime(2026, 7, 1)))
            .ReturnsAsync(previousLedger);

        // Act
        var result = await _history.BuildCarryoverRowAsync(cardIdm, 2026, 7);

        // Assert
        result.Should().NotBeNull();
        result.IsCarryoverRow.Should().BeTrue();
        result.Summary.Should().Be(SummaryGenerator.GetCarryoverFromPreviousMonthSummary(6));
        result.Income.Should().Be(0, "月次繰越の受入欄は空欄");
        result.Balance.Should().Be(3000);
        result.Date.Should().Be(new DateTime(2026, 7, 1));
    }

    [Fact]
    public async Task BuildCarryoverRowAsync_前年度データなし_nullが返ること()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        _ledgerRepositoryMock.Setup(r => r.GetCarryoverBalanceAsync(cardIdm, 2025))
            .ReturnsAsync((int?)null);

        // Act
        var result = await _history.BuildCarryoverRowAsync(cardIdm, 2026, 4);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task BuildCarryoverRowAsync_前月データなし_nullが返ること()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        _ledgerRepositoryMock.Setup(r => r.GetLatestBeforeDateAsync(cardIdm, new DateTime(2026, 6, 1)))
            .ReturnsAsync((Ledger?)null);

        // Act
        var result = await _history.BuildCarryoverRowAsync(cardIdm, 2026, 6);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task BuildCarryoverRowAsync_1月_前月は12月であること()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var previousLedger = new Ledger { Balance = 2000 };
        _ledgerRepositoryMock.Setup(r => r.GetLatestBeforeDateAsync(cardIdm, new DateTime(2026, 1, 1)))
            .ReturnsAsync(previousLedger);

        // Act
        var result = await _history.BuildCarryoverRowAsync(cardIdm, 2026, 1);

        // Assert
        result.Should().NotBeNull();
        result.Summary.Should().Be(SummaryGenerator.GetCarryoverFromPreviousMonthSummary(12));
        result.Balance.Should().Be(2000);
    }

    #endregion

    #region 履歴削除フロー（Issue #1486 / Issue #1574）

    /// <summary>
    /// Issue #1574: 貸出中レコード（IsLentRecord=true）の削除を試みた場合、
    /// 旧仕様（Issue #1486）の <c>NavigationService.ShowWarning</c> による削除拒否は行われない。
    /// 代わりに通常レコードと同じ認証フローへ進む。
    /// </summary>
    [Fact]
    public async Task DeleteLedgerRow_LentRecord_DoesNotShowBlockingWarning()
    {
        // Arrange
        var lentLedger = new LedgerDto
        {
            Id = 101,
            IsLentRecord = true,
        };

        // Act
        await _history.DeleteLedgerRowCommand.ExecuteAsync(lentLedger);

        // Assert: 旧仕様の「削除不可」警告は出なくなった（Issue #1574）
        _navigationServiceMock.Verify(
            n => n.ShowWarning(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never,
            "Issue #1574: 貸出中レコードでも削除フローへ進めるよう、旧仕様の拒否警告を撤廃");
    }

    /// <summary>
    /// Issue #1574: 貸出中レコードでも認証フローが起動すること。
    /// 旧仕様（Issue #1486）では認証前に拒否していたが、本 Issue で復旧手段として認証を経由した削除を許可する。
    /// </summary>
    [Fact]
    public async Task DeleteLedgerRow_LentRecord_StartsAuthenticationFlow()
    {
        // Arrange
        var lentLedger = new LedgerDto
        {
            Id = 102,
            IsLentRecord = true,
        };

        // Act
        await _history.DeleteLedgerRowCommand.ExecuteAsync(lentLedger);

        // Assert: 認証は起動する（Mock デフォルトで null 返却 → MessageBox.Show 手前で短絡）
        _staffAuthServiceMock.Verify(
            s => s.RequestAuthenticationAsync(It.IsAny<string>()),
            Times.Once,
            "Issue #1574: 貸出中レコードでも認証フローを開始する（復旧手段の提供）");
    }

    /// <summary>
    /// 認証がキャンセル（null 返却）された場合、貸出中レコードでも削除には進まない。
    /// </summary>
    [Fact]
    public async Task DeleteLedgerRow_LentRecord_WhenAuthCancelled_DoesNotDelete()
    {
        // Arrange
        var lentLedger = new LedgerDto
        {
            Id = 103,
            CardIdm = DeleteConflictCardIdm,
            IsLentRecord = true,
        };
        // Issue #2104: 認証以外（確認・読み取り・削除）はすべて成功する状態にしてから、認証だけを
        // キャンセルさせる。確認ダイアログを未設定（既定で false）のままにすると、認証ゲートを
        // 外しても確認の「いいえ」で止まるため、ゲートの有無を区別できなかった。
        ArrangeLedgerDelete(
            new Ledger { Id = 103, CardIdm = DeleteConflictCardIdm, IsLentRecord = true },
            deleted: true);
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync((StaffAuthResult)null);

        // Act
        await _history.DeleteLedgerRowCommand.ExecuteAsync(lentLedger);

        // Assert
        _navigationServiceMock.Verify(
            d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never,
            "認証キャンセル時は確認ダイアログを出さない");
        _ledgerRepositoryMock.Verify(
            r => r.DeleteAsync(It.IsAny<int>(), It.IsAny<SQLiteTransaction>()),
            Times.Never,
            "認証キャンセル時は削除に進まない");
        _cardRepositoryMock.Verify(
            c => c.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()),
            Times.Never,
            "認証キャンセル時は is_lent リセットも行わない");
    }

    /// <summary>
    /// nullの ledger を渡された場合は、警告も認証も削除も一切起こさないこと（既存ガード仕様）。
    /// </summary>
    [Fact]
    public async Task DeleteLedgerRow_NullLedger_DoesNothing()
    {
        // Act
        await _history.DeleteLedgerRowCommand.ExecuteAsync(null);

        // Assert
        _navigationServiceMock.Verify(
            n => n.ShowWarning(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
        _staffAuthServiceMock.Verify(
            s => s.RequestAuthenticationAsync(It.IsAny<string>()),
            Times.Never);
        _ledgerRepositoryMock.Verify(
            r => r.DeleteAsync(It.IsAny<int>(), It.IsAny<SQLiteTransaction>()),
            Times.Never);
    }

    #endregion

    #region Issue #1814: 履歴ページ番号のクランプ後の再取得テスト

    /// <summary>
    /// 履歴ページングテストの共通アレンジ。
    /// 「呼び出し時点の totalCount」を返す関数を受け取り、GetPagedAsync を
    /// 「要求ページが総ページ数を超えていれば空、そうでなければ pageSize 件」で応答させる。
    /// これは実装（LedgerRepository.GetPagedAsync の OFFSET/LIMIT）と同じ振る舞い。
    /// </summary>
    private List<int> ArrangeHistoryPaging(Func<int, int> totalCountForCall, int pageSize)
        => ArrangeHistoryPaging(_history, _ledgerRepositoryMock, totalCountForCall, pageSize);

    /// <summary>
    /// <see cref="ArrangeHistoryPaging(Func{int, int}, int)"/> の本体。親のフロー（定期更新・返却後処理）を通る
    /// <c>MainViewModelTests</c> も同じ応答のモックを使うため、履歴パネルとリポジトリのモックを受け取る形で共有する。
    /// </summary>
    internal static List<int> ArrangeHistoryPaging(
        HistoryPanelViewModel history, Mock<ILedgerRepository> ledgerRepositoryMock,
        Func<int, int> totalCountForCall, int pageSize)
    {
        var requestedPages = new List<int>();

        history.HistoryCard = new CardDto { CardIdm = "0123456789ABCDEF", CardNumber = "A-1" };
        history.HistoryFromDate = new DateTime(2026, 8, 1);
        history.HistoryToDate = new DateTime(2026, 8, 31);
        history.HistoryPageSize = pageSize;

        // LoadHistoryLedgersAsync は末尾で統合取り消しボタンの可否を問い合わせる
        ledgerRepositoryMock
            .Setup(r => r.GetMergeHistoriesAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>());

        ledgerRepositoryMock
            .Setup(r => r.GetPagedAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((string _, DateTime _, DateTime _, int page, int size) =>
            {
                var totalCount = totalCountForCall(requestedPages.Count);
                requestedPages.Add(page);

                var offset = (page - 1) * size;
                var take = Math.Max(0, Math.Min(size, totalCount - offset));
                var items = Enumerable.Range(0, take)
                    .Select(i => new Ledger
                    {
                        Id = offset + i + 1,
                        CardIdm = "0123456789ABCDEF",
                        Date = new DateTime(2026, 8, 10),
                        Summary = "鉄道（A駅～B駅）",
                        Expense = 210,
                        Balance = 1000 - (offset + i) * 210,
                    })
                    .ToList();

                return ((IEnumerable<Ledger>)items, totalCount);
            });

        return requestedPages;
    }

    /// <summary>
    /// Issue #1814 の中核。総件数が減って現在ページが無効になったら、
    /// クランプしたページで取り直し「一覧が空なのに件数表示は全件」という
    /// 食い違いを残さないこと。
    /// </summary>
    [Fact]
    public async Task LoadHistoryLedgersAsync_総件数減少でクランプされたら再取得して一覧と件数表示を一致させること()
    {
        // Arrange: 2ページ目を表示中に、総件数が 60 件 → 30 件（＝1ページ分）へ減った
        var requestedPages = ArrangeHistoryPaging(_ => 30, pageSize: 30);
        _history.HistoryCurrentPage = 2;

        // Act
        await _history.LoadHistoryLedgersAsync();

        // Assert: クランプ後のページで取り直している
        requestedPages.Should().Equal(new[] { 2, 1 },
            "クランプ前のページで空の結果を受け取ったら、クランプ後のページで取り直すこと");
        _history.HistoryCurrentPage.Should().Be(1);
        _history.HistoryTotalPages.Should().Be(1);

        // Assert: 一覧が空のまま残らない（Issue #1814 の実害）
        _history.HistoryLedgers.Should().HaveCount(30,
            "クランプ後のページの行が表示されること");

        // Assert: 件数表示・ページ表示と一覧の中身が一致する
        _history.HistoryStatusMessage.Should().Be("1～30件を表示（全30件）");
        _history.HistoryPageDisplay.Should().Be("1 / 1");
    }

    /// <summary>
    /// クランプが不要な通常のページ読み込みでは取り直さないこと。
    /// （再取得ロジックが常に 2 回問い合わせる実装へ退行していないことを固定する）
    /// </summary>
    [Fact]
    public async Task LoadHistoryLedgersAsync_クランプ不要なら再取得しないこと()
    {
        // Arrange: 全 60 件（2 ページ）の 2 ページ目
        var requestedPages = ArrangeHistoryPaging(_ => 60, pageSize: 30);
        _history.HistoryCurrentPage = 2;

        // Act
        await _history.LoadHistoryLedgersAsync();

        // Assert
        requestedPages.Should().Equal(new[] { 2 }, "クランプが起きなければ 1 回だけ問い合わせること");
        _history.HistoryCurrentPage.Should().Be(2);
        _history.HistoryLedgers.Should().HaveCount(30);
        _history.HistoryStatusMessage.Should().Be("31～60件を表示（全60件）");
    }

    /// <summary>
    /// 共有モードで他 PC の削除が連続してもループが止まり、かつ**復旧不能な状態に着地しない**こと。
    /// 上限到達時は 1 ページ目へ戻して取得を確定する。
    /// </summary>
    /// <remarks>
    /// クランプしたページで取り直さずに抜けると、一覧はクランプ前の無効なページの結果（＝空）で
    /// ページ番号だけがクランプ後になる。クランプ先が 1 ページ目だとページ送りが全て
    /// CanExecute=false になり、**Issue #1814 が直そうとしている状態そのもの**に着地する。
    /// 1 ページ目は totalCount &gt; 0 なら必ず行を返す（OFFSET 0）ため、そこへ落とせば決定的に収束する。
    /// </remarks>
    [Fact]
    public async Task LoadHistoryLedgersAsync_クランプが連続しても1ページ目へ戻して整合した状態で確定すること()
    {
        // Arrange: 取得のたびに総件数が減り続ける（40 → 30 → 20 → 10 …）
        var totalCounts = new[] { 40, 30, 20, 10, 10, 10 };
        var requestedPages = ArrangeHistoryPaging(call => totalCounts[Math.Min(call, totalCounts.Length - 1)], pageSize: 10);
        _history.HistoryCurrentPage = 5;

        // Act
        await _history.LoadHistoryLedgersAsync();

        // Assert: クランプ 3 回で打ち切り、最後に 1 ページ目を取得して確定する（無限ループしない）
        requestedPages.Should().Equal(new[] { 5, 4, 3, 1 },
            "クランプ上限に達したら 1 ページ目へ戻して 1 回だけ取り直すこと");
        _history.HistoryCurrentPage.Should().Be(1);

        // Assert: 一覧・件数表示・ページ番号がすべて同じ取得に由来する（#1814 の不変条件）
        _history.HistoryLedgers.Should().HaveCount(10,
            "打ち切り経路でも一覧が空のまま残らないこと");
        _history.HistoryTotalCount.Should().Be(10);
        _history.HistoryTotalPages.Should().Be(1);
        _history.HistoryStatusMessage.Should().Be("1～10件を表示（全10件）");
    }

    /// <summary>
    /// 履歴が 0 件になった場合はページ 1 へ戻し、「該当する履歴がありません」を表示すること。
    /// </summary>
    [Fact]
    public async Task LoadHistoryLedgersAsync_総件数0ならページ1へ戻すこと()
    {
        // Arrange
        var requestedPages = ArrangeHistoryPaging(_ => 0, pageSize: 30);
        _history.HistoryCurrentPage = 3;

        // Act
        await _history.LoadHistoryLedgersAsync();

        // Assert
        requestedPages.Should().Equal(new[] { 3, 1 });
        _history.HistoryCurrentPage.Should().Be(1);
        _history.HistoryTotalPages.Should().Be(1);
        _history.HistoryLedgers.Should().BeEmpty();
        _history.HistoryStatusMessage.Should().Be("該当する履歴がありません");
    }

    #endregion

    #region Issue #1837: 履歴削除の確認ダイアログ（MessageBox 直呼びから IDialogService へ移行）

    /*
     * 移行前は MessageBox.Show の直呼びだったため、この経路の単体テストは 1 件も書けなかった
     * （実モーダルが開いてテストランナーが止まる）。IDialogService へ移した副次的な利得として、
     * 「確認で『いいえ』を選んだら 6 年保存の台帳を消さない」というガードを固定できる。
     */

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteLedgerRow_確認の結果に従って削除すること(bool confirmed)
    {
        // Arrange
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync(new StaffAuthResult { Idm = "AABBCCDDEEFF0011", StaffName = "田中太郎" });
        _navigationServiceMock
            .Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), "履歴の削除"))
            .Returns(confirmed);
        _ledgerRepositoryMock
            .Setup(r => r.GetByIdAsync(It.IsAny<int>()))
            .ReturnsAsync((Ledger)null);

        // Issue #1944: 読み取りが null（他 PC が先に削除）でも無言で戻らず、一覧を再読込して
        // 競合を案内するようになった。後段のダッシュボード更新・警告再チェックはメイン画面への要求で、
        // 記録用ホスト（_host）が受けるため準備は要らない（Issue #2159）。
        // 本テストの表明（確認の結果に従って GetByIdAsync まで進むか）は変えていない。
        _ledgerRepositoryMock
            .Setup(r => r.GetAllLatestBalancesAsync())
            .ReturnsAsync(new Dictionary<string, (int Balance, DateTime? LastUsageDate)>());

        var dto = new LedgerDto
        {
            Id = 42,
            Date = new DateTime(2026, 1, 10),
            DateDisplay = "R8.1.10",
            Summary = "鉄道（天神～博多）",
            Balance = 2300
        };

        // Act
        await _history.DeleteLedgerRow(dto);

        // Assert: 確認は IDialogService 経由で 1 度だけ行う
        _navigationServiceMock.Verify(
            d => d.ShowWarningConfirmation(It.IsAny<string>(), "履歴の削除"), Times.Once,
            "確認は MessageBox 直呼びではなく IDialogService 経由で行うこと（Issue #1837）");

        // 「いいえ」なら対象行の読み取りにすら進まない（＝何も消さない）
        _ledgerRepositoryMock.Verify(
            r => r.GetByIdAsync(It.IsAny<int>()),
            confirmed ? Times.Once() : Times.Never(),
            "確認で「いいえ」を選んだら削除処理へ進まないこと");
    }

    /// <summary>
    /// 認証をキャンセルした場合は確認ダイアログを出さないこと（対の表明）。
    /// これが無いと「認証を無視して必ず確認する」実装でも上のテストは緑になる。
    /// </summary>
    [Fact]
    public async Task DeleteLedgerRow_認証をキャンセルしたら確認を出さないこと()
    {
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync((StaffAuthResult)null);

        await _history.DeleteLedgerRow(new LedgerDto { Id = 42 });

        _navigationServiceMock.Verify(
            d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    #endregion

    #region 履歴削除の競合検出（Issue #1944）

    private const string DeleteConflictCardIdm = "0123456789ABCDEF";

    /// <summary>
    /// 削除の案内で名指しする対象（実装と同じ組み立て）。
    /// </summary>
    private const string DeleteConflictTarget = "履歴「R8.1.10 鉄道（天神～博多）」";

    /// <summary>
    /// 履歴削除フローを「認証済み・確認済み」の状態まで進め、対象行の読み取り結果と
    /// DELETE の影響行数を指定する。戻り値は一覧再読込の要求ページ記録（再読込の観測用）。
    /// </summary>
    /// <param name="fullLedger">
    /// <c>GetByIdAsync</c> が返す行。<c>null</c> は「読み取りの時点で既に他 PC が削除済み」を表す
    /// </param>
    /// <param name="deleted"><c>DeleteAsync</c> の戻り値。<c>false</c> ＝影響行数 0 ＝競合</param>
    private List<int> ArrangeLedgerDelete(Ledger fullLedger, bool deleted)
    {
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync(new StaffAuthResult { Idm = "AABBCCDDEEFF0011", StaffName = "田中太郎" });
        _navigationServiceMock
            .Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), "履歴の削除"))
            .Returns(true);
        _ledgerRepositoryMock
            .Setup(r => r.GetByIdAsync(It.IsAny<int>()))
            .ReturnsAsync(fullLedger);
        _ledgerRepositoryMock
            .Setup(r => r.DeleteAsync(It.IsAny<int>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(deleted);

        // 削除の後段のダッシュボード更新・警告再チェックはメイン画面の仕事で、ここでは記録用ホスト（_host）が
        // 受けて記録するだけなので準備は要らない（Issue #2159。親を通した経路は MainViewModelTests の連携テスト）

        // 削除後の一覧再読込を観測できるようにする（他 PC が削除済みなので総件数 0）
        return ArrangeHistoryPaging(_ => 0, pageSize: 30);
    }

    private static LedgerDto DeleteTargetDto() => new LedgerDto
    {
        Id = 42,
        CardIdm = DeleteConflictCardIdm,
        Date = new DateTime(2026, 1, 10),
        DateDisplay = "R8.1.10",
        Summary = "鉄道（天神～博多）",
        Balance = 2300,
    };

    /// <summary>
    /// Issue #1953: 貸出中レコードを削除したあとの <c>is_lent</c> リセットが 0 行（＝競合）でも、
    /// <b>履歴削除そのものは成功として扱う</b>こと。
    /// </summary>
    /// <remarks>
    /// このリセットは履歴削除のコミットが確定した<b>あと</b>に走る後処理であり、失敗を成否へ
    /// 巻き込むと「削除は済んでいるのに削除できなかったと案内する」（
    /// <c>.claude/rules/development-conventions.md</c>「コミット確定後の後処理を、成否の判定に
    /// 巻き込まない」Issue #1805 / #1727）。0 行になる原因は「他 PC がこのカードを論理削除した」
    /// ことだが、論理削除の条件が <c>is_lent = 0</c>（<c>CardRepository.DeleteAsync</c> の WHERE 句）
    /// である以上、そのカードの <c>is_lent</c> は既に 0 で運用に影響しない。
    /// 無言にはせず Warning ログ（本番のログファイルに出るレベル。Issue #1716）で痕跡を残す。
    /// </remarks>
    [Fact]
    public async Task DeleteLedgerRow_貸出状態リセットが0行でも削除を失敗として案内しないこと()
    {
        var loggerMock = new Mock<ILogger<HistoryPanelViewModel>>();
        var history = CreateHistoryPanel(loggerMock.Object);
        ArrangeLedgerDelete(
            new Ledger { Id = 42, CardIdm = DeleteConflictCardIdm, IsLentRecord = true },
            deleted: true);
        _ledgerRepositoryMock
            .Setup(r => r.HasOtherLentRecordsAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(false);
        _cardRepositoryMock
            .Setup(c => c.UpdateLentStatusAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        await history.DeleteLedgerRow(DeleteTargetDto());

        _navigationServiceMock.Verify(
            d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never,
            "履歴削除は確定済み。後処理の失敗で「削除できませんでした」と案内すると、" +
            "職員は削除されていないと誤解する（Issue #1953 / #1805）");
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("貸出状態")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once,
            "無言で握りつぶさず本番ログへ痕跡を残すこと（Issue #1716）");
    }

    /// <summary>
    /// 対の表明: リセットが成功する通常の削除では Warning ログを出さないこと。
    /// </summary>
    /// <remarks>
    /// これが無いと「常に Warning を出す」実装でも上のテストが緑になり、
    /// 起動のたびにログが肥大化する退行に気付けない（Issue #1730 の方針）。
    /// </remarks>
    [Fact]
    public async Task DeleteLedgerRow_貸出状態リセットが成功したらWarningを出さないこと()
    {
        var loggerMock = new Mock<ILogger<HistoryPanelViewModel>>();
        var history = CreateHistoryPanel(loggerMock.Object);
        ArrangeLedgerDelete(
            new Ledger { Id = 42, CardIdm = DeleteConflictCardIdm, IsLentRecord = true },
            deleted: true);
        _ledgerRepositoryMock
            .Setup(r => r.HasOtherLentRecordsAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(false);
        _cardRepositoryMock
            .Setup(c => c.UpdateLentStatusAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        await history.DeleteLedgerRow(DeleteTargetDto());

        _cardRepositoryMock.Verify(
            c => c.UpdateLentStatusAsync(DeleteConflictCardIdm, false, null, null),
            Times.Once,
            "貸出中レコードを消したらリセット自体は行うこと（Issue #1574）");
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("貸出状態")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Never);
    }

    /// <summary>
    /// Issue #1944 の中核。<c>DeleteAsync</c> が 0 行（＝競合）を返したら、
    /// 6 年保存の監査ログへ「削除した」と記録してはならない。
    /// </summary>
    [Fact]
    public async Task DeleteLedgerRow_削除が0行なら監査ログを記録しないこと()
    {
        ArrangeLedgerDelete(new Ledger { Id = 42, CardIdm = DeleteConflictCardIdm }, deleted: false);

        await _history.DeleteLedgerRow(DeleteTargetDto());

        _operationLogRepositoryMock.Verify(
            r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()),
            Times.Never,
            "削除していないのに「削除した」と記録すると、履歴の個別削除（Issue #635）の" +
            "訂正の追跡ができなくなる（Issue #1944）");
    }

    /// <summary>
    /// 削除していない以上、書き込みに紐付いていた副作用（<c>ic_card.is_lent</c> の解除）も行わない
    /// （<c>.claude/rules/development-conventions.md</c> Issue #1760）。
    /// </summary>
    [Fact]
    public async Task DeleteLedgerRow_削除が0行ならis_lentを解除しないこと()
    {
        ArrangeLedgerDelete(
            new Ledger { Id = 42, CardIdm = DeleteConflictCardIdm, IsLentRecord = true },
            deleted: false);
        _ledgerRepositoryMock
            .Setup(r => r.HasOtherLentRecordsAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(false);

        await _history.DeleteLedgerRow(DeleteTargetDto());

        _cardRepositoryMock.Verify(
            c => c.UpdateLentStatusAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()),
            Times.Never,
            "貸出中レコードを消せていないのに is_lent を解除すると、他 PC の状態まで巻き込む（Issue #1944）");
    }

    /// <summary>
    /// 競合は無言で握りつぶさず、<b>一覧を再読込してから</b>案内すること
    /// （文言が「再読み込みしました」と述べる以上、先に再読込しないと案内どおりに操作できない。Issue #1753）。
    /// </summary>
    [Fact]
    public async Task DeleteLedgerRow_削除が0行なら一覧を再読込してから競合を案内すること()
    {
        var requestedPages = ArrangeLedgerDelete(
            new Ledger { Id = 42, CardIdm = DeleteConflictCardIdm }, deleted: false);

        string message = null;
        var reloadCountAtNotification = -1;
        _navigationServiceMock
            .Setup(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()))
            .Callback((string m, string _) =>
            {
                message = m;
                reloadCountAtNotification = requestedPages.Count;
            });

        await _history.DeleteLedgerRow(DeleteTargetDto());

        message.Should().NotBeNull("競合を無言で握りつぶすと、削除できたように見える（Issue #1944）");
        message.Should().Be(
            ICCardManager.Common.ConcurrencyConflictMessage.ForDelete(DeleteConflictTarget, "履歴一覧"),
            "競合の文言は Common/ConcurrencyConflictMessage へ集約する（Issue #1759）");
        reloadCountAtNotification.Should().BeGreaterThan(
            0, "案内する側が先に一覧を再読込すること（Issue #1753）");
    }

    /// <summary>
    /// 読み取りの時点で対象行が消えていた場合も、同じ競合として案内すること。
    /// </summary>
    /// <remarks>
    /// 旧実装は <c>if (fullLedger == null) return;</c> で無言で戻っており、
    /// 同じユーザー操作（同じ故障原因）が経路によって「案内あり」と「無反応」に分かれていた
    /// （<c>.claude/rules/error-messages.md</c>「同じ制約違反はすべての経路で同じ例外へ変換する」と同じ形）。
    /// </remarks>
    [Fact]
    public async Task DeleteLedgerRow_対象行が既に消えていたら競合を案内すること()
    {
        ArrangeLedgerDelete(fullLedger: null, deleted: false);

        await _history.DeleteLedgerRow(DeleteTargetDto());

        _navigationServiceMock.Verify(
            d => d.ShowError(
                ICCardManager.Common.ConcurrencyConflictMessage.ForDelete(DeleteConflictTarget, "履歴一覧"),
                It.IsAny<string>()),
            Times.Once,
            "読み取りが null（他 PC が先に削除）でも無言で戻らないこと（Issue #1944）");
        _operationLogRepositoryMock.Verify(
            r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()),
            Times.Never,
            "読み取りが null なら書き込みも行わない（Issue #1760）");
    }

    /// <summary>
    /// 対の表明: 正常な削除を塞いでいないこと。
    /// これが無いと「削除を無条件に競合として扱う」実装でも上の 4 件は緑になる。
    /// </summary>
    [Fact]
    public async Task DeleteLedgerRow_削除できたら監査ログを記録し競合を案内しないこと()
    {
        ArrangeLedgerDelete(new Ledger { Id = 42, CardIdm = DeleteConflictCardIdm }, deleted: true);

        await _history.DeleteLedgerRow(DeleteTargetDto());

        _operationLogRepositoryMock.Verify(
            r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()),
            Times.Once,
            "正常な削除では監査ログを残すこと");
        _navigationServiceMock.Verify(
            d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never,
            "正常な削除を競合として案内しないこと");
    }

    /// <summary>
    /// 対の表明: 貸出中レコードを実際に削除できたときは is_lent を解除すること（Issue #1574 の維持）。
    /// </summary>
    [Fact]
    public async Task DeleteLedgerRow_貸出中レコードを削除できたらis_lentを解除すること()
    {
        ArrangeLedgerDelete(
            new Ledger { Id = 42, CardIdm = DeleteConflictCardIdm, IsLentRecord = true },
            deleted: true);
        _ledgerRepositoryMock
            .Setup(r => r.HasOtherLentRecordsAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(false);

        await _history.DeleteLedgerRow(DeleteTargetDto());

        _cardRepositoryMock.Verify(
            c => c.UpdateLentStatusAsync(DeleteConflictCardIdm, false, null, null),
            Times.Once,
            "Issue #1574 の整合性リセットを、競合検出の導入で壊していないこと");
    }

    #endregion
    #region ホストとの境界（Issue #2159）

    /// <summary>
    /// 画面へ接続しないまま親への要求が起きたら、黙って何もしないのではなく例外にすること。
    /// </summary>
    /// <remarks>
    /// 黙って捨てると、DI の配線漏れが「ダッシュボードが古いまま」「オーバーレイが出ない」の形で潜在化する（#1820）。
    /// </remarks>
    [Fact]
    public async Task ホスト未接続のまま親へ要求すると例外になること()
    {
        var detached = new HistoryPanelViewModel(
            _ledgerRepositoryMock.Object, _cardRepositoryMock.Object, _dbContext, _staffAuthServiceMock.Object,
            _ledgerMergeService, _navigationServiceMock.Object, _operationLoggerMock.Object,
            _ledgerConsistencyChecker, _toastMock.Object);
        detached.HistoryCard = new CardDto { CardIdm = "0123456789ABCDEF", CardNumber = "A-1" };

        Func<Task> act = () => detached.LoadHistoryLedgersAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*AttachHost*");
    }

    /// <summary>
    /// 1 つの履歴パネルを 2 つの画面へ接続しないこと（同じ画面の再接続は許す）。
    /// </summary>
    [Fact]
    public void AttachHostは別の画面への付け替えを拒み同じ画面の再接続は許すこと()
    {
        Action sameHost = () => _history.AttachHost(_host);
        Action otherHost = () => _history.AttachHost(new RecordingHistoryPanelHost());

        sameHost.Should().NotThrow();
        otherHost.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// 履歴の読み込み中は、メイン画面の処理中オーバーレイ（親の <c>IsBusy</c> に束縛）を出すこと。
    /// 読み込みが終われば閉じること。
    /// </summary>
    /// <remarks>
    /// 履歴パネルが自前の処理中状態を持つと、オーバーレイは誰にも束縛されていない側で立ち、画面には出ない。
    /// </remarks>
    [Fact]
    public async Task 履歴の読み込み中は親の処理中スコープを開き終われば閉じること()
    {
        var openScopesDuringRead = -1;
        _history.HistoryCard = new CardDto { CardIdm = "0123456789ABCDEF", CardNumber = "A-1" };
        _ledgerRepositoryMock
            .Setup(r => r.GetPagedAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .Callback(() => openScopesDuringRead = _host.OpenBusyScopes)
            .ReturnsAsync((new List<Ledger>(), 0));

        await _history.LoadHistoryLedgersAsync();

        openScopesDuringRead.Should().Be(1, "DB を読んでいる間はオーバーレイが出ていること");
        _host.BusyMessages.Should().Equal("読み込み中...");
        _host.OpenBusyScopes.Should().Be(0, "読み込みが終わったらオーバーレイを閉じること");
    }

    /// <summary>
    /// 貸出中レコードを削除して <c>is_lent</c> を戻したら、メイン画面へ貸出中一覧の再読込を頼むこと。
    /// 頼むのは一覧の再読込・ダッシュボード更新の後ろ（コミット直後に置くと、これだけが失敗したときに後段がすべて飛ぶ）。
    /// </summary>
    [Fact]
    public async Task 貸出状態を戻したら貸出中一覧の再読込を親へ頼むこと()
    {
        ArrangeLedgerDelete(new Ledger { Id = 42, CardIdm = DeleteConflictCardIdm, IsLentRecord = true }, deleted: true);
        _ledgerRepositoryMock.Setup(r => r.HasOtherLentRecordsAsync(DeleteConflictCardIdm, 42)).ReturnsAsync(false);
        _cardRepositoryMock
            .Setup(c => c.UpdateLentStatusAsync(DeleteConflictCardIdm, false, null, null))
            .ReturnsAsync(true);

        await _history.DeleteLedgerRow(DeleteTargetDto());

        _host.Calls.Should().Equal(
            new[]
            {
                RecordingHistoryPanelHost.BeginBusyCall,
                RecordingHistoryPanelHost.RefreshDashboardCall,
                RecordingHistoryPanelHost.RefreshLentCardsCall,
                RecordingHistoryPanelHost.CheckWarningsCall,
            },
            "一覧の再読込・ダッシュボードの後に貸出中一覧、続いて警告の順（#1753）。" +
            "貸出中一覧の読み直しだけが失敗しても、一覧とダッシュボードは更新済みであること（コードレビューで検出）");
    }

    /// <summary>
    /// 対の表明: 貸出状態を戻さなかった（他の貸出中レコードが残る／競合で 0 行だった）ときは再読込を頼まない。
    /// </summary>
    /// <remarks>
    /// これが無いと「削除のたびに常に頼む」実装でも上のテストが緑になる。
    /// 0 行は他 PC がカードを論理削除した競合で、何も変えていない（#1953）。
    /// </remarks>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task 貸出状態を戻さなかったときは貸出中一覧の再読込を頼まないこと(bool hasOtherLentRecords, bool updated)
    {
        ArrangeLedgerDelete(new Ledger { Id = 42, CardIdm = DeleteConflictCardIdm, IsLentRecord = true }, deleted: true);
        _ledgerRepositoryMock
            .Setup(r => r.HasOtherLentRecordsAsync(DeleteConflictCardIdm, 42))
            .ReturnsAsync(hasOtherLentRecords);
        _cardRepositoryMock
            .Setup(c => c.UpdateLentStatusAsync(DeleteConflictCardIdm, false, null, null))
            .ReturnsAsync(updated);

        await _history.DeleteLedgerRow(DeleteTargetDto());

        _host.CountOf(RecordingHistoryPanelHost.RefreshLentCardsCall).Should().Be(0);
        _host.CountOf(RecordingHistoryPanelHost.RefreshDashboardCall).Should().Be(1,
            "削除そのものは確定しているので、ダッシュボードの更新は頼む（貸出中一覧だけを頼まない）");
    }

    /// <summary>
    /// カードの履歴を開く入口は、前のカードのハイライトを消してから当月の履歴を開くこと。
    /// カードが見つからない（他 PC で削除された等）ときはハイライトを消すだけで開かないこと。
    /// </summary>
    [Fact]
    public async Task ShowCardHistoryAsyncはハイライトを消してから開きカードが無ければ開かないこと()
    {
        var field = typeof(HistoryPanelViewModel).GetField("_balanceInconsistencies",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var markers = (Dictionary<int, (int ExpectedBalance, int ActualBalance, bool IsInitialBalanceCorrection)>)field!.GetValue(_history)!;
        markers[7] = (1000, 900, false);

        await _history.ShowCardHistoryAsync(null);

        markers.Should().BeEmpty("前のカードのハイライトを次の表示へ持ち越さない");
        _history.IsHistoryVisible.Should().BeFalse("カードが無ければ開かない");

        markers[7] = (1000, 900, false);
        var today = DateTime.Today;
        await _history.ShowCardHistoryAsync(new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "A-1" });

        markers.Should().BeEmpty();
        _history.IsHistoryVisible.Should().BeTrue();
        _history.HistoryCard!.CardIdm.Should().Be("0123456789ABCDEF");
        _history.HistoryFromDate.Should().Be(new DateTime(today.Year, today.Month, 1), "当月の 1 日から表示する");
    }

    #endregion
}
