using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Tests.Infrastructure.Timing;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// 同行者数入力ダイアログの自動クローズ（Issue #2009）の単体テスト。
/// </summary>
/// <remarks>
/// 「欠陥を突く側」（入力待ちで止まり続けない）と
/// 「正当な操作を塞いでいない側」（入力中・0 設定では閉じない）を対で置く。
/// 前者だけだと「常に即閉じる」実装でも緑になり、後者だけだと修正前のコードでも緑になる。
/// </remarks>
public class CompanionCountInputTimeoutTests
{
    private readonly Mock<ILedgerRepository> _ledgerRepoMock = new();
    private readonly TestTimerFactory _timerFactory = new();
    private readonly CompanionCountInputViewModel _vm;

    public CompanionCountInputTimeoutTests()
    {
        _ledgerRepoMock
            .Setup(r => r.UpdateCompanionCountAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(true);
        _vm = new CompanionCountInputViewModel(_ledgerRepoMock.Object, _timerFactory);
    }

    private static Ledger Usage(int id = 1)
        => new Ledger { Id = id, Date = new DateTime(2026, 9, 3), Summary = "鉄道（博多～天神）", Expense = 260, StaffName = "博多 花子" };

    private TestTimer Timer => _timerFactory.LastCreatedTimer!;

    [Fact]
    public void Initialize_秒数を指定するとカウントダウンが始まること()
    {
        _vm.Initialize(new[] { Usage() }, autoCloseSeconds: 30);

        _vm.IsCountdownRunning.Should().BeTrue();
        _vm.RemainingSeconds.Should().Be(30);
        Timer.IsRunning.Should().BeTrue();
        Timer.Interval.Should().Be(TimeSpan.FromSeconds(1));
        _vm.CountdownMessage.Should().Contain("30秒").And.Contain("外0名");
    }

    [Fact]
    public void Tick_残り秒数が減り案内文言に反映されること()
    {
        _vm.Initialize(new[] { Usage() }, autoCloseSeconds: 30);

        Timer.SimulateTicks(3);

        _vm.RemainingSeconds.Should().Be(27);
        _vm.CountdownMessage.Should().Contain("27秒");
    }

    [Fact]
    public void Tick_秒数経過で外0名として閉じること()
    {
        _vm.Initialize(new[] { Usage() }, autoCloseSeconds: 30);

        Timer.SimulateTicks(30);

        _vm.IsSaved.Should().BeTrue("入力待ちで業務が止まらないよう自動的に閉じる（#2009）");
        _vm.WasClosedByTimeout.Should().BeTrue();
        _vm.IsCountdownRunning.Should().BeFalse();
        Timer.IsRunning.Should().BeFalse("閉じた後にタイマーが回り続けない");
        _ledgerRepoMock.Verify(
            r => r.UpdateCompanionCountAsync(It.IsAny<int>(), It.IsAny<int>()),
            Times.Never,
            "「外0名」は返却時に 0 で INSERT 済みなので書き込みは不要");
    }

    [Fact]
    public void Tick_期限前は閉じないこと()
    {
        _vm.Initialize(new[] { Usage() }, autoCloseSeconds: 30);

        Timer.SimulateTicks(29);

        _vm.IsSaved.Should().BeFalse();
        _vm.WasClosedByTimeout.Should().BeFalse();
    }

    [Fact]
    public void 入力を始めたらカウントダウンを取り消すこと()
    {
        _vm.Initialize(new[] { Usage() }, autoCloseSeconds: 30);

        _vm.Items[0].CompanionCountText = "2";

        _vm.IsCountdownRunning.Should().BeFalse("入力中の職員の目の前で閉じない");
        _vm.CountdownMessage.Should().BeEmpty();

        Timer.SimulateTicks(60);
        _vm.IsSaved.Should().BeFalse();
        _vm.WasClosedByTimeout.Should().BeFalse();
    }

    [Fact]
    public void CancelCountdown_キー操作等で取り消せること()
    {
        _vm.Initialize(new[] { Usage() }, autoCloseSeconds: 30);

        // ダイアログの PreviewKeyDown / PreviewMouseDown から呼ばれる経路
        _vm.CancelCountdown();

        _vm.IsCountdownRunning.Should().BeFalse();
        Timer.SimulateTicks(60);
        _vm.IsSaved.Should().BeFalse();
    }

    [Fact]
    public void Initialize_0秒なら自動的に閉じないこと()
    {
        _vm.Initialize(new[] { Usage() }, autoCloseSeconds: 0);

        _vm.IsCountdownRunning.Should().BeFalse("0 は「必ず尋ねる」の意味（#2009）");
        _vm.CountdownMessage.Should().BeEmpty();
        _timerFactory.LastCreatedTimer.Should().BeNull("タイマー自体を作らない");
    }

    [Fact]
    public void Initialize_対象行が無ければカウントダウンしないこと()
    {
        _vm.Initialize(new List<Ledger>(), autoCloseSeconds: 30);

        _vm.IsCountdownRunning.Should().BeFalse();
        _timerFactory.LastCreatedTimer.Should().BeNull();
    }

    [Fact]
    public void Initialize_再初期化で前回のタイマーが残らないこと()
    {
        _vm.Initialize(new[] { Usage() }, autoCloseSeconds: 30);
        var firstTimer = Timer;

        _vm.Initialize(new[] { Usage(2) }, autoCloseSeconds: 30);

        firstTimer.IsRunning.Should().BeFalse("前回のカウントダウンは止める");
        firstTimer.SimulateTicks(60);
        _vm.IsSaved.Should().BeFalse("止めたタイマーは閉じる判断に影響しない");
        _vm.RemainingSeconds.Should().Be(30);
    }

    [Fact]
    public async Task SaveAsync_カウントダウンを止めること()
    {
        _vm.Initialize(new[] { Usage() }, autoCloseSeconds: 30);
        _vm.CancelCountdown();
        // 保存操作そのものでも止まることを確認するため、いったんカウントダウンを張り直す
        _vm.Initialize(new[] { Usage() }, autoCloseSeconds: 30);

        await _vm.SaveAsync();

        _vm.IsCountdownRunning.Should().BeFalse();
        Timer.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Skip_カウントダウンを止めること()
    {
        _vm.Initialize(new[] { Usage() }, autoCloseSeconds: 30);

        _vm.Skip();

        _vm.IsCountdownRunning.Should().BeFalse();
        Timer.IsRunning.Should().BeFalse();
        _vm.WasClosedByTimeout.Should().BeFalse("職員の操作で閉じたのであってタイムアウトではない");
    }

    [Fact]
    public void Initialize_タイムアウト済みの印を持ち越さないこと()
    {
        _vm.Initialize(new[] { Usage() }, autoCloseSeconds: 30);
        Timer.SimulateTicks(30);
        _vm.WasClosedByTimeout.Should().BeTrue();

        _vm.Initialize(new[] { Usage(2) }, autoCloseSeconds: 30);

        _vm.WasClosedByTimeout.Should().BeFalse("前回の結果を持ち越すと、閉じ方の判定が食い違う（#1883）");
    }

    [Fact]
    public void 既定秒数は30秒であること()
    {
        AppConstants.DefaultCompanionCountInputTimeoutSeconds.Should().Be(30);
        new AppSettings().CompanionCountInputTimeoutSeconds.Should().Be(30);
    }
}
