using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Dtos;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// 繰越情報消失一覧ダイアログの ViewModel の単体テスト（Issue #1758）
/// </summary>
/// <remarks>
/// このダイアログの唯一の役割は「失われた元の値を、復旧を依頼する相手へ正確に伝えられる形で見せる」こと。
/// したがって表示の正確さ（消失していない項目を消失として見せない・値を加工しすぎない）が要件になる。
/// </remarks>
public class CarryoverDataLossViewModelTests
{
    private static CarryoverDataLossViewModel CreateViewModel(params CarryoverDataLossItem[] items)
    {
        var detector = new Mock<ICarryoverDataLossDetector>();
        detector.Setup(d => d.DetectAsync()).ReturnsAsync(items.ToList());
        return new CarryoverDataLossViewModel(detector.Object);
    }

    private static CarryoverDataLossItem FullLossItem() => new CarryoverDataLossItem
    {
        CardIdm = "0123456789ABCDEF",
        CardDisplayName = "はやかけん 001",
        LostStartingPageNumber = 7,
        LostCarryoverIncomeTotal = 45000,
        LostCarryoverExpenseTotal = 37500,
        LostCarryoverFiscalYear = 2025,
        LostAt = new DateTime(2026, 5, 20, 14, 30, 0),
        OperatorName = "総務 花子"
    };

    [Fact]
    public async Task InitializeAsync_検出結果を一覧へ読み込むこと()
    {
        var vm = CreateViewModel(FullLossItem());

        await vm.InitializeAsync();

        vm.Items.Should().ContainSingle();
        var row = vm.Items[0];
        row.CardDisplayName.Should().Be("はやかけん 001");
        row.LostStartingPageNumberText.Should().Be("7");
        row.LostCarryoverFiscalYearText.Should().Be("2025年度");
        row.LostAtText.Should().Be(DisplayFormatters.FormatDateTime(new DateTime(2026, 5, 20, 14, 30, 0)));
        row.OperatorName.Should().Be("総務 花子");
        vm.HasItems.Should().BeTrue();
    }

    [Fact]
    public async Task InitializeAsync_金額はカンマ区切りで表示すること()
    {
        var vm = CreateViewModel(FullLossItem());

        await vm.InitializeAsync();

        vm.Items[0].LostCarryoverIncomeTotalText.Should().Be(DisplayFormatters.FormatAmountWithUnit(45000));
        vm.Items[0].LostCarryoverExpenseTotalText.Should().Be(DisplayFormatters.FormatAmountWithUnit(37500));
    }

    [Fact]
    public async Task InitializeAsync_失われていない項目は消失なしと表示すること()
    {
        // 消失していない項目まで値を並べると、復旧作業で現在の正しい値を上書きさせてしまう。
        var item = FullLossItem();
        item.LostCarryoverIncomeTotal = null;
        item.LostCarryoverExpenseTotal = null;
        item.LostCarryoverFiscalYear = null;
        var vm = CreateViewModel(item);

        await vm.InitializeAsync();

        var row = vm.Items[0];
        row.LostStartingPageNumberText.Should().Be("7");
        row.LostCarryoverIncomeTotalText.Should().Be(CarryoverDataLossViewModel.NotLostText);
        row.LostCarryoverExpenseTotalText.Should().Be(CarryoverDataLossViewModel.NotLostText);
        row.LostCarryoverFiscalYearText.Should().Be(CarryoverDataLossViewModel.NotLostText);
    }

    [Fact]
    public async Task InitializeAsync_被害がなければ一覧が空になること()
    {
        var vm = CreateViewModel();

        await vm.InitializeAsync();

        vm.Items.Should().BeEmpty();
        vm.HasItems.Should().BeFalse();
    }

    [Fact]
    public async Task InitializeAsync_再実行しても重複しないこと()
    {
        // 復旧の進み具合を確認するために再読み込みできる。追加のみだと行が二重になる。
        var vm = CreateViewModel(FullLossItem());

        await vm.InitializeAsync();
        await vm.InitializeAsync();

        vm.Items.Should().ContainSingle();
    }
}
