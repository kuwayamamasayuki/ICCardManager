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
        return new CarryoverDataLossViewModel(detector.Object, Mock.Of<INavigationService>());
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
        var vm = new CarryoverDataLossViewModel(detector.Object, Mock.Of<INavigationService>());

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
        var vm = new CarryoverDataLossViewModel(detector.Object, Mock.Of<INavigationService>());

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
    #region 復旧（Issue #2255）

    [Fact]
    public void From_行に検出結果を持たせること()
    {
        // 復旧ダイアログへは表示用の文字列ではなく検出結果そのものを渡す（失われた値を数値で使うため）
        var item = FullLossItem();

        CarryoverDataLossRow.From(item).Item.Should().BeSameAs(item);
    }

    [Fact]
    public async Task RecoverAsync_押した行の検出結果で復旧ダイアログを初期化すること()
    {
        // Arrange: ダイアログの初期化処理（Func）を捕まえ、Window を実体化せずに ViewModel だけ差し込んで実行する
        // （モックの ShowDialogAsync は Func を実行しないので、渡す引数を変えても緑になる。testing.md #2104）
        var item = FullLossItem();
        var detector = new Mock<ICarryoverDataLossDetector>();
        detector.Setup(d => d.DetectAsync()).ReturnsAsync(new List<CarryoverDataLossItem> { item });
        Func<ICCardManager.Views.Dialogs.CarryoverRecoveryDialog, Task>? configure = null;
        var navigation = new Mock<INavigationService>();
        navigation.Setup(n => n.ShowDialogAsync(It.IsAny<Func<ICCardManager.Views.Dialogs.CarryoverRecoveryDialog, Task>>()))
            .Callback<Func<ICCardManager.Views.Dialogs.CarryoverRecoveryDialog, Task>>(f => configure = f)
            .ReturnsAsync(false);
        var vm = new CarryoverDataLossViewModel(detector.Object, navigation.Object);
        await vm.InitializeAsync();

        // Act
        await vm.RecoverAsync(vm.Items[0]);

        // Assert
        configure.Should().NotBeNull("押した行で復旧ダイアログを開くこと");
        var (dialog, recoveryViewModel) = CreateRecoveryDialogWithoutWindow();
        await configure!(dialog);
        recoveryViewModel.CardDisplayName.Should().Be("はやかけん 001");
        recoveryViewModel.StartingPageNumberText.Should().Be("7", "押した行の失われた値が入力欄の初期値になる");
        recoveryViewModel.CarryoverIncomeTotalText.Should().Be("45000");
    }

    [Fact]
    public async Task RecoverAsync_保存したら一覧を作り直してから完了を案内すること()
    {
        // Arrange: 復旧後の再検出では対象が消える
        var detector = new Mock<ICarryoverDataLossDetector>();
        detector.SetupSequence(d => d.DetectAsync())
            .ReturnsAsync(new List<CarryoverDataLossItem> { FullLossItem() })
            .ReturnsAsync(new List<CarryoverDataLossItem>());
        var navigation = RecoveryDialogReturns(true);
        var vm = new CarryoverDataLossViewModel(detector.Object, navigation.Object);
        await vm.InitializeAsync();

        // Act
        await vm.RecoverAsync(vm.Items[0]);

        // Assert
        detector.Verify(d => d.DetectAsync(), Times.Exactly(2), "閉じたら一覧を作り直す");
        vm.Items.Should().BeEmpty();
        vm.HasItems.Should().BeFalse();
        vm.StatusMessage.Should().Be("はやかけん 001の繰越情報を復旧しました。メイン画面の警告は、この画面を閉じると更新されます。");
        vm.IsStatusError.Should().BeFalse();
    }

    [Fact]
    public async Task RecoverAsync_保存せずに閉じても一覧を作り直し_完了とは案内しないこと()
    {
        // ダイアログの中で競合（他のパソコンで先に復旧・削除）を検出した場合、案内は「一覧で状態を確認して」と
        // 述べる。一覧が古いままでは案内が事実にならない（Issue #1753）
        var detector = new Mock<ICarryoverDataLossDetector>();
        detector.SetupSequence(d => d.DetectAsync())
            .ReturnsAsync(new List<CarryoverDataLossItem> { FullLossItem() })
            .ReturnsAsync(new List<CarryoverDataLossItem>());
        var vm = new CarryoverDataLossViewModel(detector.Object, RecoveryDialogReturns(false).Object);
        await vm.InitializeAsync();

        await vm.RecoverAsync(vm.Items[0]);

        detector.Verify(d => d.DetectAsync(), Times.Exactly(2));
        vm.Items.Should().BeEmpty("他のパソコンで復旧された状態が一覧に反映される");
        vm.StatusMessage.Should().BeEmpty();
    }

    [Fact]
    public async Task RecoverAsync_別のカードのダイアログを保存せずに閉じても_前の完了の案内を消さないこと()
    {
        // カード A を復旧した後、カード B の復旧ダイアログをキャンセルしただけで「A を復旧しました」が消えると、
        // 職員は A の復旧が取り消されたと誤解し得る
        var other = FullLossItem();
        other.CardIdm = "FEDCBA9876543210";
        other.CardDisplayName = "nimoca 003";
        var detector = new Mock<ICarryoverDataLossDetector>();
        detector.SetupSequence(d => d.DetectAsync())
            .ReturnsAsync(new List<CarryoverDataLossItem> { FullLossItem(), other })
            .ReturnsAsync(new List<CarryoverDataLossItem> { other })
            .ReturnsAsync(new List<CarryoverDataLossItem> { other });
        var navigation = new Mock<INavigationService>();
        navigation.SetupSequence(n => n.ShowDialogAsync(It.IsAny<Func<ICCardManager.Views.Dialogs.CarryoverRecoveryDialog, Task>>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);
        var vm = new CarryoverDataLossViewModel(detector.Object, navigation.Object);
        await vm.InitializeAsync();

        await vm.RecoverAsync(vm.Items[0]);
        await vm.RecoverAsync(vm.Items[0]);

        detector.Verify(d => d.DetectAsync(), Times.Exactly(3));
        vm.StatusMessage.Should().StartWith("はやかけん 001の繰越情報を復旧しました。");
        vm.IsStatusError.Should().BeFalse();
    }

    [Fact]
    public async Task RecoverAsync_復旧の後で一覧の作り直しに失敗したら_復旧済みであることと失敗を併せて伝えること()
    {
        // 復旧はコミット済みで取り消されていない。作り直しの失敗だけを伝えると、職員はもう一度復旧しようとする
        var detector = new Mock<ICarryoverDataLossDetector>();
        detector.SetupSequence(d => d.DetectAsync())
            .ReturnsAsync(new List<CarryoverDataLossItem> { FullLossItem() })
            .ThrowsAsync(new InvalidOperationException("DB 接続断を注入"));
        var vm = new CarryoverDataLossViewModel(detector.Object, RecoveryDialogReturns(true).Object);
        await vm.InitializeAsync();

        await vm.RecoverAsync(vm.Items[0]);

        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().StartWith("はやかけん 001の繰越情報を復旧しました。");
        vm.StatusMessage.Should().Contain(
            ExceptionMessageFormatter.ToUserMessage(new InvalidOperationException("DB 接続断を注入"), "繰越情報消失一覧の再読み込み"));
        vm.EmptyStateMessage.Should().Be(CarryoverDataLossViewModel.DetectionFailedMessage, "「被害なし」と読ませない");
    }

    [Fact]
    public async Task RecoverAsync_行が無ければダイアログを開かないこと()
    {
        var navigation = RecoveryDialogReturns(true);
        var vm = new CarryoverDataLossViewModel(Mock.Of<ICarryoverDataLossDetector>(), navigation.Object);

        await vm.RecoverAsync(null);
        await vm.RecoverAsync(new CarryoverDataLossRow());

        navigation.Verify(n => n.ShowDialogAsync(It.IsAny<Func<ICCardManager.Views.Dialogs.CarryoverRecoveryDialog, Task>>()), Times.Never);
    }

    private static Mock<INavigationService> RecoveryDialogReturns(bool? result)
    {
        var navigation = new Mock<INavigationService>();
        navigation.Setup(n => n.ShowDialogAsync(It.IsAny<Func<ICCardManager.Views.Dialogs.CarryoverRecoveryDialog, Task>>()))
            .ReturnsAsync(result);
        return navigation;
    }

    /// <summary>
    /// Window を実体化せず（STA 不要）、復旧ダイアログへ ViewModel だけを差し込む
    /// </summary>
    private static (ICCardManager.Views.Dialogs.CarryoverRecoveryDialog Dialog, CarryoverRecoveryViewModel ViewModel)
        CreateRecoveryDialogWithoutWindow()
    {
        var cardRepository = new Mock<ICCardManager.Data.Repositories.ICardRepository>();
        cardRepository.Setup(r => r.GetByIdmAsync("0123456789ABCDEF", false))
            .ReturnsAsync(new ICCardManager.Models.IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "001" });
        var dbContext = new ICCardManager.Data.DbContext(":memory:");
        var service = new CardManagementService(
            dbContext, cardRepository.Object,
            new OperationLogger(Mock.Of<ICCardManager.Data.Repositories.IOperationLogRepository>(), Mock.Of<ICurrentOperatorContext>()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CardManagementService>.Instance);
        var recoveryViewModel = new CarryoverRecoveryViewModel(
            cardRepository.Object, service, Mock.Of<IStaffAuthService>(),
            new ICCardManager.Tests.Infrastructure.Timing.FixedSystemClock(new DateTime(2026, 10, 8)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CarryoverRecoveryViewModel>.Instance);

        var dialog = (ICCardManager.Views.Dialogs.CarryoverRecoveryDialog)System.Runtime.Serialization.FormatterServices
            .GetUninitializedObject(typeof(ICCardManager.Views.Dialogs.CarryoverRecoveryDialog));
        typeof(ICCardManager.Views.Dialogs.CarryoverRecoveryDialog)
            .GetField("_viewModel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(dialog, recoveryViewModel);
        return (dialog, recoveryViewModel);
    }

    #endregion
}
