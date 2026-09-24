using FluentAssertions;
using ICCardManager.Dtos;
using ICCardManager.Models;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;


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
        // Issue #2106: 旧テストは紙出納簿移行（Issue #510 / #1215）の 4 項目と払戻の 2 項目を
        // 既定値のまま渡しており、写像を消しても緑だった。全項目に既定値と異なる値を入れる
        var card = new IcCard
        {
            CardIdm = "07FE112233445566",
            CardType = "はやかけん",
            CardNumber = "H-001",
            Note = "テストカード",
            IsLent = true,
            LastLentStaff = "FFFF000000000001",
            LastLentAt = new DateTime(2025, 1, 15, 10, 30, 0),
            StartingPageNumber = 7,
            CarryoverIncomeTotal = 12000,
            CarryoverExpenseTotal = 8350,
            CarryoverFiscalYear = 2024,
            IsRefunded = true,
            RefundedAt = new DateTime(2025, 3, 31, 17, 0, 0)
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
        dto.StartingPageNumber.Should().Be(7, "既定値 1 と異なる値で写像を確かめる");
        dto.CarryoverIncomeTotal.Should().Be(12000);
        dto.CarryoverExpenseTotal.Should().Be(8350);
        dto.CarryoverFiscalYear.Should().Be(2024);
        dto.IsRefunded.Should().BeTrue();
        dto.RefundedAt.Should().Be(new DateTime(2025, 3, 31, 17, 0, 0));
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
            // Issue #2106: 受入・貸出中フラグも既定値（0 / false）と異なる値にして写像を確かめる
            Income = 500,
            Expense = 210,
            Balance = 4790,
            StaffName = "山田太郎",
            CompanionCount = 1, // Issue #1906
            Note = "通勤利用",
            IsLentRecord = true,
            Details = new List<LedgerDetail>
            {
                new LedgerDetail { LedgerId = 1, EntryStation = "博多駅", ExitStation = "天神駅", Amount = 210 }
            }
        };

        // Act
        var dto = ledger.ToDto();

        // Assert
        dto.Id.Should().Be(1);
        dto.CompanionCount.Should().Be(1);
        dto.StaffName.Should().Be("山田太郎", "DTO の StaffName は生の氏名のまま");
        dto.DisplayStaffName.Should().Be("山田太郎 外1名", "Issue #1906: 一覧の利用者列は DisplayStaffName で導出");
        dto.CardIdm.Should().Be("07FE112233445566");
        dto.Date.Should().Be(new DateTime(2025, 1, 15));
        dto.DateDisplay.Should().Contain("R7"); // 和暦変換される（短縮形式: R7.1.15）
        dto.Summary.Should().Be("鉄道（博多駅～天神駅）");
        dto.Income.Should().Be(500);
        dto.Expense.Should().Be(210);
        dto.Balance.Should().Be(4790);
        dto.StaffName.Should().Be("山田太郎");
        dto.Note.Should().Be("通勤利用");
        dto.IsLentRecord.Should().BeTrue();
        dto.Details.Should().ContainSingle();
        dto.Details[0].EntryStation.Should().Be("博多駅", "明細も DTO へ写す");
        dto.Details[0].ExitStation.Should().Be("天神駅");
        dto.Details[0].Amount.Should().Be(210);
        dto.DetailCountValue.Should().Be(1, "Details があるときは Details の件数を使う");
    }

    /// <summary>
    /// 受入金額の表示プロパティが正しいこと
    /// </summary>
    [Theory]
    [InlineData(1000, "1,000")]
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
    [InlineData(210, "210")]
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
        // Issue #2106: 旧テストは BusStops を設定せず、3 つのフラグも false（既定値）のままだったため、
        // DtoMapper の IsCharge / IsBus / IsPointRedemption の写像を消しても緑だった。
        // ここでは値の写像を、フラグの写像は下の Theory で 1 つずつ立てて確かめる
        var detail = new LedgerDetail
        {
            LedgerId = 1,
            UseDate = new DateTime(2025, 1, 15, 9, 30, 0),
            EntryStation = "博多駅",
            ExitStation = "天神駅",
            BusStops = "天神～博多駅前",
            Amount = 210,
            Balance = 4790,
            IsCharge = false,
            IsPointRedemption = true,
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
        dto.BusStops.Should().Be("天神～博多駅前");
        dto.Amount.Should().Be(210);
        dto.Balance.Should().Be(4790);
        dto.IsCharge.Should().BeFalse();
        dto.IsPointRedemption.Should().BeTrue();
        dto.IsBus.Should().BeFalse();
    }

    /// <summary>
    /// Issue #2106: 3 つのフラグ（チャージ／ポイント還元／バス利用）がそれぞれ自分のプロパティへ写ること。
    /// </summary>
    /// <remarks>
    /// 1 つだけを立てた明細を与え、DTO で立っているのがそのフラグだけであることを表明する（取り違えも検出する）。
    /// <see cref="LedgerDetailDto.RouteDisplay"/> は写像されたフラグで表示を切り替えるため、
    /// ポイント還元の写像が消えると明細画面でポイント還元が鉄道利用として表示される。
    /// </remarks>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ToDto_FromLedgerDetail_EachFlagMapsToItsOwnProperty(bool isCharge, bool isPointRedemption, bool isBus)
    {
        var detail = new LedgerDetail
        {
            LedgerId = 1,
            Amount = 240,
            Balance = 1000,
            IsCharge = isCharge,
            IsPointRedemption = isPointRedemption,
            IsBus = isBus
        };

        var dto = detail.ToDto();

        dto.IsCharge.Should().Be(isCharge);
        dto.IsPointRedemption.Should().Be(isPointRedemption);
        dto.IsBus.Should().Be(isBus);
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
            LentAt = new DateTime(2025, 1, 15),
            // Issue #2106: 繰越・開始ページ・払戻の各項目も既定値と異なる値で渡す。
            // 逆変換で落ちると、カード更新時に紙出納簿移行カードの繰越累計が既定値へ戻る（#1726 と同じ害）
            StartingPageNumber = 7,
            CarryoverIncomeTotal = 12000,
            CarryoverExpenseTotal = 8350,
            CarryoverFiscalYear = 2024,
            IsRefunded = true,
            RefundedAt = new DateTime(2025, 3, 31, 17, 0, 0)
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
        entity.StartingPageNumber.Should().Be(7, "既定値 1 と異なる値で写像を確かめる");
        entity.CarryoverIncomeTotal.Should().Be(12000);
        entity.CarryoverExpenseTotal.Should().Be(8350);
        entity.CarryoverFiscalYear.Should().Be(2024);
        entity.IsRefunded.Should().BeTrue();
        entity.RefundedAt.Should().Be(new DateTime(2025, 3, 31, 17, 0, 0));
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

    #region 写像の網羅（リフレクション、Issue #2106）

    // Issue #2106: 上の「ShouldMapAllProperties」は項目を手で列挙するため、モデルと DTO の両方へ
    // プロパティを足して DtoMapper へ書き足し忘れても緑のまま通る（LedgerClonerCoverageTests #1959 と同じ形）。
    // 写像先の書き込み可能プロパティをリフレクションで列挙し、写像元の同名（または改名表にある）プロパティに
    // 「写像先の既定値と異なる値」を入れて、写像先へ届くことを確かめる。
    //
    // - 対応するプロパティの集合はリテラルで固定する（リフレクションの誤りで走査が 0 件に縮んだとき
    //   緑のまま無力化しないように。プロパティを足したら、ここで写像するかどうかを明示的に決める）
    // - 写像先にしか無いプロパティ（画面の状態・引数で渡す値・表示用の導出値）は理由付きで列挙する
    // - bool は全部を true にすると取り違え（IsCharge ← IsBus 等）を検出できないため、1 つずつ立てて走査する
    // - 未対応の型は例外にする（fail-open にしない、#1944）

    private const string UiStateReason = "画面の状態（選択・チェック・強調・警告表示）で、エンティティには無い";

    [Fact]
    public void ToDto_FromIcCard_MapsEveryCounterpartProperty()
    {
        AssertEveryCounterpartIsMapped<IcCard, CardDto>(
            c => c.ToDto(),
            expectedCounterparts: new[]
            {
                nameof(CardDto.CardIdm), nameof(CardDto.CardType), nameof(CardDto.CardNumber), nameof(CardDto.Note),
                nameof(CardDto.IsLent), nameof(CardDto.LastLentStaff), nameof(CardDto.LentAt),
                nameof(CardDto.StartingPageNumber), nameof(CardDto.CarryoverIncomeTotal),
                nameof(CardDto.CarryoverExpenseTotal), nameof(CardDto.CarryoverFiscalYear),
                nameof(CardDto.IsRefunded), nameof(CardDto.RefundedAt)
            },
            renamedFrom: new Dictionary<string, string> { [nameof(CardDto.LentAt)] = nameof(IcCard.LastLentAt) },
            targetOnly: new Dictionary<string, string>
            {
                [nameof(CardDto.IsSelected)] = UiStateReason,
                [nameof(CardDto.LentStaffName)] = "ToDto の引数 staffName で渡す（上の個別テストで検証）",
                [nameof(CardDto.ExportState)] = "帳票の出力状況で、ReportExportStatusService が設定する",
                [nameof(CardDto.ExportLastWriteTime)] = "帳票の出力状況で、ReportExportStatusService が設定する",
                [nameof(CardDto.PreflightWarningCount)] = "帳票の事前チェック結果で、帳票画面が設定する"
            });
    }

    [Fact]
    public void ToEntity_FromCardDto_MapsEveryCounterpartProperty()
    {
        AssertEveryCounterpartIsMapped<CardDto, IcCard>(
            d => d.ToEntity(),
            expectedCounterparts: new[]
            {
                nameof(IcCard.CardIdm), nameof(IcCard.CardType), nameof(IcCard.CardNumber), nameof(IcCard.Note),
                nameof(IcCard.IsLent), nameof(IcCard.LastLentStaff), nameof(IcCard.LastLentAt),
                nameof(IcCard.StartingPageNumber), nameof(IcCard.CarryoverIncomeTotal),
                nameof(IcCard.CarryoverExpenseTotal), nameof(IcCard.CarryoverFiscalYear),
                nameof(IcCard.IsRefunded), nameof(IcCard.RefundedAt)
            },
            renamedFrom: new Dictionary<string, string> { [nameof(IcCard.LastLentAt)] = nameof(CardDto.LentAt) },
            targetOnly: new Dictionary<string, string>
            {
                [nameof(IcCard.IsDeleted)] = "CardDto は削除状態を持たない（論理削除は専用のリポジトリ操作で行う）",
                [nameof(IcCard.DeletedAt)] = "CardDto は削除状態を持たない（論理削除は専用のリポジトリ操作で行う）"
            });
    }

    [Fact]
    public void ToDto_FromStaff_MapsEveryCounterpartProperty()
    {
        AssertEveryCounterpartIsMapped<Staff, StaffDto>(
            s => s.ToDto(),
            expectedCounterparts: new[]
            {
                nameof(StaffDto.StaffIdm), nameof(StaffDto.Name), nameof(StaffDto.Number), nameof(StaffDto.Note)
            },
            targetOnly: new Dictionary<string, string>());
    }

    [Fact]
    public void ToEntity_FromStaffDto_MapsEveryCounterpartProperty()
    {
        AssertEveryCounterpartIsMapped<StaffDto, Staff>(
            d => d.ToEntity(),
            expectedCounterparts: new[]
            {
                nameof(Staff.StaffIdm), nameof(Staff.Name), nameof(Staff.Number), nameof(Staff.Note)
            },
            targetOnly: new Dictionary<string, string>
            {
                [nameof(Staff.IsDeleted)] = "StaffDto は削除状態を持たない（論理削除は専用のリポジトリ操作で行う）",
                [nameof(Staff.DeletedAt)] = "StaffDto は削除状態を持たない（論理削除は専用のリポジトリ操作で行う）"
            });
    }

    [Fact]
    public void ToDto_FromLedger_MapsEveryCounterpartProperty()
    {
        AssertEveryCounterpartIsMapped<Ledger, LedgerDto>(
            l => l.ToDto(),
            expectedCounterparts: new[]
            {
                nameof(LedgerDto.Id), nameof(LedgerDto.CardIdm), nameof(LedgerDto.Date), nameof(LedgerDto.Summary),
                nameof(LedgerDto.Income), nameof(LedgerDto.Expense), nameof(LedgerDto.Balance),
                nameof(LedgerDto.StaffName), nameof(LedgerDto.CompanionCount), nameof(LedgerDto.Note),
                nameof(LedgerDto.IsLentRecord), nameof(LedgerDto.DetailCountValue)
            },
            // Details が空のときは DetailCount（明細を読み込まない一覧取得で設定される件数）を使う
            renamedFrom: new Dictionary<string, string> { [nameof(LedgerDto.DetailCountValue)] = nameof(Ledger.DetailCount) },
            verifiedSeparately: new Dictionary<string, string>
            {
                [nameof(LedgerDto.Details)] = "要素型が異なる（LedgerDetail → LedgerDetailDto）。ToDto_FromLedger_ShouldMapAllProperties で中身を検証する"
            },
            targetOnly: new Dictionary<string, string>
            {
                [nameof(LedgerDto.DateDisplay)] = "Date から導出する和暦表示（ToDto_FromLedger_ShouldMapAllProperties で検証）",
                [nameof(LedgerDto.IsChecked)] = UiStateReason,
                [nameof(LedgerDto.IsRecentlyRecorded)] = UiStateReason,
                [nameof(LedgerDto.IsCarryoverRow)] = "履歴画面が合成する繰越行の目印で、DB の行には無い",
                [nameof(LedgerDto.HasBalanceInconsistency)] = UiStateReason,
                [nameof(LedgerDto.BalanceInconsistencyMessage)] = UiStateReason
            });
    }

    [Fact]
    public void ToDto_FromLedgerDetail_MapsEveryCounterpartProperty()
    {
        AssertEveryCounterpartIsMapped<LedgerDetail, LedgerDetailDto>(
            d => d.ToDto(),
            expectedCounterparts: new[]
            {
                nameof(LedgerDetailDto.LedgerId), nameof(LedgerDetailDto.UseDate),
                nameof(LedgerDetailDto.EntryStation), nameof(LedgerDetailDto.ExitStation), nameof(LedgerDetailDto.BusStops),
                nameof(LedgerDetailDto.Amount), nameof(LedgerDetailDto.Balance),
                nameof(LedgerDetailDto.IsCharge), nameof(LedgerDetailDto.IsPointRedemption), nameof(LedgerDetailDto.IsBus)
            },
            targetOnly: new Dictionary<string, string>
            {
                [nameof(LedgerDetailDto.UseDateDisplay)] = "UseDate から導出する表示文字列（ToDto_FromLedgerDetail_ShouldMapAllProperties で検証）"
            });
    }

    [Fact]
    public void ToDto_FromAppSettings_MapsEveryCounterpartProperty()
    {
        AssertEveryCounterpartIsMapped<AppSettings, SettingsDto>(
            s => s.ToDto(),
            expectedCounterparts: new[]
            {
                nameof(SettingsDto.WarningBalance), nameof(SettingsDto.BackupPath), nameof(SettingsDto.FontSize)
            },
            targetOnly: new Dictionary<string, string>());
    }

    [Fact]
    public void ToEntity_FromSettingsDto_MapsEveryCounterpartProperty()
    {
        // AppSettings には SettingsDto に無い項目が多数ある（SettingsDto は画面が編集する 3 項目だけを持つ）ため、
        // 写像先にしか無いプロパティの列挙は省く（targetOnly: null）
        AssertEveryCounterpartIsMapped<SettingsDto, AppSettings>(
            d => d.ToEntity(),
            expectedCounterparts: new[]
            {
                nameof(AppSettings.WarningBalance), nameof(AppSettings.BackupPath), nameof(AppSettings.FontSize)
            },
            targetOnly: null);
    }

    /// <summary>
    /// 写像先 <typeparamref name="TTarget"/> の書き込み可能プロパティのうち、写像元 <typeparamref name="TSource"/> に
    /// 対応するものが、すべて写像元の値を受け取ることを表明する。
    /// </summary>
    /// <param name="map">検査対象の写像</param>
    /// <param name="expectedCounterparts">対応するプロパティ（写像先の名前）。リテラルで固定する</param>
    /// <param name="renamedFrom">写像先の名前 → 写像元の名前（名前が異なるもの）</param>
    /// <param name="verifiedSeparately">写像先の名前 → 一括走査から外して個別に検証する理由</param>
    /// <param name="targetOnly">写像先にしか無いプロパティ → その理由。<c>null</c> なら列挙を検査しない</param>
    private static void AssertEveryCounterpartIsMapped<TSource, TTarget>(
        Func<TSource, TTarget> map,
        IReadOnlyCollection<string> expectedCounterparts,
        IReadOnlyDictionary<string, string>? renamedFrom = null,
        IReadOnlyDictionary<string, string>? verifiedSeparately = null,
        IReadOnlyDictionary<string, string>? targetOnly = null)
        where TSource : new()
        where TTarget : new()
    {
        renamedFrom ??= new Dictionary<string, string>();
        verifiedSeparately ??= new Dictionary<string, string>();

        var targetProperties = typeof(TTarget)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetSetMethod() != null && p.GetIndexParameters().Length == 0)
            .ToList();

        foreach (var name in verifiedSeparately.Keys)
        {
            targetProperties.Should().Contain(p => p.Name == name,
                $"個別検証の表に、写像先に存在しないプロパティ（{name}）を残さない");
        }

        var pairs = new List<(PropertyInfo Source, PropertyInfo Target)>();
        var targetOnlyActual = new List<string>();
        foreach (var target in targetProperties)
        {
            if (verifiedSeparately.ContainsKey(target.Name))
            {
                continue;
            }

            var sourceName = renamedFrom.TryGetValue(target.Name, out var renamed) ? renamed : target.Name;
            var source = typeof(TSource).GetProperty(sourceName, BindingFlags.Public | BindingFlags.Instance);
            if (source == null)
            {
                targetOnlyActual.Add(target.Name);
            }
            else
            {
                pairs.Add((source, target));
            }
        }

        pairs.Select(p => p.Target.Name).Should().BeEquivalentTo(expectedCounterparts,
            $"{typeof(TSource).Name} → {typeof(TTarget).Name} で写像するプロパティの集合（足したら写像するかを明示的に決める）");
        if (targetOnly != null)
        {
            targetOnlyActual.Should().BeEquivalentTo(targetOnly.Keys,
                $"{typeof(TTarget).Name} にしか無いプロパティは理由付きで列挙する");
        }

        foreach (var (source, target) in pairs)
        {
            source.CanWrite.Should().BeTrue($"写像元 {typeof(TSource).Name}.{source.Name} に値を入れて検査するため");
            source.PropertyType.Should().Be(target.PropertyType,
                $"{typeof(TSource).Name}.{source.Name} と {typeof(TTarget).Name}.{target.Name} は同じ型で写す（異なるなら verifiedSeparately へ）");
        }

        // bool は 1 つずつ立てる（全部 true だと取り違えを検出できない）。bool が無ければ 1 回だけ走査する
        var booleans = pairs.Where(p => p.Source.PropertyType == typeof(bool)).Select(p => p.Source.Name).ToList();
        var runs = booleans.Count == 0 ? new List<string?> { null } : booleans.Select(b => (string?)b).ToList();
        var freshTarget = new TTarget();

        foreach (var raisedBoolean in runs)
        {
            var sourceInstance = new TSource();
            var index = 0;
            foreach (var (source, _) in pairs)
            {
                source.SetValue(sourceInstance, SampleValue(source, index++, raisedBoolean));
            }

            var mapped = map(sourceInstance);

            foreach (var (source, target) in pairs)
            {
                var expected = source.GetValue(sourceInstance);
                if (source.PropertyType != typeof(bool))
                {
                    expected.Should().NotBe(target.GetValue(freshTarget),
                        $"{target.Name} は写像先の既定値と異なる値で検査する（既定値のままでは写像の欠落が見えない）");
                }

                target.GetValue(mapped).Should().Be(expected,
                    $"{typeof(TTarget).Name}.{target.Name} は {typeof(TSource).Name}.{source.Name} から写す" +
                    (raisedBoolean == null ? string.Empty : $"（{raisedBoolean} だけを true にした走査）"));
            }
        }
    }

    /// <summary>
    /// プロパティごとに異なる、型の既定値ではない見本値を返す。未対応の型は例外にする（fail-open にしない）。
    /// </summary>
    private static object? SampleValue(PropertyInfo property, int index, string? raisedBoolean)
    {
        var type = property.PropertyType;
        if (type == typeof(bool))
        {
            return property.Name == raisedBoolean;
        }
        if (type == typeof(string))
        {
            return $"見本値{index}_{property.Name}";
        }
        if (type == typeof(int))
        {
            return 1000 + index;
        }
        if (type == typeof(int?))
        {
            return (int?)(2000 + index);
        }
        if (type == typeof(DateTime))
        {
            return new DateTime(2023, 4, 1, 9, 0, 0).AddDays(index).AddMinutes(index);
        }
        if (type == typeof(DateTime?))
        {
            return (DateTime?)new DateTime(2024, 5, 1, 10, 0, 0).AddDays(index).AddMinutes(index);
        }
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return values.GetValue(values.Length - 1);
        }

        throw new InvalidOperationException(
            $"{property.DeclaringType?.Name}.{property.Name} の型 {type.Name} に見本値がありません。" +
            "SampleValue に型を足すか、verifiedSeparately で個別検証へ回してください。");
    }

    #endregion
}
