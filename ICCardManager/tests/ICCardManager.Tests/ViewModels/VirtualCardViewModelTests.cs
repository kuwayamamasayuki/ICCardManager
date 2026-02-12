#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

public class VirtualCardViewModelTests
{
    private static Mock<ILedgerRepository> CreateMockLedgerRepository(int? balance = null)
    {
        var mock = new Mock<ILedgerRepository>();
        if (balance.HasValue)
        {
            mock.Setup(r => r.GetLatestLedgerAsync(It.IsAny<string>()))
                .ReturnsAsync(new Ledger { Balance = balance.Value });
        }
        return mock;
    }

    private static VirtualCardViewModel CreateViewModel(
        Mock<ILedgerRepository> mockLedgerRepo = null)
    {
        mockLedgerRepo ??= CreateMockLedgerRepository();

        var vm = new VirtualCardViewModel(mockLedgerRepo.Object);
        vm.ShowMessage = (_, _) => { }; // テスト中はMessageBoxを表示しない
        vm.CloseAction = () => { };
        return vm;
    }

    #region コンストラクタ

    [Fact]
    public void Constructor_InitializesCardSelectItems_WithDisplayNames()
    {
        var vm = CreateViewModel();

        vm.CardSelectItems.Should().HaveCount(DebugDataService.TestCardList.Length);
        vm.CardSelectItems[0].DisplayName.Should().Contain("はやかけん");
        vm.CardSelectItems[0].Idm.Should().Be("07FE112233445566");
    }

    [Fact]
    public void Constructor_InitializesStaffSelectItems_WithDisplayNames()
    {
        var vm = CreateViewModel();

        vm.StaffSelectItems.Should().HaveCount(DebugDataService.TestStaffList.Length);
        vm.StaffSelectItems[0].DisplayName.Should().Contain("山田太郎");
        vm.StaffSelectItems[0].Idm.Should().Be("FFFF000000000001");
    }

    [Fact]
    public void Constructor_SelectsFirstCardAndStaff()
    {
        var vm = CreateViewModel();

        vm.SelectedCard.Should().NotBeNull();
        vm.SelectedCard.Idm.Should().Be(DebugDataService.TestCardList.First().CardIdm);
        vm.SelectedStaff.Should().NotBeNull();
        vm.SelectedStaff.Idm.Should().Be(DebugDataService.TestStaffList.First().StaffIdm);
    }

    [Fact]
    public void Constructor_EntriesIsEmpty()
    {
        var vm = CreateViewModel();
        vm.Entries.Should().BeEmpty();
    }

    #endregion

    #region InitializeAsync（残高読み取り）

    [Fact]
    public async Task InitializeAsync_ReadsBalanceFromDb()
    {
        var mockLedgerRepo = CreateMockLedgerRepository(balance: 4980);
        var vm = CreateViewModel(mockLedgerRepo: mockLedgerRepo);

        await vm.InitializeAsync();

        vm.CurrentBalance.Should().Be(4980);
    }

    [Fact]
    public async Task InitializeAsync_NoLedgerRecord_BalanceIsZero()
    {
        var mockLedgerRepo = CreateMockLedgerRepository(balance: null);
        var vm = CreateViewModel(mockLedgerRepo: mockLedgerRepo);

        await vm.InitializeAsync();

        vm.CurrentBalance.Should().Be(0);
    }

    #endregion

    #region AddEntry

    [Fact]
    public void AddEntry_AddsOneEntry()
    {
        var vm = CreateViewModel();

        vm.AddEntry();

        vm.Entries.Should().HaveCount(1);
    }

    [Fact]
    public void AddEntry_DefaultAmount_Is200()
    {
        var vm = CreateViewModel();

        vm.AddEntry();

        vm.Entries[0].Amount.Should().Be(200);
    }

    [Fact]
    public void AddEntry_CanAddUpTo20Entries()
    {
        var vm = CreateViewModel();

        for (int i = 0; i < 20; i++)
        {
            vm.AddEntry();
        }

        vm.Entries.Should().HaveCount(20);
    }

    [Fact]
    public void AddEntry_CannotAddMoreThan20()
    {
        var vm = CreateViewModel();

        for (int i = 0; i < 20; i++)
        {
            vm.AddEntry();
        }

        vm.CanAddEntry().Should().BeFalse();
        vm.AddEntryCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void CanAddEntry_TrueWhenUnder20()
    {
        var vm = CreateViewModel();

        vm.AddEntry();

        vm.CanAddEntry().Should().BeTrue();
    }

    #endregion

    #region RemoveEntry

    [Fact]
    public void RemoveEntry_RemovesSpecifiedEntry()
    {
        var vm = CreateViewModel();
        vm.AddEntry();
        vm.AddEntry();
        var entryToRemove = vm.Entries[0];

        vm.RemoveEntry(entryToRemove);

        vm.Entries.Should().HaveCount(1);
        vm.Entries.Should().NotContain(entryToRemove);
    }

    [Fact]
    public void RemoveEntry_NullDoesNotThrow()
    {
        var vm = CreateViewModel();

        var act = () => vm.RemoveEntry(null);

        act.Should().NotThrow();
    }

    [Fact]
    public void RemoveEntry_ReenablesAddAfterRemoval()
    {
        var vm = CreateViewModel();
        for (int i = 0; i < 20; i++)
        {
            vm.AddEntry();
        }
        vm.CanAddEntry().Should().BeFalse();

        vm.RemoveEntry(vm.Entries[0]);

        vm.CanAddEntry().Should().BeTrue();
    }

    #endregion

    #region ApplyAndTouch - TouchResult生成の検証

    [Fact]
    public void ApplyAndTouch_WithEntries_CreatesTouchResult()
    {
        var vm = CreateViewModel();
        vm.CurrentBalance = 5000;

        vm.AddEntry();
        vm.Entries[0].EntryStation = "博多";
        vm.Entries[0].ExitStation = "天神";
        vm.Entries[0].Amount = 200;
        vm.Entries[0].IsCharge = false;

        vm.ApplyAndTouch();

        vm.TouchResult.Should().NotBeNull();
        vm.TouchResult.HasEntries.Should().BeTrue();
        vm.TouchResult.StaffIdm.Should().Be(vm.SelectedStaff.Idm);
        vm.TouchResult.CardIdm.Should().Be(vm.SelectedCard.Idm);
        vm.TouchResult.CurrentBalance.Should().Be(5000);
        vm.TouchResult.HistoryDetails.Should().HaveCount(1);
        vm.TouchResult.HistoryDetails[0].Balance.Should().Be(5000);
        vm.TouchResult.HistoryDetails[0].Amount.Should().Be(200);
        vm.TouchResult.HistoryDetails[0].EntryStation.Should().Be("博多");
        vm.TouchResult.HistoryDetails[0].ExitStation.Should().Be("天神");
    }

    [Fact]
    public void ApplyAndTouch_ChargeEntry_SetsIsChargeFlag()
    {
        var vm = CreateViewModel();
        vm.CurrentBalance = 8000;

        vm.AddEntry();
        vm.Entries[0].IsCharge = true;
        vm.Entries[0].Amount = 3000;

        vm.ApplyAndTouch();

        vm.TouchResult.HistoryDetails[0].Balance.Should().Be(8000);
        vm.TouchResult.HistoryDetails[0].Amount.Should().Be(3000);
        vm.TouchResult.HistoryDetails[0].IsCharge.Should().BeTrue();
    }

    [Fact]
    public void ApplyAndTouch_MultipleEntries_CalculatesBalancesCorrectly()
    {
        // UI上は上＝最古、下＝最新の順で入力
        // 内部では逆順（FeliCa慣例: index 0＝最新）に変換される
        var vm = CreateViewModel();
        vm.CurrentBalance = 4800;

        vm.AddEntry(); // index 0: 利用300円（最古 = 上の行）
        vm.Entries[0].EntryStation = "薬院";
        vm.Entries[0].ExitStation = "大橋";
        vm.Entries[0].Amount = 300;
        vm.Entries[0].IsCharge = false;

        vm.AddEntry(); // index 1: チャージ3000円
        vm.Entries[1].IsCharge = true;
        vm.Entries[1].Amount = 3000;

        vm.AddEntry(); // index 2: 利用200円（最新 = 下の行）
        vm.Entries[2].EntryStation = "博多";
        vm.Entries[2].ExitStation = "天神";
        vm.Entries[2].Amount = 200;
        vm.Entries[2].IsCharge = false;

        vm.ApplyAndTouch();

        // HistoryDetails は最新→最古の順（FeliCa慣例）
        vm.TouchResult.HistoryDetails.Should().HaveCount(3);
        vm.TouchResult.HistoryDetails[0].Balance.Should().Be(4800);  // 最新: 現在残高
        vm.TouchResult.HistoryDetails[0].EntryStation.Should().Be("博多");
        vm.TouchResult.HistoryDetails[1].Balance.Should().Be(5000);  // 4800 + 200（利用前の残高）
        vm.TouchResult.HistoryDetails[1].IsCharge.Should().BeTrue();
        vm.TouchResult.HistoryDetails[2].Balance.Should().Be(2000);  // 5000 - 3000（チャージ前の残高）
        vm.TouchResult.HistoryDetails[2].EntryStation.Should().Be("薬院");
    }

    [Fact]
    public void ApplyAndTouch_PassesCorrectBalance()
    {
        var vm = CreateViewModel();
        vm.CurrentBalance = 3200;

        vm.AddEntry();
        vm.Entries[0].Amount = 200;

        vm.ApplyAndTouch();

        vm.TouchResult.CurrentBalance.Should().Be(3200);
    }

    [Fact]
    public void ApplyAndTouch_EmptyStations_DetectedAsBus()
    {
        var vm = CreateViewModel();

        vm.AddEntry();
        vm.Entries[0].EntryStation = "";
        vm.Entries[0].ExitStation = "";
        vm.Entries[0].IsCharge = false;
        vm.Entries[0].Amount = 230;

        vm.ApplyAndTouch();

        vm.TouchResult.HistoryDetails.First().IsBus.Should().BeTrue();
    }

    [Fact]
    public void ApplyAndTouch_NoEntries_CreatesEmptyTouchResult()
    {
        var vm = CreateViewModel();

        vm.ApplyAndTouch();

        // エントリなしでもTouchResultは生成される（HasEntries = false）
        vm.TouchResult.Should().NotBeNull();
        vm.TouchResult.HasEntries.Should().BeFalse();
    }

    [Fact]
    public void ApplyAndTouch_NoSelection_DoesNotCreateResult()
    {
        var vm = CreateViewModel();
        vm.SelectedCard = null;

        vm.ApplyAndTouch();

        vm.TouchResult.Should().BeNull("カード未選択の場合はTouchResultを作らない");
    }

    #endregion

    #region VirtualHistoryEntry

    [Fact]
    public void VirtualHistoryEntry_DefaultValues()
    {
        var entry = new VirtualHistoryEntry();

        entry.UseDate.Should().Be(DateTime.Today);
        entry.EntryStation.Should().Be("");
        entry.ExitStation.Should().Be("");
        entry.Amount.Should().Be(0);
        entry.IsCharge.Should().BeFalse();
    }

    #endregion

    #region IdmSelectItem

    [Fact]
    public void IdmSelectItem_ToString_ReturnsDisplayName()
    {
        var item = new IdmSelectItem { Idm = "123", DisplayName = "テスト表示名" };
        item.ToString().Should().Be("テスト表示名");
    }

    #endregion
}
#endif
