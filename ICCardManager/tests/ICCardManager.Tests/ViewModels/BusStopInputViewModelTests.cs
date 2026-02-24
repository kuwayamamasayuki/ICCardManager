using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// BusStopInputViewModelの単体テスト
/// </summary>
public class BusStopInputViewModelTests
{
    private readonly Mock<ILedgerRepository> _ledgerRepoMock;
    private readonly Mock<ISettingsRepository> _settingsRepoMock;
    private readonly BusStopInputViewModel _viewModel;

    public BusStopInputViewModelTests()
    {
        _ledgerRepoMock = new Mock<ILedgerRepository>();
        _settingsRepoMock = new Mock<ISettingsRepository>();

        // バス停サジェストのデフォルト: 空
        _ledgerRepoMock.Setup(r => r.GetBusStopSuggestionsAsync())
            .ReturnsAsync(Enumerable.Empty<(string BusStops, int UsageCount)>());

        _viewModel = new BusStopInputViewModel(
            _ledgerRepoMock.Object,
            _settingsRepoMock.Object);
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
        var suggestions = new List<(string BusStops, int UsageCount)>
        {
            ("天神バス停～博多駅前", 5),
            ("薬院駅前～大橋駅前", 3),
        };
        _ledgerRepoMock.Setup(r => r.GetBusStopSuggestionsAsync())
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
        var suggestions = new List<(string BusStops, int UsageCount)>
        {
            ("天神バス停～博多駅前", 5),
            ("薬院駅前～大橋駅前", 3),
        };
        _ledgerRepoMock.Setup(r => r.GetBusStopSuggestionsAsync())
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
        _ledgerRepoMock.Setup(r => r.GetBusStopSuggestionsAsync())
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
            r => r.UpdateAsync(It.IsAny<Ledger>()), Times.Never);
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
                It.IsAny<int>(), It.IsAny<List<(int, string)>>()))
            .Returns(Task.CompletedTask);

        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(true);

        _viewModel.InitializeWithDetails(ledger, ledger.Details);

        // Act
        await _viewModel.SaveAsync();

        // Assert: 未入力のバス停は★マーク
        detail1.BusStops.Should().Be("★");
        detail2.BusStops.Should().Be("天神バス停");
    }

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
                It.IsAny<int>(), It.IsAny<List<(int, string)>>()))
            .Returns(Task.CompletedTask);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>()))
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
                It.IsAny<int>(), It.IsAny<List<(int, string)>>()))
            .Returns(Task.CompletedTask);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(false);

        _viewModel.InitializeWithDetails(ledger, details);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.IsSaved.Should().BeFalse();
        _viewModel.StatusMessage.Should().Be("保存に失敗しました");
    }

    #endregion

    #region SkipAsync

    [Fact]
    public async Task SkipAsync_未入力のバス停のみに星マークが付くこと()
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
                It.IsAny<int>(), It.IsAny<List<(int, string)>>()))
            .Returns(Task.CompletedTask);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(true);

        _viewModel.InitializeWithDetails(ledger, ledger.Details);

        // Act
        await _viewModel.SkipAsync();

        // Assert
        detail1.BusStops.Should().Be("★");
        detail2.BusStops.Should().Be("天神バス停"); // 入力済みは変更なし
        _viewModel.IsSaved.Should().BeTrue();
    }

    #endregion
}

/// <summary>
/// BusStopInputItemの単体テスト（サジェストフィルタリング）
/// </summary>
public class BusStopInputItemTests
{
    private BusStopInputItem CreateItem(string busStops = "", List<string> suggestions = null)
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
}
