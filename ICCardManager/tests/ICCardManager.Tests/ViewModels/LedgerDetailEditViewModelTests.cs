using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Models;
using ICCardManager.ViewModels;
using Xunit;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// LedgerDetailEditViewModelの単体テスト
/// Issue #635: 履歴詳細の追加/編集ダイアログ
/// </summary>
public class LedgerDetailEditViewModelTests
{
    private readonly LedgerDetailEditViewModel _viewModel;

    public LedgerDetailEditViewModelTests()
    {
        _viewModel = new LedgerDetailEditViewModel();
    }

    /// <summary>
    /// テスト用のアイテムリストを作成
    /// </summary>
    private static ObservableCollection<LedgerDetailItemViewModel> CreateTestItems()
    {
        var items = new ObservableCollection<LedgerDetailItemViewModel>();
        var details = new[]
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 2, 10, 9, 0, 0),
                EntryStation = "博多", ExitStation = "天神",
                Amount = 260, Balance = 740,
                SequenceNumber = 1
            },
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 2, 10, 12, 0, 0),
                EntryStation = "天神", ExitStation = "赤坂",
                Amount = 200, Balance = 540,
                SequenceNumber = 2
            },
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 2, 10, 18, 0, 0),
                EntryStation = "赤坂", ExitStation = "博多",
                Amount = 260, Balance = 280,
                SequenceNumber = 3
            }
        };

        for (int i = 0; i < details.Length; i++)
        {
            items.Add(new LedgerDetailItemViewModel(details[i], i));
        }
        return items;
    }

    #region InitializeForInsert テスト

    [Fact]
    public void InitializeForInsert_SetsInsertMode()
    {
        // Arrange
        var items = CreateTestItems();

        // Act
        _viewModel.InitializeForInsert(items, 1);

        // Assert
        _viewModel.IsInsertMode.Should().BeTrue();
        _viewModel.DialogTitle.Should().Be("利用詳細の追加");
        _viewModel.InsertIndex.Should().Be(1);
    }

    [Fact]
    public void InitializeForInsert_ClampsIndexToRange()
    {
        // Arrange
        var items = CreateTestItems();

        // Act
        _viewModel.InitializeForInsert(items, 100);

        // Assert: 最大はitems.Countにクランプされる
        _viewModel.InsertIndex.Should().Be(items.Count);
    }

    #endregion

    #region InitializeForEdit テスト

    [Fact]
    public void InitializeForEdit_PopulatesFields()
    {
        // Arrange
        var items = CreateTestItems();
        var editTarget = items[0]; // 博多→天神

        // Act
        _viewModel.InitializeForEdit(editTarget, items);

        // Assert
        _viewModel.IsInsertMode.Should().BeFalse();
        _viewModel.DialogTitle.Should().Be("利用詳細の編集");
        _viewModel.SelectedUsageType.Should().Be(UsageType.Rail);
        _viewModel.EntryStation.Should().Be("博多");
        _viewModel.ExitStation.Should().Be("天神");
        _viewModel.AmountText.Should().Be("260");
        _viewModel.BalanceText.Should().Be("740");
    }

    [Fact]
    public void InitializeForEdit_ChargeType_SetsCorrectUsageType()
    {
        // Arrange
        var chargeDetail = new LedgerDetail
        {
            UseDate = new DateTime(2026, 2, 10, 10, 0, 0),
            IsCharge = true,
            Amount = 3000,
            Balance = 3540,
            SequenceNumber = 1
        };
        var items = new ObservableCollection<LedgerDetailItemViewModel>
        {
            new LedgerDetailItemViewModel(chargeDetail, 0)
        };

        // Act
        _viewModel.InitializeForEdit(items[0], items);

        // Assert
        _viewModel.SelectedUsageType.Should().Be(UsageType.Charge);
    }

    [Fact]
    public void InitializeForEdit_BusType_SetsCorrectUsageType()
    {
        // Arrange
        var busDetail = new LedgerDetail
        {
            UseDate = new DateTime(2026, 2, 10, 10, 0, 0),
            IsBus = true, BusStops = "天神バス停",
            Amount = 100, Balance = 440,
            SequenceNumber = 1
        };
        var items = new ObservableCollection<LedgerDetailItemViewModel>
        {
            new LedgerDetailItemViewModel(busDetail, 0)
        };

        // Act
        _viewModel.InitializeForEdit(items[0], items);

        // Assert
        _viewModel.SelectedUsageType.Should().Be(UsageType.Bus);
        _viewModel.BusStops.Should().Be("天神バス停");
    }

    #endregion

    #region UsageType切替 テスト

    [Fact]
    public void UsageTypeRail_ShowsStationFields()
    {
        // Act
        _viewModel.SelectedUsageType = UsageType.Rail;

        // Assert
        _viewModel.ShowRailFields.Should().BeTrue();
        _viewModel.ShowBusFields.Should().BeFalse();
    }

    [Fact]
    public void UsageTypeBus_ShowsBusFields()
    {
        // Act
        _viewModel.SelectedUsageType = UsageType.Bus;

        // Assert
        _viewModel.ShowRailFields.Should().BeFalse();
        _viewModel.ShowBusFields.Should().BeTrue();
    }

    [Fact]
    public void UsageTypeCharge_HidesAllRouteFields()
    {
        // Act
        _viewModel.SelectedUsageType = UsageType.Charge;

        // Assert
        _viewModel.ShowRailFields.Should().BeFalse();
        _viewModel.ShowBusFields.Should().BeFalse();
    }

    #endregion

    #region 残高自動計算 テスト

    [Fact]
    public void AutoCalculateBalance_Usage_SubtractsFromPrevious()
    {
        // Arrange: 前行の残高が740
        var items = CreateTestItems();
        _viewModel.InitializeForInsert(items, 1); // index=1, 前行は博多→天神(Balance=740)
        _viewModel.SelectedUsageType = UsageType.Rail;

        // Act
        _viewModel.AmountText = "200";

        // Assert: 740 - 200 = 540
        _viewModel.BalanceText.Should().Be("540");
    }

    [Fact]
    public void AutoCalculateBalance_Charge_AddsToPrevious()
    {
        // Arrange: 前行の残高が540
        var items = CreateTestItems();
        _viewModel.InitializeForInsert(items, 2); // index=2, 前行は天神→赤坂(Balance=540)
        _viewModel.SelectedUsageType = UsageType.Charge;

        // Act
        _viewModel.AmountText = "3000";

        // Assert: 540 + 3000 = 3540
        _viewModel.BalanceText.Should().Be("3540");
    }

    [Fact]
    public void AutoCalculateBalance_PointRedemption_AddsToPrevious()
    {
        // Arrange
        var items = CreateTestItems();
        _viewModel.InitializeForInsert(items, 1);
        _viewModel.SelectedUsageType = UsageType.PointRedemption;

        // Act
        _viewModel.AmountText = "50";

        // Assert: 740 + 50 = 790
        _viewModel.BalanceText.Should().Be("790");
    }

    [Fact]
    public void AutoCalculateBalance_FirstRow_PreviousBalanceIsZero()
    {
        // Arrange: 先頭に挿入
        var items = CreateTestItems();
        _viewModel.InitializeForInsert(items, 0);
        _viewModel.SelectedUsageType = UsageType.Charge;

        // Act
        _viewModel.AmountText = "1000";

        // Assert: 0 + 1000 = 1000
        _viewModel.BalanceText.Should().Be("1000");
    }

    [Fact]
    public void AutoCalculateBalance_Off_DoesNotChange()
    {
        // Arrange
        var items = CreateTestItems();
        _viewModel.InitializeForInsert(items, 1);
        _viewModel.AutoCalculateBalance = false;
        _viewModel.BalanceText = "999";

        // Act
        _viewModel.AmountText = "200";

        // Assert: 自動計算オフなので変わらない
        _viewModel.BalanceText.Should().Be("999");
    }

    #endregion

    #region SuggestInsertIndex テスト

    [Fact]
    public void SuggestInsertIndex_ByDate_CorrectPosition()
    {
        // Arrange
        var items = CreateTestItems();
        _viewModel.InitializeForInsert(items, 0);

        // Act: 10:00の利用 → 9:00と12:00の間に挿入
        var index = _viewModel.SuggestInsertIndex(new DateTime(2026, 2, 10, 10, 0, 0));

        // Assert
        index.Should().Be(1, "9:00の次、12:00の前");
    }

    [Fact]
    public void SuggestInsertIndex_AfterAllItems_ReturnsCount()
    {
        // Arrange
        var items = CreateTestItems();
        _viewModel.InitializeForInsert(items, 0);

        // Act: 20:00 → 全アイテムより後
        var index = _viewModel.SuggestInsertIndex(new DateTime(2026, 2, 10, 20, 0, 0));

        // Assert
        index.Should().Be(3, "末尾に挿入");
    }

    [Fact]
    public void SuggestInsertIndex_BeforeAllItems_ReturnsZero()
    {
        // Arrange
        var items = CreateTestItems();
        _viewModel.InitializeForInsert(items, 0);

        // Act: 8:00 → 全アイテムより前
        var index = _viewModel.SuggestInsertIndex(new DateTime(2026, 2, 10, 8, 0, 0));

        // Assert
        index.Should().Be(0, "先頭に挿入");
    }

    #endregion

    #region MoveInsertPosition テスト

    [Fact]
    public void MoveInsertPosition_UpDown_BoundaryCheck()
    {
        // Arrange
        var items = CreateTestItems();
        _viewModel.InitializeForInsert(items, 0);

        // Act: 0より上には行けない
        _viewModel.MoveInsertPositionUpCommand.Execute(null);
        _viewModel.InsertIndex.Should().Be(0, "下限");

        // Act: 下に移動
        _viewModel.MoveInsertPositionDownCommand.Execute(null);
        _viewModel.InsertIndex.Should().Be(1);

        // Act: items.Countまで移動
        _viewModel.MoveInsertPositionDownCommand.Execute(null);
        _viewModel.MoveInsertPositionDownCommand.Execute(null);
        _viewModel.InsertIndex.Should().Be(3, "items.Count");

        // Act: items.Countより上には行けない
        _viewModel.MoveInsertPositionDownCommand.Execute(null);
        _viewModel.InsertIndex.Should().Be(3, "上限");
    }

    #endregion

    #region Validate テスト

    [Fact]
    public void Confirm_MissingDate_ValidationError()
    {
        // Arrange: 日付なし
        _viewModel.UseDate = null;
        _viewModel.AmountText = "260";

        // Act
        var result = _viewModel.Validate();

        // Assert
        result.Should().BeFalse();
        _viewModel.HasValidationError.Should().BeTrue();
        _viewModel.ValidationMessage.Should().Contain("利用日");
    }

    [Fact]
    public void Confirm_MissingAmount_ValidationError()
    {
        // Arrange
        _viewModel.UseDate = DateTime.Today;
        _viewModel.AmountText = "";

        // Act
        var result = _viewModel.Validate();

        // Assert
        result.Should().BeFalse();
        _viewModel.ValidationMessage.Should().Contain("金額");
    }

    [Fact]
    public void Confirm_NegativeAmount_ValidationError()
    {
        // Arrange
        _viewModel.UseDate = DateTime.Today;
        _viewModel.AmountText = "-100";

        // Act
        var result = _viewModel.Validate();

        // Assert
        result.Should().BeFalse();
        _viewModel.ValidationMessage.Should().Contain("0以上");
    }

    [Fact]
    public void Confirm_InvalidTime_ValidationError()
    {
        // Arrange
        _viewModel.UseDate = DateTime.Today;
        _viewModel.AmountText = "260";
        _viewModel.UseTimeText = "abc";

        // Act
        var result = _viewModel.Validate();

        // Assert
        result.Should().BeFalse();
        _viewModel.ValidationMessage.Should().Contain("HH:mm");
    }

    [Fact]
    public void Confirm_ValidData_SetsIsCompleted()
    {
        // Arrange
        var items = CreateTestItems();
        _viewModel.InitializeForInsert(items, 0);
        _viewModel.UseDate = new DateTime(2026, 2, 10);
        _viewModel.UseTimeText = "10:30";
        _viewModel.AmountText = "260";
        _viewModel.EntryStation = "博多";
        _viewModel.ExitStation = "天神";

        // Act
        _viewModel.ConfirmCommand.Execute(null);

        // Assert
        _viewModel.IsCompleted.Should().BeTrue();
        _viewModel.Result.Should().NotBeNull();
    }

    #endregion

    #region BuildLedgerDetail テスト

    [Fact]
    public void BuildLedgerDetail_Rail_CorrectFlags()
    {
        // Arrange
        _viewModel.UseDate = new DateTime(2026, 2, 10);
        _viewModel.UseTimeText = "10:30";
        _viewModel.SelectedUsageType = UsageType.Rail;
        _viewModel.EntryStation = "博多";
        _viewModel.ExitStation = "天神";
        _viewModel.AmountText = "260";
        _viewModel.BalanceText = "740";

        // Act
        var detail = _viewModel.BuildLedgerDetail();

        // Assert
        detail.IsCharge.Should().BeFalse();
        detail.IsBus.Should().BeFalse();
        detail.IsPointRedemption.Should().BeFalse();
        detail.EntryStation.Should().Be("博多");
        detail.ExitStation.Should().Be("天神");
        detail.Amount.Should().Be(260);
        detail.Balance.Should().Be(740);
        detail.UseDate.Should().Be(new DateTime(2026, 2, 10, 10, 30, 0));
    }

    [Fact]
    public void BuildLedgerDetail_Bus_CorrectFlags()
    {
        // Arrange
        _viewModel.UseDate = new DateTime(2026, 2, 10);
        _viewModel.SelectedUsageType = UsageType.Bus;
        _viewModel.BusStops = "天神バス停";
        _viewModel.AmountText = "100";
        _viewModel.BalanceText = "640";

        // Act
        var detail = _viewModel.BuildLedgerDetail();

        // Assert
        detail.IsBus.Should().BeTrue();
        detail.IsCharge.Should().BeFalse();
        detail.BusStops.Should().Be("天神バス停");
        detail.EntryStation.Should().BeEmpty();
        detail.ExitStation.Should().BeEmpty();
    }

    [Fact]
    public void BuildLedgerDetail_Charge_CorrectFlags()
    {
        // Arrange
        _viewModel.UseDate = new DateTime(2026, 2, 10);
        _viewModel.SelectedUsageType = UsageType.Charge;
        _viewModel.AmountText = "3000";
        _viewModel.BalanceText = "3540";

        // Act
        var detail = _viewModel.BuildLedgerDetail();

        // Assert
        detail.IsCharge.Should().BeTrue();
        detail.IsBus.Should().BeFalse();
        detail.IsPointRedemption.Should().BeFalse();
    }

    [Fact]
    public void BuildLedgerDetail_PointRedemption_CorrectFlags()
    {
        // Arrange
        _viewModel.UseDate = new DateTime(2026, 2, 10);
        _viewModel.SelectedUsageType = UsageType.PointRedemption;
        _viewModel.AmountText = "50";
        _viewModel.BalanceText = "790";

        // Act
        var detail = _viewModel.BuildLedgerDetail();

        // Assert
        detail.IsPointRedemption.Should().BeTrue();
        detail.IsCharge.Should().BeFalse();
        detail.IsBus.Should().BeFalse();
    }

    #endregion
}
