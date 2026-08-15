using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
        vm.EmptyStateMessage.Should().Be(CarryoverDataLossViewModel.NoLossMessage);
    }

    [Fact]
    public async Task InitializeAsync_検出に失敗したとき_被害なしと読める文言を出さないこと()
    {
        // 一覧が空になる理由は「被害が無い」と「確認できなかった」の2つある。
        // どちらも同じ「ありません」を出すと、DB 接続断で確認できなかっただけの利用者に
        // 「うちは無事だ」と誤って結論させる。データ健全性の画面で最も避けたい誤誘導。
        var detector = new Mock<ICarryoverDataLossDetector>();
        detector.Setup(d => d.DetectAsync()).ThrowsAsync(new InvalidOperationException("DB 接続断を注入"));
        var vm = new CarryoverDataLossViewModel(detector.Object);

        Func<Task> act = () => vm.InitializeAsync();

        // 呼び出し元（ダイアログ）がエラー通知を出せるよう、例外はそのまま伝える
        await act.Should().ThrowAsync<InvalidOperationException>();

        vm.HasItems.Should().BeFalse();
        vm.EmptyStateMessage.Should().Be(CarryoverDataLossViewModel.DetectionFailedMessage);
        vm.EmptyStateMessage.Should().NotBe(CarryoverDataLossViewModel.NoLossMessage);
        vm.EmptyStateMessage.Should().NotContain("ありません。", "「被害なし」と読める断定をしないこと");
    }

    [Fact]
    public async Task InitializeAsync_失敗後に成功したら案内を戻すこと()
    {
        // 接続が復旧して再読み込みしたのに「確認に失敗しました」が残ると、
        // 今度は逆に「まだ確認できていない」と誤解させる。
        var detector = new Mock<ICarryoverDataLossDetector>();
        detector.SetupSequence(d => d.DetectAsync())
            .ThrowsAsync(new InvalidOperationException("DB 接続断を注入"))
            .ReturnsAsync(new List<CarryoverDataLossItem>());
        var vm = new CarryoverDataLossViewModel(detector.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.InitializeAsync());
        await vm.InitializeAsync();

        vm.EmptyStateMessage.Should().Be(CarryoverDataLossViewModel.NoLossMessage);
    }

    [Fact]
    public void DetectionFailedMessage_エラーメッセージ品質を満たすこと()
    {
        // .claude/rules/error-messages.md の3要素
        var text = CarryoverDataLossViewModel.DetectionFailedMessage;

        text.Should().Contain("繰越情報");                 // 何が
        text.Should().MatchRegex("失敗|できません");        // なぜ
        // 「〜してください」に限定せず「〜てください」で判定する。本文言の最後の行動は
        // 「もう一度この画面を開く」であり、規約が求めるのは行動指示型で終わることであって
        // サ変動詞の形ではない（文言を正規表現へ合わせにいかない）。
        Regex.IsMatch(text, "てください。?$").Should().BeTrue("行動指示型で終わること");
        text.Length.Should().BeGreaterThan(20);
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
