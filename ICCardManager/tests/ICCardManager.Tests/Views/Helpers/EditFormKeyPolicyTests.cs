using FluentAssertions;
using Xunit;
using ProductionHelpers = ICCardManager.Views.Helpers;

namespace ICCardManager.Tests.Views.Helpers;

/// <summary>
/// Issue #2080: 「一覧＋編集フォーム」型ダイアログで Escape キーが意味する操作を固定する。
/// </summary>
/// <remarks>
/// <para>
/// 編集フォームに入力している最中の Escape は、従来「完了」（<c>IsCancel="True"</c>）へ割り当たっており、
/// 確認なしで<b>入力内容ごとダイアログが閉じて</b>いた。Escape は「いま開いている入れ子の
/// いちばん内側を閉じる」キーなので、編集中の内側は編集フォームであり、一覧へ戻るのが正しい。
/// </para>
/// <para>
/// <c>Window</c> のコードビハインドは STA 依存で xUnit から実行できないため、
/// 判断だけを純関数へ切り出してここで固定し、結線（<c>PreviewKeyDown</c> → この判断）は
/// <see cref="ICCardManager.Tests.Views.EditFormKeyboardConventionTests"/> が
/// ソーステキストの静的検査で固定する（<c>error-messages.md</c> #1817 と同じ作法）。
/// </para>
/// </remarks>
public class EditFormKeyPolicyTests
{
    [Fact]
    public void 編集中のEscapeは編集の取り消しになること()
    {
        ProductionHelpers.EditFormKeyPolicy.ResolveEscapeAction(isEditing: true)
            .Should().Be(ProductionHelpers.EditFormEscapeAction.CancelEdit,
                "Issue #2080: 編集フォームに入力している最中の Escape でダイアログごと閉じると、" +
                "確認も無く入力内容が失われる。閉じるのは編集フォームだけで、一覧へ戻る");
    }

    [Fact]
    public void 編集していないときのEscapeはダイアログを閉じること()
    {
        ProductionHelpers.EditFormKeyPolicy.ResolveEscapeAction(isEditing: false)
            .Should().Be(ProductionHelpers.EditFormEscapeAction.CloseDialog,
                "Issue #2080: 一覧を見ているだけのときは従来どおり Escape で閉じる。" +
                "編集中だけを特別扱いする修正であって、Escape を効かなくする修正ではない");
    }

    /// <summary>
    /// 入力の全域（<c>bool</c> の 2 値）で結果が異なることを表明する。
    /// </summary>
    /// <remarks>
    /// 上の 2 件は「その入力に対する期待値」であり、片方を書き換える退行はもう片方を巻き込まない。
    /// ここで<b>両者が常に異なる</b>ことを別の観測軸として置くと、
    /// 編集状態を見ずに一方へ畳んだ実装（常に閉じる／常に取り消す）が必ず赤になる。
    /// </remarks>
    [Fact]
    public void 編集中かどうかで結果が変わること()
    {
        ProductionHelpers.EditFormKeyPolicy.ResolveEscapeAction(isEditing: true)
            .Should().NotBe(ProductionHelpers.EditFormKeyPolicy.ResolveEscapeAction(isEditing: false),
                "Issue #2080: 編集状態を見ない実装（常に閉じる／常に取り消す）はこの表明で赤になる");
    }
}
