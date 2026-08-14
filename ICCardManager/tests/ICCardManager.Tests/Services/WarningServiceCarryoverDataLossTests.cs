using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// WarningService の繰越情報消失警告の単体テスト（Issue #1758）
/// </summary>
/// <remarks>
/// Issue #1726 以前の <c>UPDATE ic_card</c> で繰越累計・開始ページ番号を失ったカードは、
/// アプリからは何も通知されず、利用者が被害の有無を自力で判断できなかった。
/// 検出結果を警告エリアへ出すことで「気付けること」自体を価値とする（案A）。
/// </remarks>
public class WarningServiceCarryoverDataLossTests
{
    private static CarryoverDataLossItem Item(
        string displayName,
        int? startingPage = 7,
        int? incomeTotal = 45000,
        int? expenseTotal = 37500,
        int? fiscalYear = 2025) => new CarryoverDataLossItem
        {
            CardIdm = "0123456789ABCDEF",
            CardDisplayName = displayName,
            LostStartingPageNumber = startingPage,
            LostCarryoverIncomeTotal = incomeTotal,
            LostCarryoverExpenseTotal = expenseTotal,
            LostCarryoverFiscalYear = fiscalYear,
            LostAt = new DateTime(2026, 5, 20, 14, 30, 0),
            OperatorName = "総務 花子"
        };

    private static WarningService CreateService(params CarryoverDataLossItem[] items)
    {
        var detectorMock = new Mock<ICarryoverDataLossDetector>();
        detectorMock.Setup(d => d.DetectAsync()).ReturnsAsync(items.ToList());

        return new WarningService(
            new Mock<ILedgerRepository>().Object,
            new Mock<IDatabaseInfo>().Object,
            updateNotificationService: null,
            backupHealthService: null,
            carryoverDataLossDetector: detectorMock.Object);
    }

    #region 判定

    [Fact]
    public async Task CheckCarryoverDataLossWarningAsync_被害なし_警告を生成しないこと()
    {
        var service = CreateService();

        var warning = await service.CheckCarryoverDataLossWarningAsync();

        warning.Should().BeNull();
    }

    [Fact]
    public async Task CheckCarryoverDataLossWarningAsync_未注入_警告を生成しないこと()
    {
        // DI 未注入（既存テストの構築コード互換）でも例外にせず警告なしとする
        var service = new WarningService(
            new Mock<ILedgerRepository>().Object,
            new Mock<IDatabaseInfo>().Object);

        var warning = await service.CheckCarryoverDataLossWarningAsync();

        warning.Should().BeNull();
    }

    [Fact]
    public async Task CheckCarryoverDataLossWarningAsync_被害あり_件数とカード名を含む警告を生成すること()
    {
        var service = CreateService(Item("はやかけん 001"), Item("nimoca 003"));

        var warning = await service.CheckCarryoverDataLossWarningAsync();

        warning.Should().NotBeNull();
        warning.Type.Should().Be(WarningType.CarryoverDataLoss);
        warning.DisplayText.Should().Contain("2枚");
        warning.DisplayText.Should().Contain("はやかけん 001");
        warning.DisplayText.Should().Contain("nimoca 003");
    }

    [Fact]
    public async Task CheckCarryoverDataLossWarningAsync_多数被害_先頭数枚を列挙し残りは件数で示すこと()
    {
        // 全部並べると文字サイズ「特大」で警告エリアが何行にも折り返し、他の警告を押し出す。
        // 一方で件数だけでは「自分の担当カードが含まれるか」を判断できないため、先頭は名前で示す。
        var items = Enumerable.Range(1, AppConstants.CarryoverDataLossWarningMaxListedCards + 2)
            .Select(i => Item($"はやかけん {i:D3}"))
            .ToArray();

        var service = CreateService(items);

        var warning = await service.CheckCarryoverDataLossWarningAsync();

        warning.DisplayText.Should().Contain("はやかけん 001");
        warning.DisplayText.Should()
            .NotContain($"はやかけん {AppConstants.CarryoverDataLossWarningMaxListedCards + 1:D3}");
        warning.DisplayText.Should().Contain("ほか2枚");
    }

    [Fact]
    public void CarryoverDataLossWarningMaxListedCards_IsThree()
    {
        // 列挙数を変更した場合に上記テストの期待値も見直す必要があることを明示する
        AppConstants.CarryoverDataLossWarningMaxListedCards.Should().Be(3);
    }

    #endregion

    #region エラーメッセージ品質（.claude/rules/error-messages.md）

    [Fact]
    public async Task CarryoverDataLossWarning_SatisfiesErrorMessageQualityCriteria()
    {
        var service = CreateService(Item("はやかけん 001"));

        var warning = await service.CheckCarryoverDataLossWarningAsync();
        var text = warning.DisplayText;

        // 「何が」: 対象カードが名前で具体的に示され、失われた項目が何かが分かる
        text.Should().Contain("はやかけん 001");
        text.Should().MatchRegex("繰越累計|開始ページ番号");

        // 「なぜ」: 帳票にどう影響するのかが示されている（放置してよいものではないと分かる）
        text.Should().Contain("月次帳票");
        text.Should().MatchRegex("年度累計|ページ番号");

        // 「どうすれば」: 具体的な操作を示し、行動指示で終わる
        text.Should().Contain("クリック");
        Regex.IsMatch(text, "してください。?$").Should().BeTrue("行動指示型で終わること");

        // 曖昧な定型文で終わらせない
        text.Should().NotContain("エラーが発生しました");
        text.Should().NotContain("不正な値");

        // 情報不足を防ぐ最低文字数
        text.Length.Should().BeGreaterThan(20);
    }

    [Fact]
    public async Task CarryoverDataLossWarning_内部識別子であるIDmを文言へ露出しないこと()
    {
        // IDm は職員には意味が読み取れない内部識別子。カード名で示す。
        var service = CreateService(Item("はやかけん 001"));

        var warning = await service.CheckCarryoverDataLossWarningAsync();

        warning.DisplayText.Should().NotContain("0123456789ABCDEF");
    }

    #endregion
}
