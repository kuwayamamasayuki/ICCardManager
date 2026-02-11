#if DEBUG
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

public class VirtualCardViewModelTests
{
    private static MockCardReader CreateMockReader() => new();

    private static VirtualCardViewModel CreateViewModel(MockCardReader mockReader = null)
    {
        mockReader ??= CreateMockReader();
        return new VirtualCardViewModel(mockReader);
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

    #region AddEntry

    [Fact]
    public void AddEntry_AddsOneEntry()
    {
        var vm = CreateViewModel();

        vm.AddEntry();

        vm.Entries.Should().HaveCount(1);
    }

    [Fact]
    public void AddEntry_DefaultBalance_Is5000ForFirstEntry()
    {
        var vm = CreateViewModel();

        vm.AddEntry();

        vm.Entries[0].Balance.Should().Be(5000);
    }

    [Fact]
    public void AddEntry_InheritsBalanceFromLastEntry()
    {
        var vm = CreateViewModel();

        vm.AddEntry();
        vm.Entries[0].Balance = 3000;
        vm.AddEntry();

        vm.Entries[1].Balance.Should().Be(3000);
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

    #region ApplyAndTouch

    [Fact]
    public async Task ApplyAndTouchAsync_SetsCustomHistoryOnMockReader()
    {
        var mockReader = CreateMockReader();
        await mockReader.StartReadingAsync();

        var vm = CreateViewModel(mockReader);
        vm.CloseAction = () => { };

        vm.AddEntry();
        vm.Entries[0].EntryStation = "博多";
        vm.Entries[0].ExitStation = "天神";
        vm.Entries[0].Balance = 4500;

        await vm.ApplyAndTouchAsync();

        var history = await mockReader.ReadHistoryAsync(vm.SelectedCard.Idm);
        history.Should().HaveCount(1);
        var entry = history.First();
        entry.EntryStation.Should().Be("博多");
        entry.ExitStation.Should().Be("天神");
        entry.Balance.Should().Be(4500);
    }

    [Fact]
    public async Task ApplyAndTouchAsync_SetsCustomBalanceOnMockReader()
    {
        var mockReader = CreateMockReader();
        await mockReader.StartReadingAsync();

        var vm = CreateViewModel(mockReader);
        vm.CloseAction = () => { };

        vm.AddEntry();
        vm.Entries[0].Balance = 3200;

        await vm.ApplyAndTouchAsync();

        var balance = await mockReader.ReadBalanceAsync(vm.SelectedCard.Idm);
        balance.Should().Be(3200);
    }

    [Fact]
    public async Task ApplyAndTouchAsync_EmptyStations_DetectedAsBus()
    {
        var mockReader = CreateMockReader();
        await mockReader.StartReadingAsync();

        var vm = CreateViewModel(mockReader);
        vm.CloseAction = () => { };

        vm.AddEntry();
        vm.Entries[0].EntryStation = "";
        vm.Entries[0].ExitStation = "";
        vm.Entries[0].IsCharge = false;
        vm.Entries[0].Balance = 4000;

        await vm.ApplyAndTouchAsync();

        var history = await mockReader.ReadHistoryAsync(vm.SelectedCard.Idm);
        history.First().IsBus.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyAndTouchAsync_ChargeEntry_SetsIsCharge()
    {
        var mockReader = CreateMockReader();
        await mockReader.StartReadingAsync();

        var vm = CreateViewModel(mockReader);
        vm.CloseAction = () => { };

        vm.AddEntry();
        vm.Entries[0].IsCharge = true;
        vm.Entries[0].Balance = 8000;

        await vm.ApplyAndTouchAsync();

        var history = await mockReader.ReadHistoryAsync(vm.SelectedCard.Idm);
        history.First().IsCharge.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyAndTouchAsync_FiresCardReadEvents()
    {
        var mockReader = CreateMockReader();
        await mockReader.StartReadingAsync();

        var vm = CreateViewModel(mockReader);
        vm.CloseAction = () => { };

        var readIdms = new System.Collections.Generic.List<string>();
        mockReader.CardRead += (_, e) => readIdms.Add(e.Idm);

        await vm.ApplyAndTouchAsync();

        // 職員証 → ICカードの順で2回タッチ
        readIdms.Should().HaveCount(2);
        readIdms[0].Should().Be(vm.SelectedStaff.Idm);
        readIdms[1].Should().Be(vm.SelectedCard.Idm);
    }

    #endregion

    #region VirtualHistoryEntry

    [Fact]
    public void VirtualHistoryEntry_DefaultValues()
    {
        var entry = new VirtualHistoryEntry();

        entry.UseDate.Should().Be(System.DateTime.Today);
        entry.EntryStation.Should().Be("");
        entry.ExitStation.Should().Be("");
        entry.Balance.Should().Be(0);
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
