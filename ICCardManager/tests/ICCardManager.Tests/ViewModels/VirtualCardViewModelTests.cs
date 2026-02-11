#if DEBUG
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

public class VirtualCardViewModelTests
{
    private static HybridCardReader CreateHybridReader() => new(new MockCardReader());

    private static Mock<ICardRepository> CreateMockCardRepository(bool isLent = false)
    {
        var mock = new Mock<ICardRepository>();
        mock.Setup(r => r.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new IcCard { IsLent = isLent });
        return mock;
    }

    private static VirtualCardViewModel CreateViewModel(
        HybridCardReader hybridReader = null,
        Mock<ICardRepository> mockRepo = null)
    {
        hybridReader ??= CreateHybridReader();
        mockRepo ??= CreateMockCardRepository();
        return new VirtualCardViewModel(hybridReader, mockRepo.Object);
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
    public async Task InitializeAsync_ReadsBalanceFromReader()
    {
        var hybridReader = CreateHybridReader();
        // MockCardReader のデフォルト残高は 4980
        var vm = CreateViewModel(hybridReader);

        await vm.InitializeAsync();

        vm.CurrentBalance.Should().Be(4980);
    }

    [Fact]
    public async Task InitializeAsync_WithCustomBalance_ReadsCustomValue()
    {
        var hybridReader = CreateHybridReader();
        hybridReader.SetCustomBalance("07FE112233445566", 12345);
        var vm = CreateViewModel(hybridReader);

        await vm.InitializeAsync();

        vm.CurrentBalance.Should().Be(12345);
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

    #region ApplyAndTouch - 残高計算

    [Fact]
    public async Task ApplyAndTouchAsync_CalculatesBalanceFromAmount_Usage()
    {
        var hybridReader = CreateHybridReader();
        await hybridReader.StartReadingAsync();
        // カードが貸出中の場合（返却フロー）
        var mockRepo = CreateMockCardRepository(isLent: true);

        var vm = CreateViewModel(hybridReader, mockRepo);
        vm.CloseAction = () => { };
        vm.CurrentBalance = 5000;

        vm.AddEntry();
        vm.Entries[0].EntryStation = "博多";
        vm.Entries[0].ExitStation = "天神";
        vm.Entries[0].Amount = 200;
        vm.Entries[0].IsCharge = false;

        await vm.ApplyAndTouchAsync();

        var history = (await hybridReader.ReadHistoryAsync(vm.SelectedCard.Idm)).ToList();
        history.Should().HaveCount(1);
        history[0].Balance.Should().Be(5000);
        history[0].Amount.Should().Be(200);
        history[0].EntryStation.Should().Be("博多");
        history[0].ExitStation.Should().Be("天神");
    }

    [Fact]
    public async Task ApplyAndTouchAsync_CalculatesBalanceFromAmount_Charge()
    {
        var hybridReader = CreateHybridReader();
        await hybridReader.StartReadingAsync();
        var mockRepo = CreateMockCardRepository(isLent: true);

        var vm = CreateViewModel(hybridReader, mockRepo);
        vm.CloseAction = () => { };
        vm.CurrentBalance = 8000;

        vm.AddEntry();
        vm.Entries[0].IsCharge = true;
        vm.Entries[0].Amount = 3000;

        await vm.ApplyAndTouchAsync();

        var history = (await hybridReader.ReadHistoryAsync(vm.SelectedCard.Idm)).ToList();
        history[0].Balance.Should().Be(8000);
        history[0].Amount.Should().Be(3000);
        history[0].IsCharge.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyAndTouchAsync_MultipleEntries_CalculatesBalancesCorrectly()
    {
        var hybridReader = CreateHybridReader();
        await hybridReader.StartReadingAsync();
        var mockRepo = CreateMockCardRepository(isLent: true);

        var vm = CreateViewModel(hybridReader, mockRepo);
        vm.CloseAction = () => { };
        vm.CurrentBalance = 4800;

        vm.AddEntry(); // index 0: 利用200円（最新）
        vm.Entries[0].EntryStation = "博多";
        vm.Entries[0].ExitStation = "天神";
        vm.Entries[0].Amount = 200;
        vm.Entries[0].IsCharge = false;

        vm.AddEntry(); // index 1: チャージ3000円
        vm.Entries[1].IsCharge = true;
        vm.Entries[1].Amount = 3000;

        vm.AddEntry(); // index 2: 利用300円（最古）
        vm.Entries[2].EntryStation = "薬院";
        vm.Entries[2].ExitStation = "大橋";
        vm.Entries[2].Amount = 300;
        vm.Entries[2].IsCharge = false;

        await vm.ApplyAndTouchAsync();

        var history = (await hybridReader.ReadHistoryAsync(vm.SelectedCard.Idm)).ToList();
        history.Should().HaveCount(3);
        history[0].Balance.Should().Be(4800);  // 現在残高
        history[1].Balance.Should().Be(5000);  // 4800 + 200（利用前の残高）
        history[2].Balance.Should().Be(2000);  // 5000 - 3000（チャージ前の残高）
    }

    [Fact]
    public async Task ApplyAndTouchAsync_SetsCustomBalance_ToCurrentBalance()
    {
        var hybridReader = CreateHybridReader();
        await hybridReader.StartReadingAsync();
        var mockRepo = CreateMockCardRepository(isLent: true);

        var vm = CreateViewModel(hybridReader, mockRepo);
        vm.CloseAction = () => { };
        vm.CurrentBalance = 3200;

        vm.AddEntry();
        vm.Entries[0].Amount = 200;

        await vm.ApplyAndTouchAsync();

        var balance = await hybridReader.ReadBalanceAsync(vm.SelectedCard.Idm);
        balance.Should().Be(3200);
    }

    [Fact]
    public async Task ApplyAndTouchAsync_EmptyStations_DetectedAsBus()
    {
        var hybridReader = CreateHybridReader();
        await hybridReader.StartReadingAsync();
        var mockRepo = CreateMockCardRepository(isLent: true);

        var vm = CreateViewModel(hybridReader, mockRepo);
        vm.CloseAction = () => { };

        vm.AddEntry();
        vm.Entries[0].EntryStation = "";
        vm.Entries[0].ExitStation = "";
        vm.Entries[0].IsCharge = false;
        vm.Entries[0].Amount = 230;

        await vm.ApplyAndTouchAsync();

        var history = await hybridReader.ReadHistoryAsync(vm.SelectedCard.Idm);
        history.First().IsBus.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyAndTouchAsync_NoEntries_DoesNotOverrideBalance()
    {
        var hybridReader = CreateHybridReader();
        await hybridReader.StartReadingAsync();

        var vm = CreateViewModel(hybridReader);
        vm.CloseAction = () => { };

        await vm.ApplyAndTouchAsync();

        var balance = await hybridReader.ReadBalanceAsync(vm.SelectedCard.Idm);
        balance.Should().Be(4980);
    }

    [Fact]
    public async Task ApplyAndTouchAsync_CardNotLent_AutoLendThenReturn()
    {
        // カードが貸出中でない場合、自動で貸出→返却の2ステップを実行する
        var hybridReader = CreateHybridReader();
        await hybridReader.StartReadingAsync();
        var mockRepo = CreateMockCardRepository(isLent: false);

        var vm = CreateViewModel(hybridReader, mockRepo);
        vm.CloseAction = () => { };
        vm.CurrentBalance = 5000;

        vm.AddEntry();
        vm.Entries[0].Amount = 200;

        var readIdms = new System.Collections.Generic.List<string>();
        hybridReader.CardRead += (_, e) => readIdms.Add(e.Idm);

        await vm.ApplyAndTouchAsync();

        // 貸出（職員証→ICカード）+ 返却（職員証→ICカード）= 4回のタッチ
        readIdms.Should().HaveCount(4);
        readIdms[0].Should().Be(vm.SelectedStaff.Idm);  // 貸出: 職員証
        readIdms[1].Should().Be(vm.SelectedCard.Idm);    // 貸出: ICカード
        readIdms[2].Should().Be(vm.SelectedStaff.Idm);   // 返却: 職員証
        readIdms[3].Should().Be(vm.SelectedCard.Idm);    // 返却: ICカード
    }

    [Fact]
    public async Task ApplyAndTouchAsync_CardAlreadyLent_SingleReturn()
    {
        // カードが貸出中の場合、返却のみ（2回のタッチ）
        var hybridReader = CreateHybridReader();
        await hybridReader.StartReadingAsync();
        var mockRepo = CreateMockCardRepository(isLent: true);

        var vm = CreateViewModel(hybridReader, mockRepo);
        vm.CloseAction = () => { };
        vm.CurrentBalance = 5000;

        vm.AddEntry();
        vm.Entries[0].Amount = 200;

        var readIdms = new System.Collections.Generic.List<string>();
        hybridReader.CardRead += (_, e) => readIdms.Add(e.Idm);

        await vm.ApplyAndTouchAsync();

        // 返却のみ: 職員証→ICカード = 2回
        readIdms.Should().HaveCount(2);
        readIdms[0].Should().Be(vm.SelectedStaff.Idm);
        readIdms[1].Should().Be(vm.SelectedCard.Idm);
    }

    [Fact]
    public async Task ApplyAndTouchAsync_FiresCardReadEvents()
    {
        var hybridReader = CreateHybridReader();
        await hybridReader.StartReadingAsync();

        var vm = CreateViewModel(hybridReader);
        vm.CloseAction = () => { };

        var readIdms = new System.Collections.Generic.List<string>();
        hybridReader.CardRead += (_, e) => readIdms.Add(e.Idm);

        // エントリなし → 単純なタッチ（貸出/返却）
        await vm.ApplyAndTouchAsync();

        readIdms.Should().HaveCount(2);
        readIdms[0].Should().Be(vm.SelectedStaff.Idm);
        readIdms[1].Should().Be(vm.SelectedCard.Idm);
    }

    #endregion

    #region HybridCardReader

    [Fact]
    public async Task HybridCardReader_DelegatesToRealReader_WhenNoCustomData()
    {
        var mockReader = new MockCardReader();
        var hybridReader = new HybridCardReader(mockReader);

        var history = await hybridReader.ReadHistoryAsync("07FE112233445566");
        history.Should().NotBeNull();

        var balance = await hybridReader.ReadBalanceAsync("07FE112233445566");
        balance.Should().Be(mockReader.MockBalance);
    }

    [Fact]
    public async Task HybridCardReader_ReturnsCustomData_WhenSet()
    {
        var mockReader = new MockCardReader();
        var hybridReader = new HybridCardReader(mockReader);

        var customHistory = new System.Collections.Generic.List<LedgerDetail>
        {
            new() { EntryStation = "博多", ExitStation = "天神", Balance = 1000 }
        };
        hybridReader.SetCustomHistory("TEST_IDM", customHistory);
        hybridReader.SetCustomBalance("TEST_IDM", 1000);

        var history = await hybridReader.ReadHistoryAsync("TEST_IDM");
        history.Should().HaveCount(1);
        history.First().EntryStation.Should().Be("博多");

        var balance = await hybridReader.ReadBalanceAsync("TEST_IDM");
        balance.Should().Be(1000);
    }

    [Fact]
    public async Task HybridCardReader_ForwardsEventsFromRealReader()
    {
        var mockReader = new MockCardReader();
        var hybridReader = new HybridCardReader(mockReader);
        await mockReader.StartReadingAsync();

        var readIdms = new System.Collections.Generic.List<string>();
        hybridReader.CardRead += (_, e) => readIdms.Add(e.Idm);

        mockReader.SimulateCardRead("FROM_REAL_READER");

        readIdms.Should().HaveCount(1);
        readIdms[0].Should().Be("FROM_REAL_READER");
    }

    [Fact]
    public void HybridCardReader_SimulateCardRead_FiresWithoutStartReading()
    {
        var mockReader = new MockCardReader();
        var hybridReader = new HybridCardReader(mockReader);

        var readIdms = new System.Collections.Generic.List<string>();
        hybridReader.CardRead += (_, e) => readIdms.Add(e.Idm);

        hybridReader.SimulateCardRead("VIRTUAL_TOUCH");

        readIdms.Should().HaveCount(1);
        readIdms[0].Should().Be("VIRTUAL_TOUCH");
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
