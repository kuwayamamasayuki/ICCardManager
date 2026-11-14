#nullable enable
using System;
using System.Threading.Tasks;
using System.Windows;
using ICCardManager.Services;

namespace ICCardManager.ViewModels;

/// <summary>
/// メイン画面・履歴パネルが表示するモーダル（ダイアログ・MessageBox）の表示中、メイン画面のカード読み取りを
/// 抑制する <see cref="INavigationService"/> の包み（Issue #2484）。
/// </summary>
/// <remarks>
/// <para>
/// <c>ShowDialog</c> / <c>MessageBox.Show</c> は入れ子のメッセージポンプを回すため、表示中もカードリーダーの
/// イベントはメイン画面へ届く。抑制しないと、交通系ICカード管理ダイアログや確認の表示中に職員証をタッチした
/// だけで「交通系ICカードのタッチ待ち」へ移り、続く交通系ICカードのタッチがその職員の貸出・返却として記録される。
/// </para>
/// <para>
/// 呼び出しごとに抑制を書くのではなく、メイン画面と履歴パネルが受け取った <see cref="INavigationService"/> を
/// コンストラクタでこの包みに差し替える。以後の表示はすべてここを通るので、新しいダイアログ・確認を足しても
/// 抑制が漏れない（呼び出し側の 1 行ずつに頼ると、足すたびに書き忘れが起きる。#1763）。
/// <c>NavigationService</c>（アプリ全体で 1 つ）に入れないのは、ダイアログ側の ViewModel が出す確認まで
/// 数えると、入れ子の深さをメッセージで数える仕組みが要るため（抑制ソースは <c>HashSet</c> で参照カウントを
/// 持たない）。ダイアログの中の確認は、外側のダイアログの表示中なので既に抑制されている。
/// </para>
/// <para>
/// 抑制はメイン画面の処理だけを止める。ダイアログ自身がカードを読む機能（登録モード・職員証の差し替え・
/// データインポートのタッチ待ち・職員認証）はカードリーダーを直接購読しているので影響しない。
/// </para>
/// <para>
/// 表示が重なった場合（入れ子、または先に開いた側が先に閉じる順）の扱いは、コンストラクタの <c>beginSuppression</c> が返す
/// スコープが受け持つ（メイン画面は開いているスコープの数を数え、最後が閉じたときだけ抑制を外す）。
/// 例外で抜けても <c>using</c> で解放する（#1725）。
/// </para>
/// </remarks>
internal sealed class ModalCardReadingSuppressingNavigationService : INavigationService
{
    private readonly INavigationService _inner;
    private readonly Func<IDisposable> _beginSuppression;

    /// <param name="inner">実際に表示する <see cref="INavigationService"/></param>
    /// <param name="beginSuppression">表示の間だけ保持する抑制スコープを開く関数（表示のたびに呼ぶ）</param>
    public ModalCardReadingSuppressingNavigationService(INavigationService inner, Func<IDisposable> beginSuppression)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _beginSuppression = beginSuppression ?? throw new ArgumentNullException(nameof(beginSuppression));
    }

    /// <inheritdoc/>
    public bool ShowConfirmation(string message, string title)
    {
        using (_beginSuppression())
        {
            return _inner.ShowConfirmation(message, title);
        }
    }

    /// <inheritdoc/>
    public bool ShowWarningConfirmation(string message, string title)
    {
        using (_beginSuppression())
        {
            return _inner.ShowWarningConfirmation(message, title);
        }
    }

    /// <inheritdoc/>
    public bool? ShowThreeWayConfirmation(string message, string title)
    {
        using (_beginSuppression())
        {
            return _inner.ShowThreeWayConfirmation(message, title);
        }
    }

    /// <inheritdoc/>
    public void ShowInformation(string message, string title)
    {
        using (_beginSuppression())
        {
            _inner.ShowInformation(message, title);
        }
    }

    /// <inheritdoc/>
    public void ShowWarning(string message, string title)
    {
        using (_beginSuppression())
        {
            _inner.ShowWarning(message, title);
        }
    }

    /// <inheritdoc/>
    public void ShowError(string message, string title)
    {
        using (_beginSuppression())
        {
            _inner.ShowError(message, title);
        }
    }

    /// <inheritdoc/>
    public Views.Dialogs.CardRegistrationModeResult? ShowCardRegistrationModeDialog(int? currentCardBalance = null)
    {
        using (_beginSuppression())
        {
            return _inner.ShowCardRegistrationModeDialog(currentCardBalance);
        }
    }

    /// <inheritdoc/>
    public bool? ShowDialog<TDialog>(Action<TDialog>? configure = null) where TDialog : Window
    {
        using (_beginSuppression())
        {
            return _inner.ShowDialog(configure);
        }
    }

    /// <inheritdoc/>
    /// <remarks>表示前の非同期の初期化（<paramref name="configure"/>）から閉じるまでを抑制する。</remarks>
    public async Task<bool?> ShowDialogAsync<TDialog>(Func<TDialog, Task>? configure = null) where TDialog : Window
    {
        using (_beginSuppression())
        {
            return await _inner.ShowDialogAsync(configure);
        }
    }

    /// <inheritdoc/>
    /// <remarks>表示前の非同期の初期化（<paramref name="configure"/>）から閉じるまでを抑制する。</remarks>
    public async Task<bool?> ShowDialogAsync<TDialog, TViewModel>(Func<TViewModel, Task> configure)
        where TDialog : Window, IViewModelDialog<TViewModel>
        where TViewModel : class
    {
        using (_beginSuppression())
        {
            return await _inner.ShowDialogAsync<TDialog, TViewModel>(configure);
        }
    }
}
