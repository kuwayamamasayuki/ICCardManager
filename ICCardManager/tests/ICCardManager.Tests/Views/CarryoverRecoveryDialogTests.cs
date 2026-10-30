using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Tests.Views.Helpers;
using ICCardManager.Views.Dialogs;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2255: 繰越情報の復旧ダイアログの、Window を実体化せずに確かめられる判断。
/// </summary>
public class CarryoverRecoveryDialogTests
{
    [Theory]
    [InlineData(7, 45000, 37500, 2025, CarryoverInputField.StartingPageNumber)]
    [InlineData(null, 45000, 37500, 2025, CarryoverInputField.CarryoverIncomeTotal)]
    [InlineData(null, null, 37500, 2025, CarryoverInputField.CarryoverExpenseTotal)]
    [InlineData(null, null, null, 2025, CarryoverInputField.CarryoverFiscalYear)]
    public void ResolveInitialFocusField_失われた項目のうち画面の上にある欄へ最初のフォーカスを置くこと(
        int? lostPage, int? lostIncome, int? lostExpense, int? lostYear, CarryoverInputField expected)
    {
        // 失われていない欄から始めると、職員は確かめるべき値を探して Tab を押すことになる
        var target = new CarryoverDataLossItem
        {
            LostStartingPageNumber = lostPage,
            LostCarryoverIncomeTotal = lostIncome,
            LostCarryoverExpenseTotal = lostExpense,
            LostCarryoverFiscalYear = lostYear,
        };

        CarryoverRecoveryDialog.ResolveInitialFocusField(target).Should().Be(expected);
    }

    /// <summary>今年度（テストの固定値）</summary>
    private const int CurrentFiscalYear = 2026;

    /// <summary>
    /// Issue #2331: 対象年度欄の読み上げ用説明（HelpText）が、空欄を拒む条件をすべて述べること。
    /// </summary>
    /// <remarks>
    /// 旧文言「繰越累計が0円なら空欄でもよい」は <see cref="CarryoverInfoInput.Parse"/> より緩く、
    /// 年度が失われたカード・現在の値として年度を持つカードでは、読み上げソフトの利用者だけが案内どおりに空欄にして
    /// 保存時に拒まれていた。各行で「その条件で Parse が実際に空欄を拒むこと」と「説明がその条件を述べること」を
    /// 併せて表明し、説明と検証規則の片方だけが変わったときに赤になるようにする。
    /// </remarks>
    [Theory]
    [InlineData("この欄が失われた項目のとき", true, null, "0", "0")]
    [InlineData("現在の値（年度）があるとき", false, 2024, "0", "0")]
    // 「繰越累計」は受入・払出の両方を指す（Parse はどちらかが 1 円以上なら空欄を拒む）
    [InlineData("繰越累計が1円以上", false, null, "45000", "0")]
    [InlineData("繰越累計が1円以上", false, null, "0", "38000")]
    public void 対象年度欄の読み上げ用説明が_空欄を拒む条件を述べること(
        string phrase, bool yearLost, int? currentYear, string incomeText, string expenseText)
    {
        var lost = new CarryoverDataLossItem
        {
            CardIdm = "07FE112233445566",
            CardDisplayName = "はやかけん 001",
            LostStartingPageNumber = 7,
            LostCarryoverFiscalYear = yearLost ? 2025 : null,
        };

        var result = CarryoverInfoInput.Parse(
            "7", incomeText, expenseText, "", lost, new CarryoverInfo(1, 0, 0, currentYear), CurrentFiscalYear);

        result.ErrorField.Should().Be(CarryoverInputField.CarryoverFiscalYear,
            "この条件では保存時に空欄が拒まれること（説明が述べる条件の前提）");
        ReadFiscalYearHelpText().Should().Contain(phrase,
            "保存時に空欄を拒む条件は、読み上げ用の説明でも事前に伝えること");
    }

    /// <summary>
    /// Issue #2331: どの条件にも当たらなければ空欄は受け付けられるので、説明も空欄を無条件に禁じない（正当側）。
    /// </summary>
    [Fact]
    public void 対象年度欄の読み上げ用説明が_空欄を無条件に禁じたり許したりしないこと()
    {
        var lost = new CarryoverDataLossItem
        {
            CardIdm = "07FE112233445566",
            CardDisplayName = "はやかけん 001",
            LostStartingPageNumber = 7,
        };

        var result = CarryoverInfoInput.Parse(
            "7", "0", "0", "", lost, new CarryoverInfo(1, 0, 0, null), CurrentFiscalYear);

        result.IsValid.Should().BeTrue(result.ErrorMessage);
        var helpText = ReadFiscalYearHelpText();
        helpText.Should().Contain("ときは空欄にできない", "空欄にできないのは条件に当たるときだけである");
        helpText.Should().NotContain("空欄でもよい", "条件に当たれば空欄は拒まれる");
    }

    private static string ReadFiscalYearHelpText()
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "Views", "Dialogs", "CarryoverRecoveryDialog.xaml");
        var xaml = XamlElementInspection.StripXmlComments(File.ReadAllText(path));
        var textBox = XamlElementInspection.EnumerateElementsIncludingNested(xaml, "TextBox")
            .SingleOrDefault(t => XamlElementInspection.GetAttribute(t.StartTag, "x:Name") == "CarryoverFiscalYearTextBox");
        textBox.Should().NotBeNull("対象年度の入力欄が実在すること（検査対象の空振り防止）");

        var helpText = XamlElementInspection.GetAttribute(textBox!.StartTag, "AutomationProperties.HelpText");
        helpText.Should().NotBeNullOrEmpty("対象年度の入力欄に読み上げ用の説明があること");
        return helpText!;
    }
}
