using System;
using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Services;
using ICCardManager.Services.Import.Parsers;
using Xunit;

namespace ICCardManager.Tests.Services.Import;

/// <summary>
/// <see cref="LedgerDetailCsvRowParser"/> の単体テスト（Issue #1284 Task 8）。
/// Detail CSV の 13 列（利用履歴ID / 利用日時 / カードIDm / 管理番号 / 乗車駅 / 降車駅 /
/// バス停 / 金額 / 残額 / チャージ / ポイント還元 / バス利用 / グループID）の
/// パース処理と <see cref="LedgerDetailCsvRowParser.ValidateBooleanField"/> の挙動を検証する。
/// </summary>
public class LedgerDetailCsvRowParserTests
{
    // 13 列 LedgerDetail CSV の正常行を生成するヘルパ
    private static List<string> ValidThirteenColumnFields(
        string ledgerId = "1",
        string useDate = "2024-01-15 10:30:00",
        string cardIdm = "0102030405060708",
        string managedNumber = "001",
        string entryStation = "博多",
        string exitStation = "天神",
        string busStops = "",
        string amount = "260",
        string balance = "9740",
        string isCharge = "0",
        string isPointRedemption = "0",
        string isBus = "0",
        string groupId = "")
    {
        return new List<string>
        {
            ledgerId, useDate, cardIdm, managedNumber,
            entryStation, exitStation, busStops,
            amount, balance,
            isCharge, isPointRedemption, isBus,
            groupId
        };
    }

    [Fact]
    public void ValidateBooleanField_ZeroString_ReturnsFalse()
    {
        // Arrange - "0" は false として扱われる
        var errors = new List<CsvImportError>();

        // Act
        var ok = LedgerDetailCsvRowParser.ValidateBooleanField(
            "0", lineNumber: 2, fieldName: "チャージ", errors, out var result);

        // Assert
        ok.Should().BeTrue();
        result.Should().BeFalse();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateBooleanField_OneString_ReturnsTrue()
    {
        // Arrange - "1" は true として扱われる
        var errors = new List<CsvImportError>();

        // Act
        var ok = LedgerDetailCsvRowParser.ValidateBooleanField(
            "1", lineNumber: 3, fieldName: "チャージ", errors, out var result);

        // Assert
        ok.Should().BeTrue();
        result.Should().BeTrue();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateBooleanField_EmptyString_AddsError()
    {
        // Arrange - 空欄は受け付けず、エラーになる（"0"/"1" のみが有効）
        var errors = new List<CsvImportError>();

        // Act
        var ok = LedgerDetailCsvRowParser.ValidateBooleanField(
            "", lineNumber: 4, fieldName: "チャージ", errors, out var result);

        // Assert
        ok.Should().BeFalse();
        result.Should().BeFalse();
        errors.Should().ContainSingle();
        errors[0].Message.Should().Contain("チャージ").And.Contain("0または1");
    }

    [Fact]
    public void ValidateBooleanField_InvalidString_AddsError()
    {
        // Arrange - "yes" など想定外の値はエラー
        var errors = new List<CsvImportError>();

        // Act
        var ok = LedgerDetailCsvRowParser.ValidateBooleanField(
            "yes", lineNumber: 5, fieldName: "バス利用", errors, out var result);

        // Assert
        ok.Should().BeFalse();
        result.Should().BeFalse();
        errors.Should().ContainSingle();
        errors[0].Message.Should().Contain("バス利用").And.Contain("0または1");
        errors[0].Data.Should().Be("yes");
    }

    [Fact]
    public void ParseFields_ValidRow_ParsesCorrectly()
    {
        // Arrange - 13 列の正常 CSV 行
        var fields = ValidThirteenColumnFields(
            ledgerId: "1",
            useDate: "2024-01-15 10:30:00",
            cardIdm: "0102030405060708",
            entryStation: "博多",
            exitStation: "天神",
            busStops: "",
            amount: "260",
            balance: "9740",
            isCharge: "0",
            isPointRedemption: "0",
            isBus: "0",
            groupId: "");
        var errors = new List<CsvImportError>();

        // Act
        var detail = LedgerDetailCsvRowParser.ParseFields(
            fields, lineNumber: 2, line: "raw", errors);

        // Assert
        detail.Should().NotBeNull();
        errors.Should().BeEmpty();
        detail.LedgerId.Should().Be(1);
        detail.UseDate.Should().Be(new DateTime(2024, 1, 15, 10, 30, 0));
        detail.EntryStation.Should().Be("博多");
        detail.ExitStation.Should().Be("天神");
        detail.BusStops.Should().BeNull(); // 空欄は null に正規化
        detail.Amount.Should().Be(260);
        detail.Balance.Should().Be(9740);
        detail.IsCharge.Should().BeFalse();
        detail.IsPointRedemption.Should().BeFalse();
        detail.IsBus.Should().BeFalse();
        detail.GroupId.Should().BeNull();
    }

    /// <summary>
    /// Issue #2106: 3 つのフラグ列（[9]チャージ / [10]ポイント還元 / [11]バス利用）がそれぞれ自分の列から読まれること。
    /// </summary>
    /// <remarks>
    /// 旧テストは 3 列すべてが "0" だったため、列の添字を取り違えても（例: ポイント還元を [11] から読む）緑だった。
    /// 1 列だけを "1" にした行を列ごとに与え、立ったフラグが 1 つだけで、それが対応するプロパティであることを表明する。
    /// ポイント還元の写像が壊れると、明細画面・摘要でポイント還元が鉄道利用として扱われる。
    /// </remarks>
    [Theory]
    [InlineData("1", "0", "0", true, false, false)]
    [InlineData("0", "1", "0", false, true, false)]
    [InlineData("0", "0", "1", false, false, true)]
    public void ParseFields_EachFlagColumn_MapsToItsOwnProperty(
        string isChargeText, string isPointRedemptionText, string isBusText,
        bool expectedIsCharge, bool expectedIsPointRedemption, bool expectedIsBus)
    {
        var fields = ValidThirteenColumnFields(
            isCharge: isChargeText,
            isPointRedemption: isPointRedemptionText,
            isBus: isBusText);
        var errors = new List<CsvImportError>();

        var detail = LedgerDetailCsvRowParser.ParseFields(fields, lineNumber: 2, line: "raw", errors);

        errors.Should().BeEmpty();
        detail.Should().NotBeNull();
        detail!.IsCharge.Should().Be(expectedIsCharge, "チャージは [9] 列から読む");
        detail.IsPointRedemption.Should().Be(expectedIsPointRedemption, "ポイント還元は [10] 列から読む");
        detail.IsBus.Should().Be(expectedIsBus, "バス利用は [11] 列から読む");
    }

    /// <summary>
    /// Issue #2106: フラグ列の検証エラーは、実際に不正な値を持つ列の名前で報告されること（列名の取り違えを検出する）。
    /// </summary>
    [Theory]
    [InlineData(9, "チャージ")]
    [InlineData(10, "ポイント還元")]
    [InlineData(11, "バス利用")]
    public void ParseFields_InvalidFlagColumn_ReportsThatColumnName(int columnIndex, string expectedFieldName)
    {
        var fields = ValidThirteenColumnFields();
        fields[columnIndex] = "x";
        var errors = new List<CsvImportError>();

        var detail = LedgerDetailCsvRowParser.ParseFields(fields, lineNumber: 7, line: "raw", errors);

        detail.Should().BeNull();
        errors.Should().ContainSingle();
        errors[0].LineNumber.Should().Be(7);
        errors[0].Message.Should().StartWith(expectedFieldName);
        errors[0].Data.Should().Be("x");
    }

    /// <summary>
    /// Issue #2106: バス停列 [6] とグループID列 [12] が値を持つ行で、それぞれのプロパティへ写ること
    /// （旧テストはどちらも空欄で、null への正規化しか見ていなかった）。
    /// </summary>
    [Fact]
    public void ParseFields_BusRowWithGroupId_MapsBusStopsAndGroupId()
    {
        var fields = ValidThirteenColumnFields(
            entryStation: "",
            exitStation: "",
            busStops: "天神～博多駅前",
            amount: "210",
            balance: "9530",
            isBus: "1",
            groupId: "3");
        var errors = new List<CsvImportError>();

        var detail = LedgerDetailCsvRowParser.ParseFields(fields, lineNumber: 2, line: "raw", errors);

        errors.Should().BeEmpty();
        detail.Should().NotBeNull();
        detail!.EntryStation.Should().BeNull();
        detail.ExitStation.Should().BeNull();
        detail.BusStops.Should().Be("天神～博多駅前");
        detail.Amount.Should().Be(210);
        detail.Balance.Should().Be(9530);
        detail.IsBus.Should().BeTrue();
        detail.IsCharge.Should().BeFalse();
        detail.IsPointRedemption.Should().BeFalse();
        detail.GroupId.Should().Be(3);
    }

    [Fact]
    public void ParseFields_InvalidBalance_AddsError()
    {
        // Arrange - 残額フィールドが "abc"
        var fields = ValidThirteenColumnFields(balance: "abc");
        var errors = new List<CsvImportError>();

        // Act
        var detail = LedgerDetailCsvRowParser.ParseFields(
            fields, lineNumber: 6, line: "raw", errors);

        // Assert
        detail.Should().BeNull();
        errors.Should().ContainSingle();
        errors[0].Message.Should().Contain("残額").And.Contain("形式");
        errors[0].Data.Should().Be("abc");
    }
}
