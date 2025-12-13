using FluentAssertions;
using ICCardManager.Dtos;
using ICCardManager.Models;
using Xunit;

namespace ICCardManager.Tests.Dtos;

/// <summary>
/// DtoMapperの単体テスト
/// </summary>
public class DtoMapperTests
{
    #region IcCard → CardDto

    /// <summary>
    /// IcCardからCardDtoへの変換が正しく行われること
    /// </summary>
    [Fact]
    public void ToDto_FromIcCard_ShouldMapAllProperties()
    {
        // Arrange
        var card = new IcCard
        {
            CardIdm = "07FE112233445566",
            CardType = "はやかけん",
            CardNumber = "H-001",
            Note = "テストカード",
            IsLent = true,
            LastLentStaff = "FFFF000000000001",
            LastLentAt = new DateTime(2025, 1, 15, 10, 30, 0)
        };

        // Act
        var dto = card.ToDto(staffName: "山田太郎");

        // Assert
        dto.CardIdm.Should().Be("07FE112233445566");
        dto.CardType.Should().Be("はやかけん");
        dto.CardNumber.Should().Be("H-001");
        dto.Note.Should().Be("テストカード");
        dto.IsLent.Should().BeTrue();
        dto.LastLentStaff.Should().Be("FFFF000000000001");
        dto.LentStaffName.Should().Be("山田太郎");
        dto.LentAt.Should().Be(new DateTime(2025, 1, 15, 10, 30, 0));
    }

    /// <summary>
    /// 貸出中でないカードの変換が正しく行われること
    /// </summary>
    [Fact]
    public void ToDto_FromIcCard_WhenNotLent_ShouldMapCorrectly()
    {
        // Arrange
        var card = new IcCard
        {
            CardIdm = "07FE112233445566",
            CardType = "nimoca",
            CardNumber = "N-001",
            IsLent = false
        };

        // Act
        var dto = card.ToDto();

        // Assert
        dto.IsLent.Should().BeFalse();
        dto.LentStaffName.Should().BeNull();
        dto.LentAt.Should().BeNull();
        dto.LastLentStaff.Should().BeNull();
    }

    /// <summary>
    /// 表示用プロパティが正しく生成されること
    /// </summary>
    [Fact]
    public void CardDto_DisplayProperties_ShouldBeCorrect()
    {
        // Arrange
        var dto = new CardDto
        {
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = true,
            LentAt = new DateTime(2025, 1, 15, 10, 30, 0)
        };

        // Assert
        dto.DisplayName.Should().Be("はやかけん H-001");
        dto.LentStatusDisplay.Should().Be("貸出中");
        dto.LentAtDisplay.Should().Be("2025/01/15 10:30");
    }

    /// <summary>
    /// 在庫状態の表示が正しいこと
    /// </summary>
    [Fact]
    public void CardDto_LentStatusDisplay_WhenNotLent_ShouldShowInStock()
    {
        // Arrange
        var dto = new CardDto { IsLent = false };

        // Assert
        dto.LentStatusDisplay.Should().Be("在庫");
    }

    /// <summary>
    /// カードリストの一括変換が正しく行われること
    /// </summary>
    [Fact]
    public void ToDtoList_FromIcCards_ShouldMapAllItems()
    {
        // Arrange
        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = "07FE112233445566", CardType = "はやかけん", CardNumber = "H-001" },
            new IcCard { CardIdm = "05FE112233445567", CardType = "nimoca", CardNumber = "N-001" }
        };

        // Act
        var dtos = cards.ToDtoList();

        // Assert
        dtos.Should().HaveCount(2);
        dtos[0].CardType.Should().Be("はやかけん");
        dtos[1].CardType.Should().Be("nimoca");
    }

    #endregion

    #region Staff → StaffDto

    /// <summary>
    /// StaffからStaffDtoへの変換が正しく行われること
    /// </summary>
    [Fact]
    public void ToDto_FromStaff_ShouldMapAllProperties()
    {
        // Arrange
        var staff = new Staff
        {
            StaffIdm = "FFFF000000000001",
            Name = "山田太郎",
            Number = "001",
            Note = "テスト職員"
        };

        // Act
        var dto = staff.ToDto();

        // Assert
        dto.StaffIdm.Should().Be("FFFF000000000001");
        dto.Name.Should().Be("山田太郎");
        dto.Number.Should().Be("001");
        dto.Note.Should().Be("テスト職員");
    }

    /// <summary>
    /// 職員番号がある場合の表示名が正しいこと
    /// </summary>
    [Fact]
    public void StaffDto_DisplayName_WithNumber_ShouldIncludeNumber()
    {
        // Arrange
        var dto = new StaffDto { Name = "山田太郎", Number = "001" };

        // Assert
        dto.DisplayName.Should().Be("001 山田太郎");
    }

    /// <summary>
    /// 職員番号がない場合の表示名が正しいこと
    /// </summary>
    [Fact]
    public void StaffDto_DisplayName_WithoutNumber_ShouldShowNameOnly()
    {
        // Arrange
        var dto = new StaffDto { Name = "山田太郎", Number = null };

        // Assert
        dto.DisplayName.Should().Be("山田太郎");
    }

    /// <summary>
    /// 職員リストの一括変換が正しく行われること
    /// </summary>
    [Fact]
    public void ToDtoList_FromStaffs_ShouldMapAllItems()
    {
        // Arrange
        var staffList = new List<Staff>
        {
            new Staff { StaffIdm = "FFFF000000000001", Name = "山田太郎" },
            new Staff { StaffIdm = "FFFF000000000002", Name = "鈴木花子" }
        };

        // Act
        var dtos = staffList.ToDtoList();

        // Assert
        dtos.Should().HaveCount(2);
        dtos[0].Name.Should().Be("山田太郎");
        dtos[1].Name.Should().Be("鈴木花子");
    }

    #endregion

    #region Ledger → LedgerDto

    /// <summary>
    /// LedgerからLedgerDtoへの変換が正しく行われること
    /// </summary>
    [Fact]
    public void ToDto_FromLedger_ShouldMapAllProperties()
    {
        // Arrange
        var ledger = new Ledger
        {
            Id = 1,
            CardIdm = "07FE112233445566",
            Date = new DateTime(2025, 1, 15),
            Summary = "鉄道（博多駅～天神駅）",
            Income = 0,
            Expense = 210,
            Balance = 4790,
            StaffName = "山田太郎",
            Note = "通勤利用",
            IsLentRecord = false,
            Details = new List<LedgerDetail>
            {
                new LedgerDetail { LedgerId = 1, EntryStation = "博多駅", ExitStation = "天神駅", Amount = 210 }
            }
        };

        // Act
        var dto = ledger.ToDto();

        // Assert
        dto.Id.Should().Be(1);
        dto.CardIdm.Should().Be("07FE112233445566");
        dto.Date.Should().Be(new DateTime(2025, 1, 15));
        dto.DateDisplay.Should().Contain("R7"); // 和暦変換される（短縮形式: R7.01.15）
        dto.Summary.Should().Be("鉄道（博多駅～天神駅）");
        dto.Income.Should().Be(0);
        dto.Expense.Should().Be(210);
        dto.Balance.Should().Be(4790);
        dto.StaffName.Should().Be("山田太郎");
        dto.Note.Should().Be("通勤利用");
        dto.IsLentRecord.Should().BeFalse();
        dto.Details.Should().HaveCount(1);
    }

    /// <summary>
    /// 受入金額の表示プロパティが正しいこと
    /// </summary>
    [Theory]
    [InlineData(1000, "+1,000")]
    [InlineData(0, "")]
    public void LedgerDto_IncomeDisplay_ShouldFormatCorrectly(int income, string expected)
    {
        // Arrange
        var dto = new LedgerDto { Income = income };

        // Assert
        dto.IncomeDisplay.Should().Be(expected);
    }

    /// <summary>
    /// 払出金額の表示プロパティが正しいこと
    /// </summary>
    [Theory]
    [InlineData(210, "-210")]
    [InlineData(0, "")]
    public void LedgerDto_ExpenseDisplay_ShouldFormatCorrectly(int expense, string expected)
    {
        // Arrange
        var dto = new LedgerDto { Expense = expense };

        // Assert
        dto.ExpenseDisplay.Should().Be(expected);
    }

    /// <summary>
    /// 残額の表示プロパティが正しいこと
    /// </summary>
    [Fact]
    public void LedgerDto_BalanceDisplay_ShouldFormatWithCommas()
    {
        // Arrange
        var dto = new LedgerDto { Balance = 12345 };

        // Assert
        dto.BalanceDisplay.Should().Be("12,345");
    }

    #endregion

    #region LedgerDetail → LedgerDetailDto

    /// <summary>
    /// LedgerDetailからLedgerDetailDtoへの変換が正しく行われること
    /// </summary>
    [Fact]
    public void ToDto_FromLedgerDetail_ShouldMapAllProperties()
    {
        // Arrange
        var detail = new LedgerDetail
        {
            LedgerId = 1,
            UseDate = new DateTime(2025, 1, 15, 9, 30, 0),
            EntryStation = "博多駅",
            ExitStation = "天神駅",
            Amount = 210,
            Balance = 4790,
            IsCharge = false,
            IsBus = false
        };

        // Act
        var dto = detail.ToDto();

        // Assert
        dto.LedgerId.Should().Be(1);
        dto.UseDate.Should().Be(new DateTime(2025, 1, 15, 9, 30, 0));
        dto.UseDateDisplay.Should().Be("2025/01/15 09:30");
        dto.EntryStation.Should().Be("博多駅");
        dto.ExitStation.Should().Be("天神駅");
        dto.Amount.Should().Be(210);
        dto.Balance.Should().Be(4790);
        dto.IsCharge.Should().BeFalse();
        dto.IsBus.Should().BeFalse();
    }

    /// <summary>
    /// 鉄道利用時のルート表示が正しいこと
    /// </summary>
    [Fact]
    public void LedgerDetailDto_RouteDisplay_ForRailway_ShouldShowStations()
    {
        // Arrange
        var dto = new LedgerDetailDto
        {
            EntryStation = "博多駅",
            ExitStation = "天神駅",
            IsCharge = false,
            IsBus = false
        };

        // Assert
        dto.RouteDisplay.Should().Be("博多駅～天神駅");
    }

    /// <summary>
    /// チャージ時のルート表示が正しいこと
    /// </summary>
    [Fact]
    public void LedgerDetailDto_RouteDisplay_ForCharge_ShouldShowCharge()
    {
        // Arrange
        var dto = new LedgerDetailDto { IsCharge = true };

        // Assert
        dto.RouteDisplay.Should().Be("チャージ");
    }

    /// <summary>
    /// バス利用時（バス停名あり）のルート表示が正しいこと
    /// </summary>
    [Fact]
    public void LedgerDetailDto_RouteDisplay_ForBusWithStops_ShouldShowBusStops()
    {
        // Arrange
        var dto = new LedgerDetailDto
        {
            IsBus = true,
            BusStops = "博多駅前→天神"
        };

        // Assert
        dto.RouteDisplay.Should().Be("バス（博多駅前→天神）");
    }

    /// <summary>
    /// バス利用時（バス停名なし）のルート表示が正しいこと
    /// </summary>
    [Fact]
    public void LedgerDetailDto_RouteDisplay_ForBusWithoutStops_ShouldShowStar()
    {
        // Arrange
        var dto = new LedgerDetailDto
        {
            IsBus = true,
            BusStops = null
        };

        // Assert
        dto.RouteDisplay.Should().Be("バス（★）");
    }

    #endregion

    #region AppSettings → SettingsDto

    /// <summary>
    /// AppSettingsからSettingsDtoへの変換が正しく行われること
    /// </summary>
    [Fact]
    public void ToDto_FromAppSettings_ShouldMapAllProperties()
    {
        // Arrange
        var settings = new AppSettings
        {
            WarningBalance = 3000,
            BackupPath = @"C:\Backup",
            FontSize = FontSizeOption.Large
        };

        // Act
        var dto = settings.ToDto();

        // Assert
        dto.WarningBalance.Should().Be(3000);
        dto.BackupPath.Should().Be(@"C:\Backup");
        dto.FontSize.Should().Be(FontSizeOption.Large);
    }

    /// <summary>
    /// 残額警告閾値の表示が正しいこと
    /// </summary>
    [Fact]
    public void SettingsDto_WarningBalanceDisplay_ShouldFormatWithYen()
    {
        // Arrange
        var dto = new SettingsDto { WarningBalance = 10000 };

        // Assert
        dto.WarningBalanceDisplay.Should().Be("10,000円");
    }

    /// <summary>
    /// 文字サイズの表示が正しいこと
    /// </summary>
    [Theory]
    [InlineData(FontSizeOption.Small, "小")]
    [InlineData(FontSizeOption.Medium, "中（標準）")]
    [InlineData(FontSizeOption.Large, "大")]
    [InlineData(FontSizeOption.ExtraLarge, "特大")]
    public void SettingsDto_FontSizeDisplay_ShouldShowCorrectText(FontSizeOption fontSize, string expected)
    {
        // Arrange
        var dto = new SettingsDto { FontSize = fontSize };

        // Assert
        dto.FontSizeDisplay.Should().Be(expected);
    }

    #endregion

    #region DTO → Entity（逆変換）

    /// <summary>
    /// CardDtoからIcCardへの変換が正しく行われること
    /// </summary>
    [Fact]
    public void ToEntity_FromCardDto_ShouldMapAllProperties()
    {
        // Arrange
        var dto = new CardDto
        {
            CardIdm = "07FE112233445566",
            CardType = "はやかけん",
            CardNumber = "H-001",
            Note = "テストカード",
            IsLent = true,
            LastLentStaff = "FFFF000000000001",
            LentAt = new DateTime(2025, 1, 15)
        };

        // Act
        var entity = dto.ToEntity();

        // Assert
        entity.CardIdm.Should().Be("07FE112233445566");
        entity.CardType.Should().Be("はやかけん");
        entity.CardNumber.Should().Be("H-001");
        entity.Note.Should().Be("テストカード");
        entity.IsLent.Should().BeTrue();
        entity.LastLentStaff.Should().Be("FFFF000000000001");
        entity.LastLentAt.Should().Be(new DateTime(2025, 1, 15));
    }

    /// <summary>
    /// StaffDtoからStaffへの変換が正しく行われること
    /// </summary>
    [Fact]
    public void ToEntity_FromStaffDto_ShouldMapAllProperties()
    {
        // Arrange
        var dto = new StaffDto
        {
            StaffIdm = "FFFF000000000001",
            Name = "山田太郎",
            Number = "001",
            Note = "テスト職員"
        };

        // Act
        var entity = dto.ToEntity();

        // Assert
        entity.StaffIdm.Should().Be("FFFF000000000001");
        entity.Name.Should().Be("山田太郎");
        entity.Number.Should().Be("001");
        entity.Note.Should().Be("テスト職員");
    }

    /// <summary>
    /// SettingsDtoからAppSettingsへの変換が正しく行われること
    /// </summary>
    [Fact]
    public void ToEntity_FromSettingsDto_ShouldMapAllProperties()
    {
        // Arrange
        var dto = new SettingsDto
        {
            WarningBalance = 5000,
            BackupPath = @"D:\Backup",
            FontSize = FontSizeOption.ExtraLarge
        };

        // Act
        var entity = dto.ToEntity();

        // Assert
        entity.WarningBalance.Should().Be(5000);
        entity.BackupPath.Should().Be(@"D:\Backup");
        entity.FontSize.Should().Be(FontSizeOption.ExtraLarge);
    }

    #endregion
}
