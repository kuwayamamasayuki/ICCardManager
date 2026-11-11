#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2385: カード種別選択ダイアログの「登録が完了するまでカードを離さない」注意を出す条件。
/// </summary>
/// <remarks>
/// 注意は「交通系ICカードの登録時にカードを読み直すとき」だけ意味を持つ。読み直す側
/// （<see cref="CardManageViewModel"/> の保存。<c>CardManageViewModelTests</c> の事前読み取り残高・履歴のテストが固定）と
/// 同じ判定を使うので、ここでは判定そのものを「出ない側」と「出る側」の対で固定する。
/// </remarks>
public class CardRegistrationPreReadTests
{
    private const int ReadBalance = 4800;

    private static List<LedgerDetail> OneEntryHistory() => new()
    {
        new LedgerDetail
        {
            UseDate = new DateTime(2026, 11, 2),
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 210,
            Balance = ReadBalance,
        },
    };

    [Fact]
    public void 残高と利用履歴を事前に読み取れたときは注意を出さないこと()
    {
        CardRegistrationPreRead.NeedsCardOnReaderUntilRegistered(ReadBalance, OneEntryHistory())
            .Should().BeFalse("登録時にカードを読み直さないので、カードを離してよい");
    }

    [Fact]
    public void 残高と利用履歴をどちらも読み取れなかったときは注意を出すこと()
    {
        CardRegistrationPreRead.NeedsCardOnReaderUntilRegistered(null, null)
            .Should().BeTrue("登録時に残高と利用履歴を読み直す可能性があるので、カードを載せておく必要がある");
    }

    [Fact]
    public void 残高だけ読み取れなかったときは注意を出すこと()
    {
        CardRegistrationPreRead.NeedsCardOnReaderUntilRegistered(null, OneEntryHistory())
            .Should().BeTrue("登録時に残高を読み直す可能性がある（取り込む履歴が無ければ読み直す）ので、カードを載せておく必要がある");
    }

    [Fact]
    public void 利用履歴が空のときは残高を読み取れていても注意を出すこと()
    {
        // 後方互換の ReadHistoryAsync は読み取りの失敗も空のリストで返すため、利用 0 件と区別できない（Issue #1169）。
        // 登録時はどちらも読み直すので、案内も揃える
        CardRegistrationPreRead.NeedsCardOnReaderUntilRegistered(ReadBalance, new List<LedgerDetail>())
            .Should().BeTrue("登録時に利用履歴を読み直す可能性があるので、カードを載せておく必要がある");
    }

    [Fact]
    public void 利用履歴がnullのときは残高を読み取れていても注意を出すこと()
    {
        CardRegistrationPreRead.NeedsCardOnReaderUntilRegistered(ReadBalance, null)
            .Should().BeTrue("登録時に利用履歴を読み直す可能性があるので、カードを載せておく必要がある");
    }

    [Fact]
    public void 残高0円は読み取れた残高として扱い読み直さないこと()
    {
        // 0 円は「読み取れなかった」ではない（null と取り違えると、残高 0 円のカードで不要な注意が出る）
        CardRegistrationPreRead.RereadsBalance(0).Should().BeFalse();
        CardRegistrationPreRead.NeedsCardOnReaderUntilRegistered(0, OneEntryHistory()).Should().BeFalse();
    }

    // ── 読み直し（保存の経路が使う。Issue #2385 のレビュー: 判定と読み取りを一体にして、保存の経路で条件を書き直させない） ──

    private const string Idm = "0102030405060708";

    [Fact]
    public async Task 残高を事前に読み取れていれば残高をカードから読み直さないこと()
    {
        var reader = new Mock<ICardReader>(MockBehavior.Strict);

        var balance = await CardRegistrationPreRead.ResolveBalanceAsync(reader.Object, Idm, ReadBalance);

        balance.Should().Be(ReadBalance);
        reader.Verify(r => r.ReadBalanceAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task 残高を事前に読み取れていなければカードから読み直すこと()
    {
        var reader = new Mock<ICardReader>();
        reader.Setup(r => r.ReadBalanceAsync(Idm)).ReturnsAsync(1234);

        var balance = await CardRegistrationPreRead.ResolveBalanceAsync(reader.Object, Idm, null);

        balance.Should().Be(1234, "登録時にカードから読み直した残高を使う");
        reader.Verify(r => r.ReadBalanceAsync(Idm), Times.Once);
    }

    [Fact]
    public async Task 利用履歴を事前に読み取れていれば利用履歴をカードから読み直さないこと()
    {
        var reader = new Mock<ICardReader>(MockBehavior.Strict);
        var history = OneEntryHistory();

        var resolved = await CardRegistrationPreRead.ResolveHistoryAsync(reader.Object, Idm, history);

        resolved.Should().BeSameAs(history);
        reader.Verify(r => r.ReadHistoryAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 利用履歴が無いか0件ならカードから読み直すこと(bool empty)
    {
        var reader = new Mock<ICardReader>();
        reader.Setup(r => r.ReadHistoryAsync(Idm)).ReturnsAsync((IEnumerable<LedgerDetail>)OneEntryHistory());

        var resolved = await CardRegistrationPreRead.ResolveHistoryAsync(
            reader.Object, Idm, empty ? new List<LedgerDetail>() : null);

        resolved.Should().ContainSingle("登録時にカードから読み直した利用履歴を使う");
        reader.Verify(r => r.ReadHistoryAsync(Idm), Times.Once);
    }

    [Fact]
    public async Task 利用履歴の読み直しに失敗してもnullを返して登録を続けられること()
    {
        var reader = new Mock<ICardReader>();
        reader.Setup(r => r.ReadHistoryAsync(Idm)).ThrowsAsync(new InvalidOperationException("カードが離れた"));

        var resolved = await CardRegistrationPreRead.ResolveHistoryAsync(reader.Object, Idm, null);

        resolved.Should().BeNull("読み直せなくても例外にせず、履歴の取り込みだけを諦める（従来の SaveAsync と同じ）");
    }
}
