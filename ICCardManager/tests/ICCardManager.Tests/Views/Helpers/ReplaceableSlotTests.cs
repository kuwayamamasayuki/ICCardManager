using System;
using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Helpers;

/// <summary>
/// Issue #2141: 自動では消えない通知（エラー・記録済みの案内）を 1 枚に差し替える置き場の判断を固定する。
/// </summary>
/// <remarks>
/// 旧実装は <c>ShowError</c> のたびに新しい窓を同じ画面隅へ重ね、1 枚ずつクリックしないと消えなかった。
/// トーストの窓（<c>ToastNotificationWindow</c>）は STA 依存で xUnit から生成できないため、
/// 判断をここで、結線（<c>Show</c> の <c>autoClose: false</c> が置き場へ置くこと）は
/// <see cref="ToastNotificationStyleTests"/> の静的検査で固定する。
/// </remarks>
public class ReplaceableSlotTests
{
    private readonly List<string> _closed = new();

    private ReplaceableSlot<string> CreateSlot() => new(item => _closed.Add(item));

    [Fact]
    public void Put_新しいものを置くと古いものを閉じること()
    {
        var slot = CreateSlot();

        slot.Put("エラー1");
        slot.Put("エラー2");

        _closed.Should().Equal("エラー1");
        slot.Current.Should().Be("エラー2");
    }

    [Fact]
    public void Put_最初の1枚は何も閉じないこと()
    {
        var slot = CreateSlot();

        slot.Put("エラー1");

        _closed.Should().BeEmpty("対の表明: 置くたびに自分自身を閉じる実装を落とす");
        slot.Current.Should().Be("エラー1");
    }

    [Fact]
    public void Dismiss_置かれているものを閉じて空けること()
    {
        var slot = CreateSlot();
        slot.Put("エラー1");

        slot.Dismiss().Should().BeTrue();

        _closed.Should().Equal("エラー1");
        slot.Current.Should().BeNull();
    }

    [Fact]
    public void Dismiss_空なら何もせずfalseを返すこと()
    {
        var slot = CreateSlot();

        slot.Dismiss().Should().BeFalse();

        _closed.Should().BeEmpty();
    }

    [Fact]
    public void Release_自分で閉じたものは閉じる処理を呼ばずに置き場から外すこと()
    {
        var slot = CreateSlot();
        slot.Put("エラー1");

        slot.Release("エラー1");

        slot.Current.Should().BeNull();
        _closed.Should().BeEmpty("クリックで既に閉じたものを二重に閉じない");
    }

    [Fact]
    public void Release_差し替えの後に届いた古いものの閉じ終わりは新しいものを外さないこと()
    {
        // フェードアウトの完了（Closed）は差し替えより後に届く。古い方の Release で
        // 新しく置いた通知まで置き場から外すと、次の職員証タッチで閉じられなくなる
        var slot = CreateSlot();
        slot.Put("エラー1");
        slot.Put("エラー2");

        slot.Release("エラー1");

        slot.Current.Should().Be("エラー2");
    }

    [Fact]
    public void Put_nullは受け付けないこと()
    {
        var slot = CreateSlot();

        Action act = () => slot.Put(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
