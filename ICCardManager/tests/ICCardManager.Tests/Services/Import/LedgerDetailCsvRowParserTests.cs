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
