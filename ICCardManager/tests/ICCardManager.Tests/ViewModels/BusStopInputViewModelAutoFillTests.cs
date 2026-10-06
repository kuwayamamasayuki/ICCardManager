using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2251: バス停名入力の候補の並び（明細ごと）・既定値・往復の復路の自動補完の単体テスト。
/// </summary>
/// <remarks>
/// 「入れる側／入れない側」「補完する側／触った欄は上書きしない側」を対で置く（片側だけだと、常に入れる実装・
/// 常に入れない実装のどちらかが緑になる）。
/// </remarks>
public class BusStopInputViewModelAutoFillTests : IDisposable
{
    private const string Lender = "1111000000000001";
    private static readonly DateTime Day = new(2026, 9, 1);

    private readonly Mock<ILedgerRepository> _ledgerRepoMock = new();
    private readonly Mock<ISettingsRepository> _settingsRepoMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();
    private readonly DbContext _dbContext;
    private readonly BusStopInputViewModel _viewModel;

    private List<BusStopUsageStatRow> _stats = new();
    private List<string> _overall = new();

    public BusStopInputViewModelAutoFillTests()
    {
        _ledgerRepoMock.Setup(r => r.GetBusStopSuggestionsAsync(It.IsAny<string>()))
            .ReturnsAsync(() => _overall.Select(s => (s, 1, (DateTime?)null)).ToList());
        _ledgerRepoMock.Setup(r => r.GetBusStopUsageStatsAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(() => _stats);
        _dialogServiceMock.Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);
        _settingsRepoMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        _dbContext = TestDbContextFactory.Create();
        _viewModel = new BusStopInputViewModel(
            _ledgerRepoMock.Object, _settingsRepoMock.Object, _dialogServiceMock.Object, _dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private static BusStopUsageStatRow Stat(string busStops, int amount, bool sameStaff, int count)
        => new() { BusStops = busStops, Amount = amount, IsSameStaff = sameStaff, UsageCount = count };

    private static LedgerDetail Bus(int amount, DateTime useDate, string? busStops = null, int sequence = 1)
        => new()
        {
            LedgerId = 1,
            IsBus = true,
            Amount = amount,
            UseDate = useDate,
            BusStops = busStops,
            SequenceNumber = sequence,
        };

    private Task InitializeAsync(params LedgerDetail[] details)
    {
        var ledger = new Ledger
        {
            Id = 1,
            LenderIdm = Lender,
            Summary = "バス（★）",
            Details = details.ToList(),
        };
        return _viewModel.InitializeWithDetailsAsync(ledger, details);
    }

    #region 候補の並び（明細ごと）

    [Fact]
    public async Task 候補は明細の金額ごとに同じ職員と同じ金額の実績を先に並べること()
    {
        _overall = new List<string> { "全体～Z" };
        _stats = new List<BusStopUsageStatRow>
        {
            Stat("二百円～A", 200, sameStaff: true, count: 1),
            Stat("百五十円～B", 150, sameStaff: true, count: 1),
        };

        await InitializeAsync(Bus(200, Day), Bus(150, Day.AddDays(1), sequence: 2));

        // 空欄にフォーカスしたときの候補（先頭 8 件）が欄ごとに違う
        _viewModel.BusUsages[0].OnTextBoxGotFocus();
        _viewModel.BusUsages[0].FilteredSuggestions.Should().Equal("二百円～A", "百五十円～B", "全体～Z");
        _viewModel.BusUsages[1].OnTextBoxGotFocus();
        _viewModel.BusUsages[1].FilteredSuggestions.Should().Equal("百五十円～B", "二百円～A", "全体～Z");
    }

    [Fact]
    public async Task 利用実績はこの返却の貸出者で取得すること()
    {
        await InitializeAsync(Bus(200, Day));

        _ledgerRepoMock.Verify(r => r.GetBusStopUsageStatsAsync(SummaryGenerator.BusPlaceholder, Lender), Times.Once);
    }

    [Fact]
    public async Task 履歴の未入力一覧から開いたときはその行の貸出者で取得すること()
    {
        const string rowLender = "9999000000000009";
        var ledger = new Ledger
        {
            Id = 7,
            LenderIdm = rowLender,
            Summary = "バス（★）",
            Details = new List<LedgerDetail> { Bus(200, Day) },
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(ledger);

        await _viewModel.InitializeAsync(7);

        _ledgerRepoMock.Verify(r => r.GetBusStopUsageStatsAsync(SummaryGenerator.BusPlaceholder, rowLender), Times.Once);
    }

    [Fact]
    public async Task 複数の台帳の貸出者が1人に決まらなければ職員を指定しないこと()
    {
        var ledgers = new[]
        {
            new Ledger { Id = 0, LenderIdm = Lender, Details = new List<LedgerDetail> { Bus(200, Day) } },
            new Ledger { Id = 0, LenderIdm = "2222000000000002", Details = new List<LedgerDetail> { Bus(200, Day.AddDays(1)) } },
        };

        await _viewModel.InitializeWithLedgersAsync(ledgers);

        _ledgerRepoMock.Verify(r => r.GetBusStopUsageStatsAsync(SummaryGenerator.BusPlaceholder, null), Times.Once);
    }

    [Fact]
    public void ResolveSingleLenderIdm_貸出者が1人ならその職員で無ければnullになること()
    {
        var ledgers = new[]
        {
            new Ledger { LenderIdm = "ABCD000000000001" },
            new Ledger { LenderIdm = "ABCD000000000001" },
            new Ledger { LenderIdm = null },
        };

        BusStopInputViewModel.ResolveSingleLenderIdm(ledgers).Should().Be("ABCD000000000001");
        BusStopInputViewModel.ResolveSingleLenderIdm(new[] { new Ledger { LenderIdm = null } }).Should().BeNull();
    }

    [Fact]
    public async Task 利用実績の読み込みに失敗しても従来の全体の並びで入力できること()
    {
        // 前回の初期化で別の実績を読んでおく。失敗時に捨てなければ、その実績の並び・既定値が残る
        _overall = new List<string> { "全体1～A", "全体2～B" };
        _stats = new List<BusStopUsageStatRow> { Stat("前回～X", 200, sameStaff: true, count: 5) };
        await InitializeAsync(Bus(200, Day));
        _viewModel.BusUsages[0].BusStops.Should().Be("前回～X", "前提: 前回の初期化では既定値が入る");

        _ledgerRepoMock.Setup(r => r.GetBusStopUsageStatsAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("集計の失敗"));

        await InitializeAsync(Bus(200, Day));

        _viewModel.BusUsages[0].OnTextBoxGotFocus();
        _viewModel.BusUsages[0].FilteredSuggestions.Should().Equal("全体1～A", "全体2～B");
        _viewModel.BusUsages[0].BusStops.Should().BeEmpty();
    }

    #endregion

    #region 既定値

    [Fact]
    public async Task 同じ職員と同じ金額で2回以上の単独1位なら既定値を入れて注記を出すこと()
    {
        _stats = new List<BusStopUsageStatRow> { Stat("天神～博多", 200, sameStaff: true, count: 2) };

        await InitializeAsync(Bus(200, Day));

        var item = _viewModel.BusUsages[0];
        item.BusStops.Should().Be("天神～博多");
        item.AutoFillKind.Should().Be(BusStopAutoFillKind.Suggested);
        item.IsAutoFilled.Should().BeTrue();
        item.AutoFillNote.Should().Be(BusStopInputItem.SuggestedAutoFillNote);
        item.IsTouchedByUser.Should().BeFalse();
        item.ShowSuggestions.Should().BeFalse("自動で入れた欄の候補は開かない（職員が入力している欄ではない）");
        _viewModel.StatusMessage.Should().Contain("開いた時点で1件の欄に自動で入れました");
    }

    [Fact]
    public async Task 実績が1回だけなら既定値を入れないこと()
    {
        _stats = new List<BusStopUsageStatRow> { Stat("天神～博多", 200, sameStaff: true, count: 1) };

        await InitializeAsync(Bus(200, Day));

        _viewModel.BusUsages[0].BusStops.Should().BeEmpty();
        _viewModel.BusUsages[0].IsAutoFilled.Should().BeFalse();
        _viewModel.StatusMessage.Should().NotContain("自動で入れた");
    }

    [Fact]
    public async Task 保存済みのバス停名は既定値で上書きしないこと()
    {
        _stats = new List<BusStopUsageStatRow> { Stat("天神～博多", 200, sameStaff: true, count: 5) };

        await InitializeAsync(Bus(200, Day, busStops: "薬院～大橋"));

        _viewModel.BusUsages[0].BusStops.Should().Be("薬院～大橋");
        _viewModel.BusUsages[0].IsAutoFilled.Should().BeFalse();
    }

    [Fact]
    public async Task 既定値を書き換えたら注記が消え職員の入力になること()
    {
        _stats = new List<BusStopUsageStatRow> { Stat("天神～博多", 200, sameStaff: true, count: 2) };
        await InitializeAsync(Bus(200, Day));
        var item = _viewModel.BusUsages[0];

        item.BusStops = "天神～博多駅";

        item.IsAutoFilled.Should().BeFalse();
        item.AutoFillNote.Should().BeEmpty();
        item.IsTouchedByUser.Should().BeTrue();
    }

    [Fact]
    public async Task 既定値と同じ候補を選び直したら確認済みとして注記が消えること()
    {
        _stats = new List<BusStopUsageStatRow> { Stat("天神～博多", 200, sameStaff: true, count: 2) };
        await InitializeAsync(Bus(200, Day));
        var item = _viewModel.BusUsages[0];

        item.SelectSuggestion("天神～博多");

        item.BusStops.Should().Be("天神～博多");
        item.IsAutoFilled.Should().BeFalse();
        item.IsTouchedByUser.Should().BeTrue();
    }

    [Fact]
    public async Task 既定値は保存すると明細に書き込まれること()
    {
        _stats = new List<BusStopUsageStatRow> { Stat("天神～博多", 200, sameStaff: true, count: 2) };
        IEnumerable<(int, string)>? written = null;
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<(int, string)>>(), It.IsAny<SQLiteTransaction>()))
            .Callback<int, IEnumerable<(int, string)>, SQLiteTransaction>((_, u, _) => written = u.ToList())
            .ReturnsAsync(true);
        _ledgerRepoMock.Setup(r => r.UpdateSummaryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);

        await InitializeAsync(Bus(200, Day));
        await _viewModel.SaveAsync();

        _viewModel.IsSaved.Should().BeTrue();
        written.Should().Equal((1, "天神～博多"));
    }

    [Fact]
    public async Task 保存に失敗したら明細は自動入力の値ではなく開いた時点の値へ戻ること()
    {
        // 自動入力は明細（LedgerDetail.BusStops）にも書き込まれる。退避（#2103）より先に入れると、
        // 失敗時の復元が自動入力の値を「DB と同じ値」として戻してしまう
        _stats = new List<BusStopUsageStatRow> { Stat("天神～博多", 200, sameStaff: true, count: 2) };
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<(int, string)>>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(false);
        var detail = Bus(200, Day, busStops: SummaryGenerator.BusPlaceholder);

        await InitializeAsync(detail);
        detail.BusStops.Should().Be("天神～博多", "前提: 自動入力は明細にも書き込まれる");
        await _viewModel.SaveAsync();

        detail.BusStops.Should().Be(SummaryGenerator.BusPlaceholder);
        _viewModel.BusUsages[0].BusStops.Should().Be("天神～博多", "入力欄は戻さない（そのまま保存し直せる）");
    }

    #endregion

    #region 往復の復路の自動補完

    [Fact]
    public async Task 上の行を入力すると同じ金額同じ日の次の行へ乗降を入れ替えた値が入ること()
    {
        await InitializeAsync(Bus(200, Day), Bus(200, Day, sequence: 2));

        _viewModel.BusUsages[0].BusStops = "天神～博多";

        var next = _viewModel.BusUsages[1];
        next.BusStops.Should().Be("博多～天神");
        next.AutoFillKind.Should().Be(BusStopAutoFillKind.RoundTrip);
        next.AutoFillNote.Should().Be(BusStopInputItem.RoundTripAutoFillNote);
        next.IsTouchedByUser.Should().BeFalse();
    }

    [Theory]
    [InlineData(150, 0)]   // 金額が違う
    [InlineData(200, 1)]   // 利用日が違う
    public async Task 金額か利用日が違う次の行には復路を入れないこと(int nextAmount, int dayOffset)
    {
        await InitializeAsync(Bus(200, Day), Bus(nextAmount, Day.AddDays(dayOffset), sequence: 2));

        _viewModel.BusUsages[0].BusStops = "天神～博多";

        _viewModel.BusUsages[1].BusStops.Should().BeEmpty();
        _viewModel.BusUsages[1].IsAutoFilled.Should().BeFalse();
    }

    [Fact]
    public async Task 職員が入力した次の行は上の行が変わっても上書きしないこと()
    {
        await InitializeAsync(Bus(200, Day), Bus(200, Day, sequence: 2));
        _viewModel.BusUsages[1].BusStops = "薬院～大橋";

        _viewModel.BusUsages[0].BusStops = "天神～博多";

        _viewModel.BusUsages[1].BusStops.Should().Be("薬院～大橋");
    }

    [Fact]
    public async Task 職員が一度触って空に戻した次の行にも復路を入れないこと()
    {
        await InitializeAsync(Bus(200, Day), Bus(200, Day, sequence: 2));
        _viewModel.BusUsages[1].BusStops = "博";
        _viewModel.BusUsages[1].BusStops = string.Empty;

        _viewModel.BusUsages[0].BusStops = "天神～博多";

        _viewModel.BusUsages[1].BusStops.Should().BeEmpty();
    }

    [Fact]
    public async Task 上の行を直すと自動で入れた復路も入れ直され空にすると取り除かれること()
    {
        await InitializeAsync(Bus(200, Day), Bus(200, Day, sequence: 2));
        var first = _viewModel.BusUsages[0];
        var next = _viewModel.BusUsages[1];

        first.BusStops = "天神～博多";
        first.BusStops = "天神～博多駅";
        next.BusStops.Should().Be("博多駅～天神");

        first.BusStops = string.Empty;
        next.BusStops.Should().BeEmpty();
        next.IsAutoFilled.Should().BeFalse();
        next.IsTouchedByUser.Should().BeFalse("取り除いた後も、上の行を入れ直せばまた補完できる");

        first.BusStops = "薬院～大橋";
        next.BusStops.Should().Be("大橋～薬院");
    }

    [Fact]
    public async Task 往復の続きが3行あると交互に補完されること()
    {
        await InitializeAsync(Bus(200, Day), Bus(200, Day, sequence: 2), Bus(200, Day, sequence: 3));

        _viewModel.BusUsages[0].BusStops = "天神～博多";

        _viewModel.BusUsages.Select(b => b.BusStops).Should().Equal("天神～博多", "博多～天神", "天神～博多");
    }

    [Fact]
    public async Task 開いた時点で上の行に保存済みの値があれば次の未入力の行へ復路を入れること()
    {
        await InitializeAsync(
            Bus(200, Day, busStops: "天神～博多"),
            Bus(200, Day, busStops: SummaryGenerator.BusPlaceholder, sequence: 2));

        _viewModel.BusUsages[1].BusStops.Should().Be("博多～天神");
        _viewModel.BusUsages[1].AutoFillKind.Should().Be(BusStopAutoFillKind.RoundTrip);
    }

    [Fact]
    public async Task 既定値は往復の先頭の行だけに入り続きの行は復路になること()
    {
        // 続きの行にも同じ既定値（A～B）を入れると往復の復路として入れ直せなくなる
        _stats = new List<BusStopUsageStatRow> { Stat("天神～博多", 200, sameStaff: true, count: 4) };

        await InitializeAsync(Bus(200, Day), Bus(200, Day, sequence: 2));

        _viewModel.BusUsages[0].BusStops.Should().Be("天神～博多");
        _viewModel.BusUsages[0].AutoFillKind.Should().Be(BusStopAutoFillKind.Suggested);
        _viewModel.BusUsages[1].BusStops.Should().Be("博多～天神");
        _viewModel.BusUsages[1].AutoFillKind.Should().Be(BusStopAutoFillKind.RoundTrip);
        _viewModel.StatusMessage.Should().Contain("開いた時点で2件の欄に自動で入れました");
    }

    [Fact]
    public async Task 往復ボタンで自動の復路と同じ値を入れ直すと確認済みになること()
    {
        await InitializeAsync(Bus(200, Day), Bus(200, Day, sequence: 2));
        _viewModel.BusUsages[0].BusStops = "天神～博多";
        var next = _viewModel.BusUsages[1];

        next.ApplyRoundTripCommand.Execute(null);

        next.BusStops.Should().Be("博多～天神");
        next.IsAutoFilled.Should().BeFalse();
        next.IsTouchedByUser.Should().BeTrue();

        // 確認済みになった欄は、上の行が変わっても入れ直さない
        _viewModel.BusUsages[0].BusStops = "薬院～大橋";
        next.BusStops.Should().Be("博多～天神");
    }

    [Fact]
    public async Task 自動で入れた欄にフォーカスするとその行の並びの先頭を出すこと()
    {
        // 自動の値で絞ると、その値だけなら候補が開かず、別の区間を選ぶには値を消すしかない
        _overall = new List<string> { "全体～Z" };
        _stats = new List<BusStopUsageStatRow>
        {
            Stat("天神～博多", 200, sameStaff: true, count: 3),
            Stat("薬院～大橋", 200, sameStaff: true, count: 1),
        };
        await InitializeAsync(Bus(200, Day));
        var item = _viewModel.BusUsages[0];
        item.BusStops.Should().Be("天神～博多", "前提: 既定値が入っている");

        item.OnTextBoxGotFocus();

        item.ShowSuggestions.Should().BeTrue();
        item.FilteredSuggestions.Should().Equal("天神～博多", "薬院～大橋", "全体～Z");

        // ↓キーで開き直しても同じ
        item.HideSuggestions();
        item.HandleSuggestionKey(System.Windows.Input.Key.Down).Should().BeTrue();
        item.FilteredSuggestions.Should().Equal("天神～博多", "薬院～大橋", "全体～Z");
    }

    [Fact]
    public async Task 自動の値を書き換えた欄はフォーカス時に入力値で絞ること()
    {
        // 対の表明: 自動の欄を書き換えた後も空として扱い続ける実装を落とす
        _overall = new List<string> { "全体～Z" };
        _stats = new List<BusStopUsageStatRow>
        {
            Stat("天神～博多", 200, sameStaff: true, count: 3),
            Stat("薬院～大橋", 200, sameStaff: true, count: 1),
        };
        await InitializeAsync(Bus(200, Day));
        var item = _viewModel.BusUsages[0];
        item.IsAutoFilled.Should().BeTrue("前提: 既定値から始める");
        item.BusStops = "薬院";
        item.HideSuggestions();

        item.OnTextBoxGotFocus();

        item.FilteredSuggestions.Should().Equal("薬院～大橋");
    }

    [Fact]
    public async Task 自動で入れた値は保存前の類似の確認に載せないこと()
    {
        // 過去に「天神～博多駅」と「天神～博多駅前」の両方がある職員は、自動入力のたびに類似の確認が出てしまう
        _overall = new List<string> { "天神～博多駅", "天神～博多駅前" };
        _stats = new List<BusStopUsageStatRow> { Stat("天神～博多駅", 200, sameStaff: true, count: 2) };
        await InitializeAsync(Bus(200, Day));
        _viewModel.BusUsages[0].IsAutoFilled.Should().BeTrue("前提: 既定値が入っている");

        _viewModel.CollectSaveWarnings().Should().BeEmpty();

        // 対の表明: 職員が同じ値を入力し直したら従来どおり類似を確認する
        _viewModel.BusUsages[0].BusStops = string.Empty;
        _viewModel.BusUsages[0].BusStops = "天神～博多駅";
        _viewModel.CollectSaveWarnings().Should().ContainSingle()
            .Which.Should().Be("「天神～博多駅」は既存の「天神～博多駅前」と類似しています");
    }

    #endregion

    #region スキップ

    [Fact]
    public async Task 自動で入れた値だけならスキップで破棄の確認を出さないこと()
    {
        _stats = new List<BusStopUsageStatRow> { Stat("天神～博多", 200, sameStaff: true, count: 2) };
        await InitializeAsync(Bus(200, Day));

        _viewModel.HasInputDiscardedBySkip().Should().BeFalse();

        // 対の表明: 職員が入力した値は従来どおり確認の対象
        _viewModel.BusUsages[0].BusStops = "天神～博多駅";
        _viewModel.HasInputDiscardedBySkip().Should().BeTrue();
    }

    [Fact]
    public async Task スキップに失敗したら自動入力の状態も元へ戻ること()
    {
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<(int, string)>>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(false);
        await InitializeAsync(Bus(200, Day), Bus(200, Day, sequence: 2));
        _viewModel.BusUsages[0].BusStops = "天神～博多";

        await _viewModel.SkipAsync();

        _viewModel.IsSaved.Should().BeFalse();
        var next = _viewModel.BusUsages[1];
        next.BusStops.Should().Be("博多～天神");
        next.AutoFillKind.Should().Be(BusStopAutoFillKind.RoundTrip);
        next.IsTouchedByUser.Should().BeFalse();

        // 戻した後も往復の自動補完は働き続ける
        _viewModel.BusUsages[0].BusStops = "薬院～大橋";
        next.BusStops.Should().Be("大橋～薬院");
    }

    #endregion
}
