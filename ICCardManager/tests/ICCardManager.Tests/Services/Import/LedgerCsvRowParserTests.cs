using System;
using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Services;
using ICCardManager.Services.Import.Parsers;
using Xunit;

namespace ICCardManager.Tests.Services.Import;

/// <summary>
/// <see cref="LedgerCsvRowParser"/> の単体テスト（Issue #1284 Task 8）。
/// Import / Preview で共通利用される行パース処理のエラー分岐と正常系を検証する。
/// </summary>
public class LedgerCsvRowParserTests
{
    private const string ExistingCardIdm = "0102030405060708";

    private static HashSet<string> ExistingCards() =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ExistingCardIdm };

    // 9列フォーマット: [0]日時 [1]カードIDm [2]管理番号 [3]摘要 [4]受入金額 [5]払出金額 [6]残額 [7]利用者 [8]備考
    private static List<string> ValidNineColumnFields(
        string date = "2024-01-15 10:30:00",
        string cardIdm = ExistingCardIdm,
        string summary = "鉄道（博多～天神）",
        string income = "",
        string expense = "260",
        string balance = "9740",
        string staffName = "山田太郎",
        string note = "")
    {
        return new List<string>
        {
            date, cardIdm, "001", summary, income, expense, balance, staffName, note
        };
    }

    // 10列フォーマット（ID列あり）
    private static List<string> ValidTenColumnFields(string id)
    {
        var baseFields = ValidNineColumnFields();
        var withId = new List<string> { id };
        withId.AddRange(baseFields);
        return withId;
    }

    [Fact]
    public void TryParseRow_InsufficientColumns_AddsError()
    {
        // Arrange
        var fields = new List<string> { "2024-01-15 10:30:00", ExistingCardIdm, "001" }; // 3 columns only
        var errors = new List<CsvImportError>();

        // Act
        var result = LedgerCsvRowParser.TryParseRow(
            fields, lineNumber: 2, line: "raw,line",
            hasIdColumn: false, minColumns: 9,
            ExistingCards(), targetCardIdm: null, errors);

        // Assert
        result.Should().BeNull();
        errors.Should().ContainSingle();
        errors[0].LineNumber.Should().Be(2);
        errors[0].Message.Should().Contain("列数が不足").And.Contain("9");
    }

    [Fact]
    public void TryParseRow_InvalidDate_AddsError()
    {
        // Arrange
        var fields = ValidNineColumnFields(date: "不正な日付");
        var errors = new List<CsvImportError>();

        // Act
        var result = LedgerCsvRowParser.TryParseRow(
            fields, lineNumber: 3, line: "raw",
            hasIdColumn: false, minColumns: 9,
            ExistingCards(), targetCardIdm: null, errors);

        // Assert
        result.Should().BeNull();
        errors.Should().ContainSingle();
        errors[0].Message.Should().Contain("日時").And.Contain("形式");
    }

    [Fact]
    public void TryParseRow_InvalidBalance_AddsError()
    {
        // Arrange
        var fields = ValidNineColumnFields(balance: "abc");
        var errors = new List<CsvImportError>();

        // Act
        var result = LedgerCsvRowParser.TryParseRow(
            fields, lineNumber: 4, line: "raw",
            hasIdColumn: false, minColumns: 9,
            ExistingCards(), targetCardIdm: null, errors);

        // Assert
        result.Should().BeNull();
        errors.Should().ContainSingle();
        errors[0].Message.Should().Contain("残額").And.Contain("形式");
        errors[0].Data.Should().Be("abc");
    }

    [Fact]
    public void TryParseRow_UnknownCard_AddsError()
    {
        // Arrange - 登録済みでない IDm
        var fields = ValidNineColumnFields(cardIdm: "FFFFFFFFFFFFFFFF");
        var errors = new List<CsvImportError>();

        // Act
        var result = LedgerCsvRowParser.TryParseRow(
            fields, lineNumber: 5, line: "raw",
            hasIdColumn: false, minColumns: 9,
            ExistingCards(), targetCardIdm: null, errors);

        // Assert
        result.Should().BeNull();
        errors.Should().ContainSingle();
        errors[0].Message.Should().Contain("登録されていません");
    }

    [Fact]
    public void TryParseRow_EmptyCardIdmWithTarget_UsesTarget()
    {
        // Arrange - CSV の IDm 欄が空欄だが target が渡されている
        var fields = ValidNineColumnFields(cardIdm: "");
        var errors = new List<CsvImportError>();

        // Act
        var result = LedgerCsvRowParser.TryParseRow(
            fields, lineNumber: 6, line: "raw",
            hasIdColumn: false, minColumns: 9,
            ExistingCards(), targetCardIdm: ExistingCardIdm, errors);

        // Assert
        result.Should().NotBeNull();
        errors.Should().BeEmpty();
        result.CardIdm.Should().Be(ExistingCardIdm.ToUpperInvariant());
    }

    [Fact]
    public void TryParseRow_EmptyCardIdmWithoutTarget_AddsError()
    {
        // Arrange
        var fields = ValidNineColumnFields(cardIdm: "");
        var errors = new List<CsvImportError>();

        // Act
        var result = LedgerCsvRowParser.TryParseRow(
            fields, lineNumber: 7, line: "raw",
            hasIdColumn: false, minColumns: 9,
            ExistingCards(), targetCardIdm: null, errors);

        // Assert
        result.Should().BeNull();
        errors.Should().ContainSingle();
        errors[0].Message.Should().Contain("カードIDmは必須");
    }

    [Fact]
    public void TryParseRow_EmptySummary_AddsError()
    {
        // Arrange
        var fields = ValidNineColumnFields(summary: "   ");
        var errors = new List<CsvImportError>();

        // Act
        var result = LedgerCsvRowParser.TryParseRow(
            fields, lineNumber: 8, line: "raw",
            hasIdColumn: false, minColumns: 9,
            ExistingCards(), targetCardIdm: null, errors);

        // Assert
        result.Should().BeNull();
        errors.Should().ContainSingle();
        errors[0].Message.Should().Contain("摘要").And.Contain("必須");
    }

    [Fact]
    public void TryParseRow_ValidRow_ReturnsParsed()
    {
        // Arrange
        var fields = ValidNineColumnFields(
            date: "2024-02-20 09:00:00",
            summary: "鉄道（博多～天神）",
            income: "",
            expense: "260",
            balance: "9740",
            staffName: "佐藤花子",
            note: "備考メモ");
        var errors = new List<CsvImportError>();

        // Act
        var result = LedgerCsvRowParser.TryParseRow(
            fields, lineNumber: 10, line: "raw",
            hasIdColumn: false, minColumns: 9,
            ExistingCards(), targetCardIdm: null, errors);

        // Assert
        result.Should().NotBeNull();
        errors.Should().BeEmpty();
        result.LineNumber.Should().Be(10);
        result.LedgerId.Should().BeNull();
        result.CardIdm.Should().Be(ExistingCardIdm.ToUpperInvariant());
        result.Date.Should().Be(new DateTime(2024, 2, 20, 9, 0, 0));
        result.Summary.Should().Be("鉄道（博多～天神）");
        result.Income.Should().Be(0);
        result.Expense.Should().Be(260);
        result.Balance.Should().Be(9740);
        result.StaffName.Should().Be("佐藤花子");
        result.Note.Should().Be("備考メモ");
    }

    [Fact]
    public void TryParseRow_WithIdColumnValid_ParsesLedgerId()
    {
        // Arrange - ID列あり、有効な ID "42"
        var fields = ValidTenColumnFields("42");
        var errors = new List<CsvImportError>();

        // Act
        var result = LedgerCsvRowParser.TryParseRow(
            fields, lineNumber: 11, line: "raw",
            hasIdColumn: true, minColumns: 10,
            ExistingCards(), targetCardIdm: null, errors);

        // Assert
        result.Should().NotBeNull();
        errors.Should().BeEmpty();
        result.LedgerId.Should().Be(42);
        result.CardIdm.Should().Be(ExistingCardIdm.ToUpperInvariant());
    }

    [Fact]
    public void TryParseRow_WithIdColumnInvalidId_AddsError()
    {
        // Arrange - ID 列が "abc"（整数パース不可）
        var fields = ValidTenColumnFields("abc");
        var errors = new List<CsvImportError>();

        // Act
        var result = LedgerCsvRowParser.TryParseRow(
            fields, lineNumber: 12, line: "raw",
            hasIdColumn: true, minColumns: 10,
            ExistingCards(), targetCardIdm: null, errors);

        // Assert
        result.Should().BeNull();
        errors.Should().ContainSingle();
        errors[0].Message.Should().Contain("ID").And.Contain("形式");
        errors[0].Data.Should().Be("abc");
    }
}
