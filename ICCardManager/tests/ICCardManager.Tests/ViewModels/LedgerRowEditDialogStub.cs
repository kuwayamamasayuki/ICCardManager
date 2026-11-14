using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// 利用履歴行編集ダイアログの代役（Issue #2475）。履歴パネルの単体テストと、メイン画面を通す連携テストで共用する。
/// </summary>
/// <remarks>
/// <para>
/// <c>INavigationService.ShowDialogAsync&lt;LedgerRowEditDialog, LedgerRowEditViewModel&gt;</c> を差し替え、
/// 本体が渡す初期化（編集モードの <c>InitializeForEditAsync</c>・払戻済の設定、追加モードの <c>InitializeForAddAsync</c>（Issue #2486）など）をそのまま本物の
/// <see cref="LedgerRowEditViewModel"/> に実行させる。続けて職員の操作（削除を押す等）を実行し、
/// 本物のダイアログが閉じたときと同じ <c>DialogResult</c> を返す（保存したときだけ true。削除・キャンセルは false）。
/// 確認ダイアログは ViewModel が渡された <c>INavigationService</c> の <c>ShowWarningConfirmation</c> へ出すので、
/// 確認の応答はそちらへ仕込む。
/// </para>
/// <para>
/// <b>検出できないもの</b>: <c>DialogResult</c> はこの代役が ViewModel のフラグから自分で決める。
/// したがって Window 側（<c>LedgerRowEditDialog.xaml.cs</c>）のクローズ処理（削除要求で <c>DialogResult = false</c> にして閉じる等）の
/// 取り違えは単体テストでは検出できない。実画面で削除してダイアログが閉じ、行が消えることは
/// UI テスト <c>LedgerRowDeleteUiTests</c>（ST-007c No.14・15）が担う。
/// </para>
/// </remarks>
internal static class LedgerRowEditDialogStub
{
    /// <summary>
    /// 行編集ダイアログを本物の ViewModel で再現するよう <paramref name="navigation"/> を設定する。
    /// </summary>
    /// <returns>開いたダイアログの ViewModel（開いた順）</returns>
    public static List<LedgerRowEditViewModel> Arrange(
        Mock<INavigationService> navigation,
        ILedgerRepository ledgerRepository,
        OperationLogger operationLogger,
        DbContext dbContext,
        Action<LedgerRowEditViewModel> userAction)
        => Arrange(navigation, ledgerRepository, operationLogger, dbContext, editor =>
        {
            userAction(editor);
            return Task.CompletedTask;
        });

    /// <summary>
    /// 行編集ダイアログを本物の ViewModel で再現するよう <paramref name="navigation"/> を設定する（職員の操作が非同期のもの）。
    /// </summary>
    /// <remarks>
    /// 「保存」（<c>SaveCommand</c>）のように完了を待つ必要がある操作に使う（Issue #2486）。
    /// <c>Execute</c> で起動すると保存の完了を待たずにダイアログの結果を決めてしまう。
    /// </remarks>
    /// <returns>開いたダイアログの ViewModel（開いた順）</returns>
    public static List<LedgerRowEditViewModel> Arrange(
        Mock<INavigationService> navigation,
        ILedgerRepository ledgerRepository,
        OperationLogger operationLogger,
        DbContext dbContext,
        Func<LedgerRowEditViewModel, Task> userAction)
    {
        var staffRepository = new Mock<IStaffRepository>();
        staffRepository.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());
        var opened = new List<LedgerRowEditViewModel>();
        navigation
            .Setup(n => n.ShowDialogAsync<ICCardManager.Views.Dialogs.LedgerRowEditDialog, LedgerRowEditViewModel>(
                It.IsAny<Func<LedgerRowEditViewModel, Task>>()))
            .Returns(async (Func<LedgerRowEditViewModel, Task> configure) =>
            {
                var editor = new LedgerRowEditViewModel(
                    ledgerRepository, staffRepository.Object, operationLogger, dbContext, navigation.Object);
                await configure(editor);
                opened.Add(editor);
                await userAction(editor);
                return (bool?)(editor.IsSaved || editor.IsSaveAndEditNextRequested);
            });
        return opened;
    }
}
