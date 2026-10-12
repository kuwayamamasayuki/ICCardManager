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

    /// <summary>
    /// スコープを破棄しないまま絶対上限を超えると失効する（閉じ忘れに対する決定的な防御。Issue #2260）。
    /// </summary>
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
    public void MaxSessionDuration_IsThirtyMinutes()
    {
        CurrentOperatorContext.MaxSessionDuration.Should().Be(TimeSpan.FromMinutes(30));
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

    #region 認証は 1 操作に限るスコープ（Issue #2260）

    /// <summary>
    /// Issue #2260: <c>BeginSession</c> が返すスコープを破棄すると、有効期間の内側でもセッションが終わる。
    /// 旧実装はセッションを終える呼び出し元が無く、5 分間残ったセッションを認証の無い操作が拾っていた。
    /// </summary>
    [Fact]
    public void BeginSession_返したスコープを破棄すると有効期間内でもセッションが終わること()
    {
        var context = new CurrentOperatorContext(_clockMock.Object);
        var scope = context.BeginSession("AAAA000000000001", "田中太郎");

        _now = _now.AddSeconds(1);
        scope.Dispose();

        context.HasSession.Should().BeFalse("認証した操作が終わったら、次の操作へ認証を引き継がない");
        context.CurrentIdm.Should().BeNull();
        context.CurrentName.Should().BeNull();
    }

    /// <summary>
    /// 対の表明: スコープを破棄するまではセッションが続く（破棄する前から無効にする実装を通さない）。
    /// </summary>
    [Fact]
    public void BeginSession_スコープを破棄するまではセッションが続くこと()
    {
        var context = new CurrentOperatorContext(_clockMock.Object);
        using var scope = context.BeginSession("AAAA000000000001", "田中太郎");

        _now = _now.AddSeconds(1);

        context.HasSession.Should().BeTrue();
        context.CurrentIdm.Should().Be("AAAA000000000001");
    }

    /// <summary>
    /// Issue #2260: 既定の上限（30 分）の内側では、スコープを破棄するまで失効しない。
    /// 旧実装の 5 分では、入力に時間のかかる認証操作（「保存して次へ」で続ける履歴の変更など）の途中から、
    /// 後の記録が「GUI操作」になっていた。
    /// </summary>
    [Fact]
    public void BeginSession_上限の内側ではスコープを破棄するまで失効しないこと()
    {
        var context = new CurrentOperatorContext(_clockMock.Object);
        using var scope = context.BeginSession("AAAA000000000001", "田中太郎");

        _now = _now.Add(CurrentOperatorContext.MaxSessionDuration).AddSeconds(-1);

        context.HasSession.Should().BeTrue("認証した操作が続いている間は、上限までは時間で認証を切らない");
        context.CurrentIdm.Should().Be("AAAA000000000001");
        context.CurrentName.Should().Be("田中太郎");
    }

    /// <summary>
    /// 対の表明: スコープを破棄しないまま既定の上限（30 分）を超えると失効する。
    /// 閉じ忘れたスコープが GC などの条件に依存せず決定的に終わること。
    /// </summary>
    [Fact]
    public void BeginSession_スコープを破棄しないまま上限を超えると失効すること()
    {
        var context = new CurrentOperatorContext(_clockMock.Object);
        using var scope = context.BeginSession("AAAA000000000001", "田中太郎");

        _now = _now.Add(CurrentOperatorContext.MaxSessionDuration).AddSeconds(1);

        context.HasSession.Should().BeFalse("閉じ忘れたスコープも上限で決定的に失効する");
        context.CurrentIdm.Should().BeNull();
        context.CurrentName.Should().BeNull();
    }

    /// <summary>
    /// 古いスコープの破棄は、その後に開始された別のセッションを終えない
    /// （先の操作の後始末で、後の認証済み操作の帰属が GUI 操作へ落ちないように）。
    /// </summary>
    [Fact]
    public void BeginSession_古いスコープを破棄しても後から開始したセッションは終わらないこと()
    {
        var context = new CurrentOperatorContext(_clockMock.Object);
        var first = context.BeginSession("AAAA000000000001", "田中太郎");
        using var second = context.BeginSession("BBBB000000000002", "鈴木花子");

        first.Dispose();

        context.HasSession.Should().BeTrue();
        context.CurrentIdm.Should().Be("BBBB000000000002");
        context.CurrentName.Should().Be("鈴木花子");
    }

    /// <summary>
    /// 操作者の取り出しは 1 回の判定で IDm と氏名を返す（OperationLogger の記録に使う。Issue #2260）。
    /// </summary>
    [Fact]
    public void TryGetOperator_有効なセッションの操作者を返し_終了後は返さないこと()
    {
        var context = new CurrentOperatorContext(_clockMock.Object);
        var scope = context.BeginSession("AAAA000000000001", "田中太郎");

        context.TryGetOperator(out var idm, out var name).Should().BeTrue();
        idm.Should().Be("AAAA000000000001");
        name.Should().Be("田中太郎");

        scope.Dispose();

        context.TryGetOperator(out idm, out name).Should().BeFalse();
        idm.Should().BeNull();
        name.Should().BeNull();
    }

    #endregion
}
