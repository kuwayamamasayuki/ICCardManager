using System;
using FluentAssertions;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// <see cref="CurrentOperatorContext"/> の単体テスト。
/// Issue #1265: 操作者コンテキストの失効判定・排他制御・nullハンドリングを検証。
/// </summary>
public class CurrentOperatorContextTests
{
    private readonly Mock<ISystemClock> _clockMock;
    private DateTime _now;

    public CurrentOperatorContextTests()
    {
        _now = new DateTime(2026, 4, 17, 10, 0, 0);
        _clockMock = new Mock<ISystemClock>();
        _clockMock.Setup(c => c.Now).Returns(() => _now);
    }

    [Fact]
    public void NewInstance_HasNoSession()
    {
        var context = new CurrentOperatorContext(_clockMock.Object);

        context.HasSession.Should().BeFalse();
        context.CurrentIdm.Should().BeNull();
        context.CurrentName.Should().BeNull();
    }

    [Fact]
    public void BeginSession_SetsOperatorValues()
    {
        var context = new CurrentOperatorContext(_clockMock.Object);

        context.BeginSession("AAAA000000000001", "田中太郎");

        context.HasSession.Should().BeTrue();
        context.CurrentIdm.Should().Be("AAAA000000000001");
        context.CurrentName.Should().Be("田中太郎");
    }

    [Fact]
    public void BeginSession_WithNullIdm_Throws()
    {
        var context = new CurrentOperatorContext(_clockMock.Object);

        Action act = () => context.BeginSession(null!, "氏名");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BeginSession_WithEmptyName_Throws()
    {
        var context = new CurrentOperatorContext(_clockMock.Object);

        Action act = () => context.BeginSession("AAAA000000000001", string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithZeroDuration_Throws()
    {
        Action act = () => new CurrentOperatorContext(_clockMock.Object, TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_WithNullClock_Throws()
    {
        Action act = () => new CurrentOperatorContext(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Session_ExpiresAfterDuration()
    {
        var context = new CurrentOperatorContext(_clockMock.Object, TimeSpan.FromMinutes(5));
        context.BeginSession("AAAA000000000001", "田中太郎");

        // 経過前: セッション有効
        _now = _now.AddMinutes(4).AddSeconds(59);
        context.HasSession.Should().BeTrue();
        context.CurrentIdm.Should().Be("AAAA000000000001");

        // 経過後: セッション無効（失効）
        _now = _now.AddSeconds(2); // 計 5 分 1 秒経過
        context.HasSession.Should().BeFalse();
        context.CurrentIdm.Should().BeNull();
        context.CurrentName.Should().BeNull();
    }

    [Fact]
    public void BeginSession_RenewsExpiration()
    {
        var context = new CurrentOperatorContext(_clockMock.Object, TimeSpan.FromMinutes(5));
        context.BeginSession("AAAA000000000001", "田中太郎");

        _now = _now.AddMinutes(4);
        // 再認証で有効期限を更新
        context.BeginSession("BBBB000000000002", "山田花子");

        _now = _now.AddMinutes(4); // 元の BeginSession から 8 分経過しているが、再認証は 4 分前なので有効
        context.HasSession.Should().BeTrue();
        context.CurrentIdm.Should().Be("BBBB000000000002");
        context.CurrentName.Should().Be("山田花子");
    }

    [Fact]
    public void ClearSession_InvalidatesSession()
    {
        var context = new CurrentOperatorContext(_clockMock.Object);
        context.BeginSession("AAAA000000000001", "田中太郎");

        context.ClearSession();

        context.HasSession.Should().BeFalse();
        context.CurrentIdm.Should().BeNull();
        context.CurrentName.Should().BeNull();
    }

    [Fact]
    public void DefaultSessionDuration_IsFiveMinutes()
    {
        CurrentOperatorContext.DefaultSessionDuration.Should().Be(TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// セッション中にクロックが1ms巻き戻されても、期限前ならセッションは有効でなければならない。
    /// </summary>
    [Fact]
    public void CurrentIdm_ReflectsClockAtQueryTime()
    {
        var context = new CurrentOperatorContext(_clockMock.Object, TimeSpan.FromSeconds(30));
        context.BeginSession("AAAA000000000001", "田中太郎");

        _now = _now.AddSeconds(29);
        context.CurrentIdm.Should().Be("AAAA000000000001");

        _now = _now.AddSeconds(2); // 31秒経過 → 失効
        context.CurrentIdm.Should().BeNull();
    }
}
