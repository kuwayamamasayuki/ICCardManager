using System;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// <see cref="SingleInstanceGuard"/> の単体テスト（Issue #1910）
/// </summary>
/// <remarks>
/// <para>
/// 名前付きミューテックスは<b>カーネルオブジェクト</b>であり、同一プロセス内で 2 回目の
/// <c>Acquire</c> を行っても <c>createdNew</c> は false になる。したがって二重起動の判定は
/// 実際に 2 つのプロセスを起動しなくても検証できる。
/// </para>
/// <para>
/// ミューテックス名はテストごとに GUID で一意にする。固定名にすると、テストの並列実行や
/// 前回の実行が残したハンドルの影響で結果が入力以外の要因で変わる。
/// </para>
/// </remarks>
public class SingleInstanceGuardTests
{
    private static string UniqueName() => $@"Local\ICCardManagerTest-{Guid.NewGuid():N}";

    [Fact]
    public void 最初のインスタンスはPrimaryとして起動を継続できること()
    {
        using var guard = SingleInstanceGuard.Acquire(UniqueName());

        guard.Status.Should().Be(SingleInstanceStatus.Primary);
        guard.IsPrimaryInstance.Should().BeTrue();
        guard.AcquisitionError.Should().BeNull();
    }

    [Fact]
    public void 同じ名前の2つ目のインスタンスはAlreadyRunningとして起動を中止すること()
    {
        var name = UniqueName();
        using var first = SingleInstanceGuard.Acquire(name);

        using var second = SingleInstanceGuard.Acquire(name);

        second.Status.Should().Be(SingleInstanceStatus.AlreadyRunning);
        second.IsPrimaryInstance.Should().BeFalse();
    }

    [Fact]
    public void 名前が異なれば同時に起動できること()
    {
        // 対の表明: 判定が「常に 2 つ目を拒否する」実装へ退化していないことを固定する
        using var first = SingleInstanceGuard.Acquire(UniqueName());

        using var second = SingleInstanceGuard.Acquire(UniqueName());

        first.IsPrimaryInstance.Should().BeTrue();
        second.IsPrimaryInstance.Should().BeTrue();
    }

    [Fact]
    public void 先行インスタンスがDisposeした後は再びPrimaryとして起動できること()
    {
        var name = UniqueName();
        var first = SingleInstanceGuard.Acquire(name);
        first.Dispose();

        using var second = SingleInstanceGuard.Acquire(name);

        second.Status.Should().Be(SingleInstanceStatus.Primary);
    }

    [Fact]
    public void Disposeは冪等であること()
    {
        var guard = SingleInstanceGuard.Acquire(UniqueName());

        guard.Dispose();
        var act = () => guard.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void 判定に失敗した場合は起動を止めず理由を保持すること()
    {
        // Windows のカーネルオブジェクト名は 260 文字が上限。超過すると Mutex のコンストラクタが
        // 例外を投げる ＝ 判定不能。予防機構の不調で業務を止めないため、起動は継続させる。
        var tooLongName = @"Local\" + new string('a', 300);

        using var guard = SingleInstanceGuard.Acquire(tooLongName);

        guard.Status.Should().Be(SingleInstanceStatus.GuardUnavailable);
        guard.IsPrimaryInstance.Should().BeTrue("予防機構の不調で起動を止めてはならない");
        guard.AcquisitionError.Should().NotBeNull("なぜ二重起動を防げなかったのかをログへ残せること");
    }

    [Fact]
    public void ミューテックス名が空なら引数エラーになること()
    {
        var act = () => SingleInstanceGuard.Acquire(string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 存在するミューテックスを実在と判定すること()
    {
        // アクセス拒否の原因を「既に起動している」と「Global\ へ作る権限が無い」に
        // 切り分ける述語。前者だけを起動中止にしないと、誰も起動していない端末で
        // 「別のユーザーで起動しています」と言って起動できなくなる。
        var name = UniqueName();
        using var existing = SingleInstanceGuard.Acquire(name);

        SingleInstanceGuard.NamedMutexExists(name).Should().BeTrue();
    }

    [Fact]
    public void 存在しないミューテックスを実在と判定しないこと()
    {
        // 対の表明。ここが常に true を返す実装だと、権限不足の端末で起動できなくなる
        // （＝ fail-open が効かない）。
        SingleInstanceGuard.NamedMutexExists(UniqueName()).Should().BeFalse();
    }

    [Fact]
    public void 実在判定に失敗する名前は実在しない側へ倒すこと()
    {
        // 判定自体が失敗したときに「実在する」へ倒すと起動を止めてしまう。
        var tooLongName = @"Local\" + new string('a', 300);

        SingleInstanceGuard.NamedMutexExists(tooLongName).Should().BeFalse();
    }

    [Fact]
    public void 本番が使う名前は端末全体を対象とするGlobal接頭辞であること()
    {
        // Local\ にするとユーザーの簡易切り替えで 2 つのピッすいが 1 台の
        // カードリーダーを取り合う形が残る（AppConstants の remarks 参照）。
        AppConstants.SingleInstanceMutexName.Should().StartWith(@"Global\");
    }
}
