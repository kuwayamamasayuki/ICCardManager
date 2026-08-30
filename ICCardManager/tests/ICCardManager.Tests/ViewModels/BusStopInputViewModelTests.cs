using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// BusStopInputViewModelの単体テスト
/// </summary>
public class BusStopInputViewModelTests : IDisposable
{
    private readonly Mock<ILedgerRepository> _ledgerRepoMock;
    private readonly Mock<ISettingsRepository> _settingsRepoMock;
    private readonly Mock<IDialogService> _dialogServiceMock;

    /// <summary>
    /// Issue #1945: 明細と摘要を 1 つのトランザクションで書くため、実体の DbContext が要る
    /// （<c>BeginTransactionAsync</c> が本物の tx を返す状態でテストする。
    /// `LedgerRowEditViewModelTests` が Issue #1458 で採ったのと同じ形）。
    /// </summary>
    private readonly DbContext _dbContext;
    private readonly BusStopInputViewModel _viewModel;

    public BusStopInputViewModelTests()
    {
        _ledgerRepoMock = new Mock<ILedgerRepository>();
        _settingsRepoMock = new Mock<ISettingsRepository>();
        _dialogServiceMock = new Mock<IDialogService>();

        // バス停サジェストのデフォルト: 空
        _ledgerRepoMock.Setup(r => r.GetBusStopSuggestionsAsync(It.IsAny<string>()))
            .ReturnsAsync(Enumerable.Empty<(string BusStops, int UsageCount, DateTime? LastUsedDate)>());

        // Issue #1811: 保存前の確認ダイアログは既定で「はい」（警告を承知で保存する）。
        // 「いいえ」の挙動は各テストで個別に上書きする
        _dialogServiceMock.Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        _dbContext = TestDbContextFactory.Create();

        _viewModel = new BusStopInputViewModel(
            _ledgerRepoMock.Object,
            _settingsRepoMock.Object,
            _dialogServiceMock.Object,
            _dbContext);
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        GC.SuppressFinalize(this);
    }

    #region InitializeWithDetails（同期版）

    [Fact]
    public void InitializeWithDetails_バス利用のみが抽出されること()
    {
        // Arrange
        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, UseDate = DateTime.Today, Amount = 200 },
            new LedgerDetail { IsBus = false, EntryStation = "博多", ExitStation = "天神", Amount = 210 },
            new LedgerDetail { IsBus = true, UseDate = DateTime.Today, Amount = 150 },
        };

        // Act
        _viewModel.InitializeWithDetails(ledger, details);

        // Assert
        _viewModel.BusUsages.Should().HaveCount(2);
        _viewModel.StatusMessage.Should().Contain("2件");
    }

    [Fact]
    public void InitializeWithDetails_バス利用がない場合のメッセージ()
    {
        // Arrange
        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = false, EntryStation = "博多", ExitStation = "天神" },
        };

        // Act
        _viewModel.InitializeWithDetails(ledger, details);

        // Assert
        _viewModel.BusUsages.Should().BeEmpty();
        _viewModel.StatusMessage.Should().Be("バス利用の履歴がありません");
    }

    [Fact]
    public void InitializeWithDetails_HasUnsavedChangesがfalseになること()
    {
        // Arrange
        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, UseDate = DateTime.Today },
        };

        // Act
        _viewModel.InitializeWithDetails(ledger, details);

        // Assert
        _viewModel.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void InitializeWithDetails_既存のバス停名が保持されること()
    {
        // Arrange
        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, BusStops = "天神バス停～博多駅前", Amount = 200 },
        };

        // Act
        _viewModel.InitializeWithDetails(ledger, details);

        // Assert
        _viewModel.BusUsages[0].BusStops.Should().Be("天神バス停～博多駅前");
    }

    [Fact]
    public void InitializeWithDetails_バス停名が未入力の場合は空文字になること()
    {
        // Arrange
        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, BusStops = null, Amount = 200 },
        };

        // Act
        _viewModel.InitializeWithDetails(ledger, details);

        // Assert
        _viewModel.BusUsages[0].BusStops.Should().BeEmpty();
    }

    #endregion

    #region InitializeWithDetailsAsync（非同期版）

    [Fact]
    public async Task InitializeWithDetailsAsync_サジェスト候補が読み込まれること()
    {
        // Arrange
        var suggestions = new List<(string BusStops, int UsageCount, DateTime? LastUsedDate)>
        {
            ("天神バス停～博多駅前", 5, DateTime.Today),
            ("薬院駅前～大橋駅前", 3, DateTime.Today.AddDays(-10)),
        };
        _ledgerRepoMock.Setup(r => r.GetBusStopSuggestionsAsync(It.IsAny<string>()))
            .ReturnsAsync(suggestions);

        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, UseDate = DateTime.Today, Amount = 200 },
        };

        // Act
        await _viewModel.InitializeWithDetailsAsync(ledger, details);

        // Assert
        _viewModel.BusStopSuggestions.Should().HaveCount(2);
        _viewModel.BusStopSuggestions.Should().Contain("天神バス停～博多駅前");
    }

    [Fact]
    public async Task InitializeWithDetailsAsync_サジェスト件数がステータスに表示されること()
    {
        // Arrange
        var suggestions = new List<(string BusStops, int UsageCount, DateTime? LastUsedDate)>
        {
            ("天神バス停～博多駅前", 5, DateTime.Today),
            ("薬院駅前～大橋駅前", 3, DateTime.Today.AddDays(-10)),
        };
        _ledgerRepoMock.Setup(r => r.GetBusStopSuggestionsAsync(It.IsAny<string>()))
            .ReturnsAsync(suggestions);

        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, UseDate = DateTime.Today, Amount = 200 },
        };

        // Act
        await _viewModel.InitializeWithDetailsAsync(ledger, details);

        // Assert
        _viewModel.StatusMessage.Should().Contain("2件の候補あり");
    }

    [Fact]
    public async Task InitializeWithDetailsAsync_サジェスト取得失敗時に空リストになること()
    {
        // Arrange
        _ledgerRepoMock.Setup(r => r.GetBusStopSuggestionsAsync(It.IsAny<string>()))
            .ThrowsAsync(new Exception("DB error"));

        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, UseDate = DateTime.Today, Amount = 200 },
        };

        // Act
        await _viewModel.InitializeWithDetailsAsync(ledger, details);

        // Assert: 例外をスローせず、空リストになる
        _viewModel.BusStopSuggestions.Should().BeEmpty();
        _viewModel.BusUsages.Should().HaveCount(1);
    }

    #endregion

    #region SaveAsync

    [Fact]
    public async Task SaveAsync_Ledgerがnullの場合は何もしないこと()
    {
        // Act（Ledgerを設定せずに保存）
        await _viewModel.SaveAsync();

        // Assert: リポジトリは呼ばれない
        _ledgerRepoMock.Verify(
            r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Never);
    }

    [Fact]
    public async Task SaveAsync_未入力のバス停に星マークが付くこと()
    {
        // Arrange
        var detail1 = new LedgerDetail { IsBus = true, BusStops = null, Amount = 200, SequenceNumber = 1 };
        var detail2 = new LedgerDetail { IsBus = true, BusStops = "天神バス停", Amount = 150, SequenceNumber = 2 };
        var ledger = new Ledger
        {
            Id = 1,
            Details = new List<LedgerDetail> { detail1, detail2 }
        };

        _settingsRepoMock.Setup(s => s.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings());

        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<List<(int, string)>>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);

        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);

        _viewModel.InitializeWithDetails(ledger, ledger.Details);

        // Act
        await _viewModel.SaveAsync();

        // Assert: 未入力のバス停は★マーク
        detail1.BusStops.Should().Be("★");
        detail2.BusStops.Should().Be("天神バス停");
    }

    #region Issue #1945: バス停名更新の競合

    /// <summary>
    /// Issue #1945（欠陥を突く側）: バス停名の更新が競合（影響行数 0）したときは、
    /// 摘要（ledger.summary）を書き換えないこと。
    /// 旧実装は戻り値を捨てて必ず UpdateAsync まで進んでいたため、
    /// 「摘要はバス停名入り・明細は★のまま」という自己矛盾した台帳が 6 年間残った。
    /// </summary>
    [Fact]
    public async Task SaveAsync_バス停名更新が競合したら摘要を更新しないこと_Issue1945()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { LedgerId = 1, IsBus = true, BusStops = "★", Amount = 200, SequenceNumber = 1 }
        };
        var ledger = new Ledger { Id = 1, Summary = "バス（★）", Details = details };

        _settingsRepoMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<(int, string)>>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(false); // 競合
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        _viewModel.InitializeWithDetails(ledger, details);
        _viewModel.BusUsages[0].BusStops = "天神～博多";

        // Act
        await _viewModel.SaveAsync();

        // Assert: 摘要の UPDATE へ到達しない／保存済みにしない／原因を名指しした案内を出す
        _ledgerRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _viewModel.IsSaved.Should().BeFalse();
        _viewModel.StatusMessage.Should().Be(BusStopInputViewModel.BusStopConflictMessage);
    }

    /// <summary>
    /// Issue #1945（対の表明）: 競合していないときは従来どおり摘要まで更新すること。
    /// この表明が無いと「常に摘要を更新しない」実装でも上のテストは緑になる。
    /// </summary>
    [Fact]
    public async Task SaveAsync_バス停名更新が成功したら摘要も更新すること_Issue1945()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { LedgerId = 1, IsBus = true, BusStops = "★", Amount = 200, SequenceNumber = 1 }
        };
        var ledger = new Ledger { Id = 1, Summary = "バス（★）", Details = details };

        _settingsRepoMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<(int, string)>>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        _viewModel.InitializeWithDetails(ledger, details);
        _viewModel.BusUsages[0].BusStops = "天神～博多";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _ledgerRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Once);
        _viewModel.IsSaved.Should().BeTrue();
    }

    /// <summary>
    /// Issue #1945: スキップ（★のまま保存）経路も同じ判定を通ること。
    /// 保存経路が 2 つあるので、片方だけ直す形を残さない。
    /// </summary>
    [Fact]
    public async Task SkipAsync_バス停名更新が競合したら摘要を更新しないこと_Issue1945()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { LedgerId = 1, IsBus = true, BusStops = "天神～博多", Amount = 200, SequenceNumber = 1 }
        };
        var ledger = new Ledger { Id = 1, Summary = "バス（天神～博多）", Details = details };

        _settingsRepoMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<(int, string)>>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(false);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        _viewModel.InitializeWithDetails(ledger, details);

        // Act
        await _viewModel.SkipAsync();

        // Assert
        _ledgerRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _viewModel.IsSaved.Should().BeFalse();
        _viewModel.StatusMessage.Should().Be(BusStopInputViewModel.BusStopConflictMessage);
    }

    /// <summary>
    /// Issue #1945 / #1806（対の表明）: バス停名と摘要は同じ事実を 2 か所に持つため、
    /// **同一トランザクション**で書くこと。分けると、明細だけが確定して摘要が「バス（★）」のまま残る
    /// 鏡像の不整合（本 Issue が消した自己矛盾の裏返し）を作る。
    /// </summary>
    [Fact]
    public async Task SaveAsync_明細と摘要は同一トランザクションで書かれること_Issue1945()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { LedgerId = 1, IsBus = true, BusStops = "★", Amount = 200, SequenceNumber = 1 }
        };
        var ledger = new Ledger { Id = 1, Summary = "バス（★）", Details = details };

        SQLiteTransaction detailTransaction = null;
        SQLiteTransaction summaryTransaction = null;

        _settingsRepoMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<(int, string)>>(), It.IsAny<SQLiteTransaction>()))
            .Callback<int, IEnumerable<(int, string)>, SQLiteTransaction>((_, __, tx) => detailTransaction = tx)
            .ReturnsAsync(true);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
            .Callback<Ledger, SQLiteTransaction>((_, tx) => summaryTransaction = tx)
            .ReturnsAsync(true);

        _viewModel.InitializeWithDetails(ledger, details);
        _viewModel.BusUsages[0].BusStops = "天神～博多";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        detailTransaction.Should().NotBeNull();
        summaryTransaction.Should().BeSameAs(detailTransaction, "明細と摘要は 1 つの論理操作（Issue #1806）");
        _ledgerRepoMock.Verify(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<(int, string)>>()),
            Times.Never, "tx なしオーバーロードは使わない");
        _ledgerRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Ledger>()), Times.Never);
    }

    /// <summary>
    /// Issue #1945: 摘要の更新が競合（影響行数 0）したときも保存失敗として扱うこと。
    /// 明細だけが確定して摘要が古いまま残る鏡像の不整合を、トランザクションが巻き戻す。
    /// </summary>
    [Fact]
    public async Task SaveAsync_摘要の更新が競合したら保存失敗になること_Issue1945()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { LedgerId = 1, IsBus = true, BusStops = "★", Amount = 200, SequenceNumber = 1 }
        };
        var ledger = new Ledger { Id = 1, Summary = "バス（★）", Details = details };

        _settingsRepoMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<(int, string)>>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(false); // 台帳が他 PC で削除された等

        _viewModel.InitializeWithDetails(ledger, details);
        _viewModel.BusUsages[0].BusStops = "天神～博多";

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.IsSaved.Should().BeFalse();
        _viewModel.StatusMessage.Should().Be("保存に失敗しました");
    }

    #endregion

    [Fact]
    public async Task SaveAsync_成功時にIsSavedがtrueになること()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, BusStops = "テスト", Amount = 200, SequenceNumber = 1 }
        };
        var ledger = new Ledger { Id = 1, Details = details };

        _settingsRepoMock.Setup(s => s.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings());
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<List<(int, string)>>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);

        _viewModel.InitializeWithDetails(ledger, details);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.IsSaved.Should().BeTrue();
        _viewModel.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_失敗時にIsSavedがfalseのままであること()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, BusStops = "テスト", Amount = 200, SequenceNumber = 1 }
        };
        var ledger = new Ledger { Id = 1, Details = details };

        _settingsRepoMock.Setup(s => s.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings());
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<List<(int, string)>>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(false);

        _viewModel.InitializeWithDetails(ledger, details);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.IsSaved.Should().BeFalse();
        _viewModel.StatusMessage.Should().Be("保存に失敗しました");
    }

    #endregion

    #region SaveAsync の保存前確認（Issue #1811）

    /// <summary>
    /// 保存系リポジトリのモックを成功で設定し、指定のバス停名で初期化する。
    /// </summary>
    private Ledger ArrangeSaveWithBusStops(params string[] busStops)
    {
        var details = busStops
            .Select((stops, i) => new LedgerDetail
            {
                IsBus = true, BusStops = stops, Amount = 200, SequenceNumber = i + 1
            })
            .ToList();
        var ledger = new Ledger { Id = 1, Details = details };

        _settingsRepoMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<List<(int, string)>>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        _viewModel.InitializeWithDetails(ledger, details);
        return ledger;
    }

    /// <summary>
    /// Issue #1811: 未入力・形式・類似の 3 種類の警告は、保存前に 1 つの確認ダイアログへ
    /// すべて載せて提示すること（修正前は <c>StatusMessage</c> へ順に代入して後の警告が前を上書きし、
    /// 直後の「保存しました」と <c>IsSaved</c> による Close で読める時間が無かった）。
    /// 確認ダイアログは処理中スコープの外（<c>IsBusy == false</c>）で出すこと（Issue #1793）。
    /// </summary>
    [Fact]
    public async Task SaveAsync_警告があるときは保存前の確認ダイアログに全警告が載ること()
    {
        // Arrange: 未入力 1 件・「～」なし 1 件（「天神」は既存「天神南～博多」とも類似）
        _viewModel.BusStopSuggestions = new List<string> { "天神南～博多" };
        ArrangeSaveWithBusStops("", "天神");
        string? shownMessage = null;
        bool? busyAtDialog = null;
        _dialogServiceMock.Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((m, _) => { shownMessage = m; busyAtDialog = _viewModel.IsBusy; })
            .Returns(true);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        shownMessage.Should().NotBeNull("警告があるときは保存前に確認ダイアログを出す");
        shownMessage.Should().Contain("未入力のバス停が1件");
        shownMessage.Should().Contain("「○○～△△」の形式");
        shownMessage.Should().Contain("「天神」は既存の「天神南～博多」と類似");
        shownMessage.Should().Contain("保存しますか");
        busyAtDialog.Should().BeFalse("確認ダイアログは処理中オーバーレイの外で出す（Issue #1793）");
        _viewModel.IsSaved.Should().BeTrue("「はい」なら保存する");
    }

    /// <summary>
    /// Issue #1811: 確認ダイアログで「いいえ」を選ぶと何も保存せず入力画面に留まり、
    /// 修正の手掛かりとして警告の全文がステータス欄に残ること。
    /// </summary>
    [Fact]
    public async Task SaveAsync_確認ダイアログでいいえを選ぶと保存せず警告がステータス欄に残ること()
    {
        // Arrange
        _viewModel.BusStopSuggestions = new List<string> { "天神南～博多" };
        var ledger = ArrangeSaveWithBusStops("", "天神");
        _dialogServiceMock.Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        // Act
        await _viewModel.SaveAsync();

        // Assert: 保存されない（ダイアログは閉じない）
        _viewModel.IsSaved.Should().BeFalse();
        _ledgerRepoMock.Verify(r => r.UpdateDetailBusStopsAsync(It.IsAny<int>(), It.IsAny<List<(int, string)>>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _ledgerRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        ledger.Details[0].BusStops.Should().Be("", "「いいえ」では★への変換も行わない");

        // Assert: 3 つの警告がすべてステータス欄に残る（上書きされない）
        _viewModel.StatusMessage.Should().Contain("未入力のバス停が1件");
        _viewModel.StatusMessage.Should().Contain("「○○～△△」の形式");
        _viewModel.StatusMessage.Should().Contain("「天神」は既存の「天神南～博多」と類似");
    }

    /// <summary>
    /// Issue #1811: 警告が 1 つも無いときは確認ダイアログを挟まず、従来どおり即座に保存すること
    /// （過剰な確認で返却フローを遅くしない）。
    /// </summary>
    [Fact]
    public async Task SaveAsync_警告がないときは確認ダイアログを出さずに保存すること()
    {
        // Arrange: すべて入力済み・形式どおり・類似なし
        _viewModel.BusStopSuggestions = new List<string> { "薬院～大橋" };
        ArrangeSaveWithBusStops("天神～博多", "博多～天神");

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _dialogServiceMock.Verify(
            d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _viewModel.IsSaved.Should().BeTrue();
        _viewModel.StatusMessage.Should().Be("保存しました");
    }

    /// <summary>
    /// Issue #1914: 摘要は「ラベル＋全角括弧」の区切り書式のため、対応の取れない
    /// 全角括弧を含むバス停名は摘要から読み取れなくなる。入力し直せるうちに知らせること。
    /// </summary>
    [Fact]
    public void CollectSaveWarnings_全角括弧の対応が取れていない入力を警告すること()
    {
        // Arrange: 「天神）西口～博多」は閉じ括弧だけが余る
        ArrangeSaveWithBusStops("天神）西口～博多", "赤坂～大濠公園");

        // Act
        var warnings = _viewModel.CollectSaveWarnings();

        // Assert: 件数と復旧手段（半角への置き換え）を示すこと
        warnings.Should().ContainSingle(w => w.Contains("全角括弧") && w.Contains("1件"));
        warnings.Should().ContainSingle(w => w.Contains("半角"));
    }

    /// <summary>
    /// Issue #1914: 対応の取れている括弧（「天神（西口）」等）は正当な入力であり、
    /// 摘要からも読み取れる。警告で塞がないこと（対の表明）。
    /// </summary>
    [Fact]
    public void CollectSaveWarnings_対応の取れた括弧は警告しないこと()
    {
        // Arrange
        ArrangeSaveWithBusStops("天神（西口）～博多", "赤坂(東口)～大濠公園");

        // Act
        var warnings = _viewModel.CollectSaveWarnings();

        // Assert
        warnings.Should().NotContain(w => w.Contains("全角括弧"));
    }

    /// <summary>
    /// Issue #1811（コードレビュー指摘）: 同じバス停名を複数行に入力した場合（同一路線を1日に2回利用する等）、
    /// 行ごとに同じ警告文言が生成される。重複したまま列挙すると確認ダイアログに同じ行が並び、
    /// 上限件数と「ほか N 件」の残数も重複分で水増しされる。
    /// </summary>
    [Fact]
    public void CollectSaveWarnings_同一文言の類似警告は重複しないこと()
    {
        // Arrange: 同じ「天神」を2行に入力（既存「天神南～博多」と部分包含で類似）
        _viewModel.BusStopSuggestions = new List<string> { "天神南～博多" };
        ArrangeSaveWithBusStops("天神", "天神");

        // Act
        var warnings = _viewModel.CollectSaveWarnings();

        // Assert
        warnings.Should().ContainSingle(w => w.Contains("「天神」は既存の「天神南～博多」と類似"));
    }

    /// <summary>
    /// Issue #1811: 類似警告が多数になるとき（「天神」が天神を含む既存候補すべてに一致する等）、
    /// 確認ダイアログには上限件数まで列挙し、残りは件数で要約すること。
    /// </summary>
    [Fact]
    public async Task SaveAsync_類似警告が上限を超えると残りは件数で要約されること()
    {
        // Arrange: 「天神」を含む既存候補を上限 + 2 件用意する
        var limit = BusStopInputViewModel.MaxListedSimilarWarnings;
        _viewModel.BusStopSuggestions = Enumerable.Range(1, limit + 2)
            .Select(i => $"天神～候補{i}")
            .ToList();
        ArrangeSaveWithBusStops("天神");
        string? shownMessage = null;
        _dialogServiceMock.Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((m, _) => shownMessage = m)
            .Returns(true);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        shownMessage.Should().NotBeNull();
        for (var i = 1; i <= limit; i++)
        {
            shownMessage.Should().Contain($"「天神～候補{i}」と類似");
        }
        shownMessage.Should().NotContain($"「天神～候補{limit + 1}」と類似");
        shownMessage.Should().Contain("ほか2件");
    }

    #endregion

    #region SkipAsync

    [Fact]
    public async Task SkipAsync_未入力のバス停に星マークが付くこと()
    {
        // Arrange
        var detail1 = new LedgerDetail { IsBus = true, BusStops = null, Amount = 200, SequenceNumber = 1 };
        var ledger = new Ledger
        {
            Id = 1,
            Details = new List<LedgerDetail> { detail1 }
        };

        _settingsRepoMock.Setup(s => s.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings());
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<List<(int, string)>>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);

        _viewModel.InitializeWithDetails(ledger, ledger.Details);

        // Act
        await _viewModel.SkipAsync();

        // Assert
        detail1.BusStops.Should().Be("★");
        _viewModel.IsSaved.Should().BeTrue();
    }

    [Fact]
    public async Task SkipAsync_入力済みのバス停も星マークにリセットされること()
    {
        // Arrange: Issue #1156 — スキップ時は入力済みの内容も破棄する
        var detail1 = new LedgerDetail { IsBus = true, BusStops = null, Amount = 200, SequenceNumber = 1 };
        var detail2 = new LedgerDetail { IsBus = true, BusStops = "天神バス停～博多駅前", Amount = 150, SequenceNumber = 2 };
        var ledger = new Ledger
        {
            Id = 1,
            Details = new List<LedgerDetail> { detail1, detail2 }
        };

        _settingsRepoMock.Setup(s => s.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings());
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<List<(int, string)>>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);

        _viewModel.InitializeWithDetails(ledger, ledger.Details);

        // ユーザーがバス停名を入力した状態をシミュレート
        _viewModel.BusUsages[0].BusStops = "薬院大通～六本松三丁目";

        // Act: スキップを実行
        await _viewModel.SkipAsync();

        // Assert: 入力済みの内容も含め、すべて★にリセットされる
        detail1.BusStops.Should().Be("★");
        detail2.BusStops.Should().Be("★");
        _viewModel.BusUsages[0].BusStops.Should().Be("★");
        _viewModel.BusUsages[1].BusStops.Should().Be("★");
        _viewModel.IsSaved.Should().BeTrue();
    }

    [Fact]
    public async Task SkipAsync_Ledgerがnullの場合は何もしないこと()
    {
        // Act（Ledgerを設定せずにスキップ）
        await _viewModel.SkipAsync();

        // Assert: リポジトリは呼ばれない
        _ledgerRepoMock.Verify(
            r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Never);
    }

    #endregion

    #region InitializeWithLedgersAsync (Issue #1203)

    [Fact]
    public async Task InitializeWithLedgersAsync_複数Ledgerの全バス利用が1つのリストに集約されること()
    {
        // Arrange: 2日分の Ledger、各々にバス利用と非バス利用が混在
        var ledger1 = new Ledger
        {
            Id = 10,
            Details = new List<LedgerDetail>
            {
                new LedgerDetail { LedgerId = 10, IsBus = true,  SequenceNumber = 1, UseDate = new DateTime(2026, 4, 1), Amount = 210 },
                new LedgerDetail { LedgerId = 10, IsBus = false, SequenceNumber = 2, EntryStation = "博多", ExitStation = "天神", Amount = 210 },
            },
        };
        var ledger2 = new Ledger
        {
            Id = 11,
            Details = new List<LedgerDetail>
            {
                new LedgerDetail { LedgerId = 11, IsBus = true, SequenceNumber = 1, UseDate = new DateTime(2026, 4, 2), Amount = 150 },
                new LedgerDetail { LedgerId = 11, IsBus = true, SequenceNumber = 2, UseDate = new DateTime(2026, 4, 2), Amount = 180 },
            },
        };

        // Act
        await _viewModel.InitializeWithLedgersAsync(new[] { ledger1, ledger2 });

        // Assert: 合計3件のバス利用が1つの BusUsages に集約される
        _viewModel.BusUsages.Should().HaveCount(3);
        _viewModel.StatusMessage.Should().Contain("3件");
        // 先頭 Ledger が Ledger プロパティに設定される（UI 表示互換）
        _viewModel.Ledger.Should().Be(ledger1);
    }

    [Fact]
    public async Task InitializeWithLedgersAsync_DetailsがemptyでもリポジトリからロードしてUseDate金額が取得されること()
    {
        // Arrange: LendingService が返す in-memory Ledger は Details が空のことがあるため、
        // ID 経由で repository から再ロードされることを検証する（Issue #1203 回帰）
        var inMemoryLedger = new Ledger { Id = 99, Details = new List<LedgerDetail>() };

        var loadedLedger = new Ledger
        {
            Id = 99,
            Details = new List<LedgerDetail>
            {
                new LedgerDetail
                {
                    LedgerId = 99,
                    IsBus = true,
                    SequenceNumber = 1,
                    UseDate = new DateTime(2026, 4, 3),
                    Amount = 230,
                },
            },
        };

        _ledgerRepoMock.Setup(r => r.GetByIdAsync(99)).ReturnsAsync(loadedLedger);

        // Act
        await _viewModel.InitializeWithLedgersAsync(new[] { inMemoryLedger });

        // Assert: 再ロードされた Ledger の Details がバス停入力項目に反映される
        _viewModel.BusUsages.Should().HaveCount(1);
        _viewModel.BusUsages[0].UseDate.Should().Be(new DateTime(2026, 4, 3));
        _viewModel.BusUsages[0].Amount.Should().Be(230);
        _ledgerRepoMock.Verify(r => r.GetByIdAsync(99), Times.Once);
    }

    [Fact]
    public async Task InitializeWithLedgersAsync_バス利用がゼロ件の場合はメッセージが設定されること()
    {
        var ledger = new Ledger
        {
            Id = 20,
            Details = new List<LedgerDetail>
            {
                new LedgerDetail { LedgerId = 20, IsBus = false, EntryStation = "A", ExitStation = "B" },
            },
        };

        await _viewModel.InitializeWithLedgersAsync(new[] { ledger });

        _viewModel.BusUsages.Should().BeEmpty();
        _viewModel.StatusMessage.Should().Contain("バス利用の履歴がありません");
    }

    [Fact]
    public async Task SaveAsync_複数Ledgerの場合はLedgerごとに更新が呼ばれること()
    {
        // Arrange
        var ledger1 = new Ledger
        {
            Id = 10,
            Details = new List<LedgerDetail>
            {
                new LedgerDetail { LedgerId = 10, IsBus = true, SequenceNumber = 1, Amount = 210 },
            },
        };
        var ledger2 = new Ledger
        {
            Id = 11,
            Details = new List<LedgerDetail>
            {
                new LedgerDetail { LedgerId = 11, IsBus = true, SequenceNumber = 1, Amount = 150 },
                new LedgerDetail { LedgerId = 11, IsBus = true, SequenceNumber = 2, Amount = 180 },
            },
        };

        _settingsRepoMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<(int, string)>>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        await _viewModel.InitializeWithLedgersAsync(new[] { ledger1, ledger2 });

        // ユーザー入力を反映
        _viewModel.BusUsages[0].BusStops = "A～B";
        _viewModel.BusUsages[1].BusStops = "C～D";
        _viewModel.BusUsages[2].BusStops = "E～F";

        // Act
        await _viewModel.SaveAsync();

        // Assert: 両 Ledger に対して UpdateDetailBusStopsAsync と UpdateAsync が1回ずつ呼ばれる
        _ledgerRepoMock.Verify(
            r => r.UpdateDetailBusStopsAsync(10, It.Is<IEnumerable<(int, string)>>(u => u.Count() == 1), It.IsAny<SQLiteTransaction>()),
            Times.Once);
        _ledgerRepoMock.Verify(
            r => r.UpdateDetailBusStopsAsync(11, It.Is<IEnumerable<(int, string)>>(u => u.Count() == 2), It.IsAny<SQLiteTransaction>()),
            Times.Once);
        _ledgerRepoMock.Verify(r => r.UpdateAsync(ledger1, It.IsAny<SQLiteTransaction>()), Times.Once);
        _ledgerRepoMock.Verify(r => r.UpdateAsync(ledger2, It.IsAny<SQLiteTransaction>()), Times.Once);
        _viewModel.IsSaved.Should().BeTrue();
    }

    [Fact]
    public async Task SkipAsync_複数Ledgerの場合は全ての明細が星マークになりLedgerごとに更新されること()
    {
        var ledger1 = new Ledger
        {
            Id = 30,
            Details = new List<LedgerDetail>
            {
                new LedgerDetail { LedgerId = 30, IsBus = true, SequenceNumber = 1, BusStops = "入力済" },
            },
        };
        var ledger2 = new Ledger
        {
            Id = 31,
            Details = new List<LedgerDetail>
            {
                new LedgerDetail { LedgerId = 31, IsBus = true, SequenceNumber = 1 },
            },
        };

        _settingsRepoMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _ledgerRepoMock.Setup(r => r.UpdateDetailBusStopsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<(int, string)>>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        await _viewModel.InitializeWithLedgersAsync(new[] { ledger1, ledger2 });

        await _viewModel.SkipAsync();

        ledger1.Details[0].BusStops.Should().Be("★");
        ledger2.Details[0].BusStops.Should().Be("★");
        _ledgerRepoMock.Verify(r => r.UpdateAsync(ledger1, It.IsAny<SQLiteTransaction>()), Times.Once);
        _ledgerRepoMock.Verify(r => r.UpdateAsync(ledger2, It.IsAny<SQLiteTransaction>()), Times.Once);
        _viewModel.IsSaved.Should().BeTrue();
    }

    #endregion

    #region ApplyRoundTrip（Issue #1570: 往復ボタン）

    [Fact]
    public void InitializeWithDetails_先頭アイテムはHasPreviousItemがfalseであること()
    {
        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, UseDate = DateTime.Today, Amount = 200 },
            new LedgerDetail { IsBus = true, UseDate = DateTime.Today, Amount = 200 },
        };

        _viewModel.InitializeWithDetails(ledger, details);

        _viewModel.BusUsages[0].HasPreviousItem.Should().BeFalse("先頭行は前の行を持たない");
        _viewModel.BusUsages[1].HasPreviousItem.Should().BeTrue("2行目以降は前の行を持つ");
    }

    [Fact]
    public void InitializeWithDetails_2行目以降のPreviousItemが直前のアイテムを指すこと()
    {
        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, UseDate = DateTime.Today, Amount = 200 },
            new LedgerDetail { IsBus = true, UseDate = DateTime.Today, Amount = 220 },
            new LedgerDetail { IsBus = true, UseDate = DateTime.Today, Amount = 240 },
        };

        _viewModel.InitializeWithDetails(ledger, details);

        _viewModel.BusUsages[0].PreviousItem.Should().BeNull();
        _viewModel.BusUsages[1].PreviousItem.Should().BeSameAs(_viewModel.BusUsages[0]);
        _viewModel.BusUsages[2].PreviousItem.Should().BeSameAs(_viewModel.BusUsages[1]);
    }

    [Fact]
    public void ApplyRoundTrip_前の行の起点と終点が入れ替えて入力されること()
    {
        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, BusStops = "天神バス停～博多駅前" },
            new LedgerDetail { IsBus = true, BusStops = null },
        };
        _viewModel.InitializeWithDetails(ledger, details);

        _viewModel.BusUsages[1].ApplyRoundTripCommand.Execute(null);

        _viewModel.BusUsages[1].BusStops.Should().Be("博多駅前～天神バス停");
    }

    [Fact]
    public void ApplyRoundTrip_前の行の前後空白がトリムされて入れ替わること()
    {
        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, BusStops = "  天神  ～  博多  " },
            new LedgerDetail { IsBus = true, BusStops = null },
        };
        _viewModel.InitializeWithDetails(ledger, details);

        _viewModel.BusUsages[1].ApplyRoundTripCommand.Execute(null);

        _viewModel.BusUsages[1].BusStops.Should().Be("博多～天神");
    }

    [Fact]
    public void ApplyRoundTrip_前の行が空欄の場合は変更されないこと()
    {
        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, BusStops = null },
            new LedgerDetail { IsBus = true, BusStops = "既存値" },
        };
        _viewModel.InitializeWithDetails(ledger, details);

        _viewModel.BusUsages[1].ApplyRoundTripCommand.Execute(null);

        _viewModel.BusUsages[1].BusStops.Should().Be("既存値", "前の行が空欄なら何もしない");
    }

    [Fact]
    public void ApplyRoundTrip_前の行が形式不正でチルダなしの場合は変更されないこと()
    {
        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, BusStops = "天神博多" },
            new LedgerDetail { IsBus = true, BusStops = "既存値" },
        };
        _viewModel.InitializeWithDetails(ledger, details);

        _viewModel.BusUsages[1].ApplyRoundTripCommand.Execute(null);

        _viewModel.BusUsages[1].BusStops.Should().Be("既存値", "「～」で分割できない値は反転対象外");
    }

    [Fact]
    public void ApplyRoundTrip_前の行が星マークのみの場合は変更されないこと()
    {
        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, BusStops = "★" },
            new LedgerDetail { IsBus = true, BusStops = "既存値" },
        };
        _viewModel.InitializeWithDetails(ledger, details);

        _viewModel.BusUsages[1].ApplyRoundTripCommand.Execute(null);

        _viewModel.BusUsages[1].BusStops.Should().Be("既存値");
    }

    [Fact]
    public void ApplyRoundTrip_前の行がチルダ複数の場合は変更されないこと()
    {
        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, BusStops = "天神～博多～小倉" },
            new LedgerDetail { IsBus = true, BusStops = "既存値" },
        };
        _viewModel.InitializeWithDetails(ledger, details);

        _viewModel.BusUsages[1].ApplyRoundTripCommand.Execute(null);

        _viewModel.BusUsages[1].BusStops.Should().Be("既存値");
    }

    [Fact]
    public void ApplyRoundTrip_先頭アイテムでは何もしないこと()
    {
        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, BusStops = "既存値" },
        };
        _viewModel.InitializeWithDetails(ledger, details);

        _viewModel.BusUsages[0].ApplyRoundTripCommand.Execute(null);

        _viewModel.BusUsages[0].BusStops.Should().Be("既存値", "先頭行はPreviousItemがnullなので何もしない");
    }

    [Fact]
    public void ApplyRoundTrip_既存値があっても上書きされること()
    {
        var ledger = new Ledger { Id = 1 };
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, BusStops = "天神～博多" },
            new LedgerDetail { IsBus = true, BusStops = "薬院～大橋" },
        };
        _viewModel.InitializeWithDetails(ledger, details);

        _viewModel.BusUsages[1].ApplyRoundTripCommand.Execute(null);

        _viewModel.BusUsages[1].BusStops.Should().Be("博多～天神");
    }

    #endregion
}

/// <summary>
/// BusStopInputItemの単体テスト（サジェストフィルタリング）
/// </summary>
public class BusStopInputItemTests
{
    private BusStopInputItem CreateItem(string busStops = "", List<string>? suggestions = null)
    {
        var detail = new LedgerDetail
        {
            IsBus = true,
            UseDate = DateTime.Today,
            Amount = 200,
            BusStops = busStops
        };
        var item = new BusStopInputItem(detail);
        if (suggestions != null)
        {
            item.SetSuggestions(suggestions);
        }
        return item;
    }

    [Fact]
    public void Constructor_DetailのBusStopsが初期値に設定されること()
    {
        // Arrange & Act
        var item = CreateItem("天神バス停～博多駅前");

        // Assert
        item.BusStops.Should().Be("天神バス停～博多駅前");
    }

    [Fact]
    public void Constructor_星マークのみのBusStopsは空文字として初期化されること()
    {
        // Issue #1205: ユーザーが★を削除する手間を省くため、★のみは空欄として扱う
        var detail = new LedgerDetail { IsBus = true, BusStops = "★" };

        var item = new BusStopInputItem(detail);

        item.BusStops.Should().BeEmpty();
        // Detail 側の永続値は変えない（保存時の空欄→★変換で元に戻る）
        detail.BusStops.Should().Be("★");
    }

    [Fact]
    public void Constructor_nullのBusStopsが空文字になること()
    {
        // Arrange
        var detail = new LedgerDetail { IsBus = true, BusStops = null };

        // Act
        var item = new BusStopInputItem(detail);

        // Assert
        item.BusStops.Should().BeEmpty();
    }

    [Fact]
    public void BusStops変更時_DetailのBusStopsも更新されること()
    {
        // Arrange
        var item = CreateItem();

        // Act
        item.BusStops = "新しいバス停";

        // Assert
        item.Detail.BusStops.Should().Be("新しいバス停");
    }

    [Fact]
    public void サジェスト_先頭一致が優先されること()
    {
        // Arrange
        var suggestions = new List<string>
        {
            "天神バス停～博多駅前",
            "博多駅前～天神バス停",
            "天神中央公園前",
            "大天神ビル前"
        };
        var item = CreateItem(suggestions: suggestions);

        // Act: 「天神」と入力
        item.BusStops = "天神";

        // Assert: 先頭一致（天神バス停、天神中央公園前）が先、部分一致（大天神ビル前）が後
        item.ShowSuggestions.Should().BeTrue();
        item.FilteredSuggestions.Should().HaveCountGreaterOrEqualTo(2);

        // 先頭一致が先に来る
        var first = item.FilteredSuggestions[0];
        first.Should().StartWith("天神");
    }

    [Fact]
    public void サジェスト_空入力の場合は非表示になること()
    {
        // Arrange
        var suggestions = new List<string> { "天神バス停", "博多駅前" };
        var item = CreateItem(suggestions: suggestions);

        // Act
        item.BusStops = "";

        // Assert
        item.ShowSuggestions.Should().BeFalse();
        item.FilteredSuggestions.Should().BeEmpty();
    }

    [Fact]
    public void サジェスト_完全一致の場合は非表示になること()
    {
        // Arrange
        var suggestions = new List<string> { "天神バス停" };
        var item = CreateItem(suggestions: suggestions);

        // Act
        item.BusStops = "天神バス停";

        // Assert: 完全一致 → ポップアップ非表示
        item.ShowSuggestions.Should().BeFalse();
    }

    [Fact]
    public void サジェスト_候補がない場合は非表示になること()
    {
        // Arrange: サジェストなし
        var item = CreateItem(suggestions: new List<string>());

        // Act
        item.BusStops = "テスト";

        // Assert
        item.ShowSuggestions.Should().BeFalse();
    }

    [Fact]
    public void サジェスト_最大8件までに制限されること()
    {
        // Arrange: 10個のサジェスト候補
        var suggestions = Enumerable.Range(1, 10)
            .Select(i => $"バス停{i}")
            .ToList();
        var item = CreateItem(suggestions: suggestions);

        // Act
        item.BusStops = "バス停";

        // Assert
        item.FilteredSuggestions.Count.Should().BeLessOrEqualTo(8);
    }

    [Fact]
    public void サジェスト_大文字小文字を区別しないこと()
    {
        // Arrange
        var suggestions = new List<string> { "ABC停留所" };
        var item = CreateItem(suggestions: suggestions);

        // Act
        item.BusStops = "abc";

        // Assert
        item.ShowSuggestions.Should().BeTrue();
        item.FilteredSuggestions.Should().Contain("ABC停留所");
    }

    [Fact]
    public void SelectSuggestion_選択した候補がBusStopsに設定されること()
    {
        // Arrange
        var suggestions = new List<string> { "天神バス停～博多駅前" };
        var item = CreateItem(suggestions: suggestions);
        item.BusStops = "天神";

        // Act
        item.SelectSuggestionCommand.Execute("天神バス停～博多駅前");

        // Assert
        item.BusStops.Should().Be("天神バス停～博多駅前");
        item.ShowSuggestions.Should().BeFalse();
    }

    [Fact]
    public void HideSuggestions_ポップアップが非表示になること()
    {
        // Arrange
        var suggestions = new List<string> { "天神バス停" };
        var item = CreateItem(suggestions: suggestions);
        item.BusStops = "天";
        item.ShowSuggestions.Should().BeTrue();

        // Act
        item.HideSuggestionsCommand.Execute(null);

        // Assert
        item.ShowSuggestions.Should().BeFalse();
    }

    [Fact]
    public void AmountDisplay_金額が正しくフォーマットされること()
    {
        // Arrange
        var detail = new LedgerDetail { IsBus = true, Amount = 1500 };
        var item = new BusStopInputItem(detail);

        // Assert
        item.AmountDisplay.Should().Be("1,500円");
    }

    [Fact]
    public void AmountDisplay_金額がnullの場合は空文字であること()
    {
        // Arrange
        var detail = new LedgerDetail { IsBus = true, Amount = null };
        var item = new BusStopInputItem(detail);

        // Assert
        item.AmountDisplay.Should().BeEmpty();
    }

    #region Issue #1133: 空入力時のサジェスト表示

    [Fact]
    public void UpdateFilteredSuggestions_空入力で直近利用候補が表示されること()
    {
        // Arrange
        var suggestions = new List<string> { "天神～博多", "薬院～大橋", "福岡空港～天神" };
        var item = CreateItem(suggestions: suggestions);

        // Act - 空文字列でフィルター
        item.UpdateFilteredSuggestions(string.Empty);

        // Assert
        item.ShowSuggestions.Should().BeTrue("空入力でも直近利用候補を表示する");
        item.FilteredSuggestions.Should().HaveCount(3);
    }

    [Fact]
    public void UpdateFilteredSuggestions_空入力で最大8件表示されること()
    {
        // Arrange
        var suggestions = Enumerable.Range(1, 15).Select(i => $"バス停{i}～バス停{i + 100}").ToList();
        var item = CreateItem(suggestions: suggestions);

        // Act
        item.UpdateFilteredSuggestions(string.Empty);

        // Assert
        item.FilteredSuggestions.Should().HaveCount(8, "空入力時は最大8件まで");
    }

    [Fact]
    public void OnTextBoxGotFocus_サジェストが表示されること()
    {
        // Arrange
        var suggestions = new List<string> { "天神～博多" };
        var item = CreateItem(suggestions: suggestions);

        // Act
        item.OnTextBoxGotFocus();

        // Assert
        item.ShowSuggestions.Should().BeTrue();
    }

    #endregion

    #region Issue #1133: 類似バス停名検出

    [Fact]
    public void IsSimilar_一方が他方を含む場合はtrue()
    {
        BusStopInputViewModel.IsSimilar("天神", "天神南").Should().BeTrue();
        BusStopInputViewModel.IsSimilar("博多駅前", "博多駅").Should().BeTrue();
    }

    [Fact]
    public void IsSimilar_乗降が逆の場合はtrue()
    {
        BusStopInputViewModel.IsSimilar("天神～博多", "博多～天神").Should().BeTrue();
    }

    [Fact]
    public void IsSimilar_無関係な名前はfalse()
    {
        BusStopInputViewModel.IsSimilar("天神～博多", "薬院～大橋").Should().BeFalse();
    }

    [Fact]
    public void IsSimilar_空文字やnullはfalse()
    {
        BusStopInputViewModel.IsSimilar("", "天神").Should().BeFalse();
        BusStopInputViewModel.IsSimilar(null, "天神").Should().BeFalse();
    }

    [Fact]
    public void DetectSimilarBusStops_類似する既存エントリを検出すること()
    {
        // Arrange
        var existing = new List<string> { "天神バス停～博多駅", "薬院～大橋" };
        var newEntries = new List<string> { "天神バス停～博多駅前" }; // 「天神バス停～博多駅」を含む

        // Act
        var warnings = BusStopInputViewModel.DetectSimilarBusStops(existing, newEntries);

        // Assert
        warnings.Should().HaveCount(1);
        warnings[0].Should().Contain("天神バス停～博多駅前").And.Contain("天神バス停～博多駅");
    }

    [Fact]
    public void DetectSimilarBusStops_完全一致は検出しないこと()
    {
        // Arrange
        var existing = new List<string> { "天神～博多" };
        var newEntries = new List<string> { "天神～博多" }; // 完全一致

        // Act
        var warnings = BusStopInputViewModel.DetectSimilarBusStops(existing, newEntries);

        // Assert
        warnings.Should().BeEmpty("完全一致は類似警告の対象外");
    }

    /// <summary>
    /// Issue #1811（コードレビュー指摘）: 「↑往復」ボタン（Issue #1570）は前行の「A～B」から
    /// 「B～A」を意図的に生成するため、完全な逆順は取り違えではなく正当な往復入力である。
    /// これを類似警告に含めると、往復入力のたびに保存前の確認ダイアログが出て
    /// 本来見せたい取り違え警告（天神／天神南）が埋もれる。
    /// </summary>
    [Fact]
    public void DetectSimilarBusStops_完全な逆順は往復として検出しないこと()
    {
        // Arrange: 「往復」ボタンで生成される値
        var existing = new List<string> { "天神～博多" };
        var newEntries = new List<string> { "博多～天神" };

        // Act
        var warnings = BusStopInputViewModel.DetectSimilarBusStops(existing, newEntries);

        // Assert
        warnings.Should().BeEmpty("完全な逆順はアプリ自身が「往復」ボタンで生成する正当な入力");
    }

    /// <summary>
    /// Issue #1811（コードレビュー指摘）: 逆順の除外が広すぎず、取り違えの疑い（部分包含）は
    /// 引き続き警告されること。除外だけを足すと対象の入力を無条件に通す実装でも緑になるため対で固定する。
    /// </summary>
    [Fact]
    public void DetectSimilarBusStops_逆順を除外しても部分包含は検出すること()
    {
        // Arrange: 「天神」と「天神南～博多」の取り違え（Issue #1811 の故障シナリオ）
        var existing = new List<string> { "天神南～博多" };
        var newEntries = new List<string> { "天神" };

        // Act
        var warnings = BusStopInputViewModel.DetectSimilarBusStops(existing, newEntries);

        // Assert
        warnings.Should().ContainSingle()
            .Which.Should().Contain("天神").And.Contain("天神南～博多");
    }

    [Fact]
    public void DetectSimilarBusStops_星マークは無視すること()
    {
        // Arrange
        var existing = new List<string> { "天神～博多" };
        var newEntries = new List<string> { "★" };

        // Act
        var warnings = BusStopInputViewModel.DetectSimilarBusStops(existing, newEntries);

        // Assert
        warnings.Should().BeEmpty();
    }

    #endregion
}
