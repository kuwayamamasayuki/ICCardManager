using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Dtos;
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
}
