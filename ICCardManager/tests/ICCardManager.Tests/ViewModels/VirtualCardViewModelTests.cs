#if DEBUG
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Infrastructure.CardReader;
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
    public void Constructor_InitializesCardAndStaffLists()
    {
        var mockReader = CreateMockReader();
        var vm = CreateViewModel(mockReader);

        vm.CardIdmList.Should().BeEquivalentTo(mockReader.MockCards);
        vm.StaffIdmList.Should().BeEquivalentTo(mockReader.MockStaffCards);
    }

    [Fact]
    public void Constructor_SelectsFirstCardAndStaff()
    {
        var mockReader = CreateMockReader();
        var vm = CreateViewModel(mockReader);

        vm.SelectedCardIdm.Should().Be(mockReader.MockCards.First());
        vm.SelectedStaffIdm.Should().Be(mockReader.MockStaffCards.First());
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
        // StartReadingAsync を呼んで _isReading を true にする
        await mockReader.StartReadingAsync();

        var vm = CreateViewModel(mockReader);
        vm.CloseAction = () => { }; // ダイアログ閉じを無視

        vm.AddEntry();
        vm.Entries[0].EntryStation = "博多";
        vm.Entries[0].ExitStation = "天神";
        vm.Entries[0].Balance = 4500;

        await vm.ApplyAndTouchAsync();

        // MockCardReader にカスタム履歴が設定されたか確認
        var history = await mockReader.ReadHistoryAsync(vm.SelectedCardIdm);
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

        var balance = await mockReader.ReadBalanceAsync(vm.SelectedCardIdm);
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

        var history = await mockReader.ReadHistoryAsync(vm.SelectedCardIdm);
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

        var history = await mockReader.ReadHistoryAsync(vm.SelectedCardIdm);
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
        readIdms[0].Should().Be(vm.SelectedStaffIdm);
        readIdms[1].Should().Be(vm.SelectedCardIdm);
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
}
#endif
