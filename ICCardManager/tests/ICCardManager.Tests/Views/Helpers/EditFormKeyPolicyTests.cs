using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FluentAssertions;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.ViewModels;
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
/// 判断だけを純関数へ切り出してここで固定し、結線（<c>KeyDown</c> → この判断）は
/// <see cref="ICCardManager.Tests.Views.EditFormKeyboardConventionTests"/> が
/// ソーステキストの静的検査で固定する（<c>error-messages.md</c> #1817 と同じ作法）。
/// </para>
/// </remarks>
[Collection(StaThreadCollection.Name)]
public class EditFormKeyPolicyTests
{
    [Fact]
    public void 編集中のEscapeは編集の取り消しになること()
    {
        ProductionHelpers.EditFormKeyPolicy.ResolveEscapeAction(isEditing: true, isBusy: false)
            .Should().Be(ProductionHelpers.EditFormEscapeAction.CancelEdit,
                "Issue #2080: 編集フォームに入力している最中の Escape でダイアログごと閉じると、" +
                "確認も無く入力内容が失われる。閉じるのは編集フォームだけで、一覧へ戻る");
    }

    [Fact]
    public void 編集していないときのEscapeはダイアログを閉じること()
    {
        ProductionHelpers.EditFormKeyPolicy.ResolveEscapeAction(isEditing: false, isBusy: false)
            .Should().Be(ProductionHelpers.EditFormEscapeAction.CloseDialog,
                "Issue #2080: 一覧を見ているだけのときは従来どおり Escape で閉じる。" +
                "編集中だけを特別扱いする修正であって、Escape を効かなくする修正ではない");
    }

    /// <summary>
    /// 処理中は編集状態に関わらず何もしないこと（コードレビューで検出）。
    /// </summary>
    /// <remarks>
    /// 処理中オーバーレイが塞ぐのはマウスのヒットテストだけで、キーボードは配下へ届く（#1761）。
    /// 保存は <c>BeginBusy</c> スコープの内側で DB を待ち、その継続で入力欄
    /// （<c>EditCardIdm</c> 等）を読むため、待機中に <c>CancelEdit()</c> が走ると
    /// <b>空の値で UPDATE が組み立てられ</b>、影響行数 0 から「他のパソコンで削除された可能性があります」
    /// という、試みてすらいない編集についての競合案内が出る。
    /// 非編集中も握り潰すのは、削除・払い戻しの待機中にダイアログを閉じると
    /// <c>Closed</c> → <c>Cleanup</c> が処理の途中で走るため。
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 処理中のEscapeは何もしないこと(bool isEditing)
    {
        ProductionHelpers.EditFormKeyPolicy.ResolveEscapeAction(isEditing, isBusy: true)
            .Should().Be(ProductionHelpers.EditFormEscapeAction.Ignore,
                "Issue #2080（コードレビュー）: 保存や削除の待機中に編集状態を巻き戻すと、" +
                "継続が空の入力欄を読み、起きていない競合として案内される");
    }

    /// <summary>
    /// 入力の全域（<c>bool</c> × <c>bool</c> の 4 通り）を走査し、3 つの結果が実際に出ることを表明する。
    /// </summary>
    /// <remarks>
    /// 上の個別の期待値は、片方を書き換える退行がもう片方を巻き込まない。
    /// ここで<b>結果の集合</b>を別の観測軸として置くと、いずれかの引数を見ずに畳んだ実装
    /// （常に閉じる／常に取り消す／<c>isBusy</c> を無視する）が必ず赤になる。
    /// </remarks>
    [Fact]
    public void 編集状態と処理中の組み合わせで3通りの結果が出ること()
    {
        var results = new List<ProductionHelpers.EditFormEscapeAction>();
        foreach (var isEditing in new[] { true, false })
        {
            foreach (var isBusy in new[] { true, false })
            {
                results.Add(ProductionHelpers.EditFormKeyPolicy.ResolveEscapeAction(isEditing, isBusy));
            }
        }

        results.Distinct().Should().BeEquivalentTo(
            new[]
            {
                ProductionHelpers.EditFormEscapeAction.CancelEdit,
                ProductionHelpers.EditFormEscapeAction.CloseDialog,
                ProductionHelpers.EditFormEscapeAction.Ignore,
            },
            "Issue #2080: どちらかの引数を見ない実装はここで結果の種類が減って赤になる");
    }

    /// <summary>
    /// <c>HandleEscape</c> が ViewModel の編集状態と処理中の<b>両方</b>を判断へ渡し、
    /// 判断の結果どおりに振り分けること。
    /// </summary>
    /// <remarks>
    /// Issue #2102: 純関数（<c>ResolveEscapeAction</c>）の単体テストと、コードビハインドが
    /// <c>HandleEscape(this, _viewModel, e)</c> を呼ぶことの静的検査の間にある <c>HandleEscape</c> 自身は
    /// 何も検査されていなかった。<c>ResolveEscapeAction(viewModel.IsEditing, false)</c> と
    /// 処理中を定数で潰しても、<c>CancelEdit</c> と <c>Close</c> の振り分けを入れ替えても緑だった。
    /// 実物の <see cref="Window"/>（表示しない）とキーイベントを STA スレッドで組み立てて、
    /// 4 通りの状態すべてについて「取り消し」「閉じる」の観測結果を表明する。
    /// </remarks>
    [Theory]
    [InlineData(true, false, EditFormEscapeOutcome.CancelEdit)]
    [InlineData(false, false, EditFormEscapeOutcome.CloseDialog)]
    [InlineData(true, true, EditFormEscapeOutcome.Nothing)]
    [InlineData(false, true, EditFormEscapeOutcome.Nothing)]
    public void HandleEscapeは編集状態と処理中に応じて振り分けること(
        bool isEditing, bool isBusy, EditFormEscapeOutcome expected)
    {
        var (outcome, handled) = RunHandleEscape(Key.Escape, isEditing, isBusy);

        outcome.Should().Be(expected,
            "Issue #2080: 処理中は何もせず、編集中は編集フォームだけを閉じ、一覧ではダイアログを閉じる");
        handled.Should().BeTrue("処理中に握り潰すときも、既定の処理へ流さないため Handled を立てる");
    }

    /// <summary>Escape 以外のキーには触れないこと（対の表明）。</summary>
    [Fact]
    public void HandleEscapeはEscape以外のキーを処理しないこと()
    {
        var (outcome, handled) = RunHandleEscape(Key.Enter, isEditing: true, isBusy: false);

        outcome.Should().Be(EditFormEscapeOutcome.Nothing, "Enter は既定ボタン（保存）が受け持つ");
        handled.Should().BeFalse("Handled を立てると既定ボタンへ Enter が届かなくなる");
    }

    /// <summary><c>HandleEscape</c> を 1 回呼んだ結果として観測できたこと。</summary>
    public enum EditFormEscapeOutcome
    {
        Nothing,
        CancelEdit,
        CloseDialog,
    }

    private static (EditFormEscapeOutcome Outcome, bool Handled) RunHandleEscape(Key key, bool isEditing, bool isBusy)
    {
        var outcome = EditFormEscapeOutcome.Nothing;
        var handled = false;
        StaTestRunner.Run(() =>
        {
            var viewModel = new FakeEditFormViewModel(isEditing, isBusy);
            var dialog = new Window();
            var closing = false;
            dialog.Closing += (_, _) => closing = true;

            var args = new KeyEventArgs(Keyboard.PrimaryDevice, new FakePresentationSource(), 0, key)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            };

            ProductionHelpers.EditFormKeyPolicy.HandleEscape(dialog, viewModel, args);

            viewModel.CancelEditCount.Should().BeLessThanOrEqualTo(1);
            (viewModel.CancelEditCount == 1 && closing).Should().BeFalse("取り消しと閉じるは同時に起きない");
            outcome = viewModel.CancelEditCount == 1
                ? EditFormEscapeOutcome.CancelEdit
                : closing ? EditFormEscapeOutcome.CloseDialog : EditFormEscapeOutcome.Nothing;
            handled = args.Handled;
        });
        return (outcome, handled);
    }

    private sealed class FakeEditFormViewModel : IEditFormViewModel
    {
        public FakeEditFormViewModel(bool isEditing, bool isBusy)
        {
            IsEditing = isEditing;
            IsBusy = isBusy;
        }

        public bool IsEditing { get; }

        public bool IsBusy { get; }

        public int CancelEditCount { get; private set; }

        public void CancelEdit() => CancelEditCount++;
    }

    /// <summary><see cref="KeyEventArgs"/> の生成に必要な入力元（中身は使われない）。</summary>
    private sealed class FakePresentationSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }

        public override bool IsDisposed => false;

        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
