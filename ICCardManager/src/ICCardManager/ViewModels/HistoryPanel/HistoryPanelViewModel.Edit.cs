using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Models;
using Microsoft.Extensions.Logging;

namespace ICCardManager.ViewModels;

public partial class HistoryPanelViewModel
{
    // === 履歴行の追加・削除・変更 ===

    #region 履歴行の追加・削除・変更（Issue #635）

    /// <summary>
    /// 履歴行を追加
    /// </summary>
    [RelayCommand]
    public async Task AddLedgerRow()
    {
        if (HistoryCard == null) return;

        // 認証
        var authResult = await _staffAuthService.RequestAuthenticationAsync("履歴の追加");
        if (authResult == null) return;

        // ダイアログ表示
        var allLedgers = HistoryLedgers.ToList();

        // Issue #1740: 一覧の先頭がカードの履歴の先頭でもあるときだけ、先頭への挿入で
        // 直前残高 0 を起点にしてよい。1ページ目に繰越行が無いことは「表示期間より前に
        // 履歴が無い」ことを意味する（BuildCarryoverRow は繰越額が取れない場合のみ null を返す）。
        var historyStartsAtCardBeginning =
            HistoryCurrentPage == 1 && allLedgers.FirstOrDefault()?.IsCarryoverRow != true;

        var result = await _navigationService.ShowDialogAsync<Views.Dialogs.LedgerRowEditDialog>(
            async d => await d.InitializeForAddAsync(
                HistoryCard.CardIdm, allLedgers, authResult.Idm, historyStartsAtCardBeginning));

        if (result == true)
        {
            await LoadHistoryLedgersAsync();
            await Host.RefreshDashboardAsync();
            await Host.CheckWarningsAsync();
            await CheckAndNotifyConsistencyAsync();
        }
    }

    /// <summary>
    /// 履歴行を削除
    /// </summary>
    [RelayCommand]
    public async Task DeleteLedgerRow(LedgerDto ledger)
    {
        if (ledger == null) return;

        // 認証
        var authResult = await _staffAuthService.RequestAuthenticationAsync("履歴の削除");
        if (authResult == null) return;

        // 確認（Issue #1574: 貸出中レコードの場合は専用の警告メッセージ）
        var confirmMessage = ledger.IsLentRecord
            ? $"以下の履歴は「貸出中」状態のレコードです。\n\n" +
              $"日付: {ledger.DateDisplay}\n摘要: {ledger.Summary}\n残高: {ledger.BalanceDisplay}円\n\n" +
              "削除すると、このカードの貸出中状態も解消されます\n" +
              "（他に貸出中レコードが残っている場合は維持されます）。\n\n" +
              "通常は、メイン画面で交通系ICカードをタッチして返却操作を\n" +
              "行うのが正しい復旧方法です。それでも削除しますか？"
            : $"以下の履歴を削除してよろしいですか？\n\n日付: {ledger.DateDisplay}\n摘要: {ledger.Summary}\n残高: {ledger.BalanceDisplay}円";

        if (!_navigationService.ShowWarningConfirmation(confirmMessage, "履歴の削除")) return;

        await DeleteLedgerRowCoreAsync(ledger);
    }

    /// <summary>
    /// 履歴 1 行を削除し、監査ログを同一トランザクションで記録する（Issue #1458 / Issue #1944）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1944: <c>DeleteAsync</c> が <c>false</c> を返すのは<b>影響行数 0</b>のとき、つまり
    /// 共有モードで他 PC が同じ行を先に削除した競合のときだけである（Issue #1753）。
    /// 旧実装は戻り値を捨てていたため、削除していないのに
    /// ①6 年保存の <c>operation_log</c> へ「削除した」という虚偽の監査記録をコミットし、
    /// ②<c>ic_card.is_lent</c> まで解除し、③UI は成功として戻っていた。
    /// 履歴の個別削除は職員認証＋確認を経る操作（Issue #635）であり、その記録が事実と食い違うと
    /// 訂正の追跡ができなくなる。
    /// </para>
    /// <para>
    /// 履歴一覧からの削除（<see cref="DeleteLedgerRow"/>）と行編集ダイアログからの削除要求
    /// （Issue #750。<see cref="EditLedgerWithAuthAsync"/>）は同じ論理的な書き込みなので、
    /// 経路ごとに同じ防御を配らずここへ寄せる
    /// （<c>.claude/rules/development-conventions.md</c>「同じ論理的な書き込みに手段が 2 通りあるか」）。
    /// 後者はモーダルダイアログを実体化するため ViewModel 単体テストから到達できず、
    /// 寄せることでしか回帰を担保できない（静的検査は <c>RepositoryDeleteResultConventionTests</c>）。
    /// なお <see cref="DeleteLedgerRow"/> は Issue #635 で一覧の削除ボタン用に作られたが、
    /// Issue #750 でそのボタンが変更ダイアログ内へ移ったため <c>DeleteLedgerRowCommand</c> に
    /// XAML のバインドは無く、<b>現在 UI から到達するのは後者だけ</b>である。
    /// 前者は認証・確認の手順を保持したまま残っており、結果として
    /// <b>本メソッドを挙動テストから踏める唯一の入口</b>になっている。
    /// </para>
    /// <para>
    /// Issue #1727「エラー通知を、失敗したサブシステムに依存させない」を本メソッドには適用していない。
    /// 競合の案内は再読込 4 連の<b>後ろ</b>にあるため、その途中で例外が出ると案内は失われる。
    /// ただし #1727 が対象とするのは「通知したい失敗と後処理の失敗が<b>同じ原因</b>で起き、
    /// 修正が対象とするまさにその状況でだけ通知が消える」形であり、ここは違う —
    /// <c>deleted == false</c> は「接続はできていて行だけが無い」ことを意味し（接続断なら
    /// <c>DeleteAsync</c> 自身が例外になる）、その直後の再読込の失敗は独立した事象である。
    /// 加えて案内文が「一覧を再読み込みしました」と述べる以上、再読込より前へ出すこともできない。
    /// 何も書き込んでいないため再試行は安全で、害は「汎用のエラー表示になる」ことに留まる。
    /// </para>
    /// </remarks>
    private async Task DeleteLedgerRowCoreAsync(LedgerDto ledger)
    {
        // Issue #1759: 案内で名指しする識別情報は、一覧の再読込より前に確定させる
        // （再読込は HistoryLedgers を作り直すため、後から一覧を辿ると対象が失われる）。
        var target = $"履歴「{ledger.DateDisplay} {ledger.Summary}」";

        var fullLedger = await _ledgerRepository.GetByIdAsync(ledger.Id);
        var deleted = false;

        // Issue #1760: 読み取りが null なら書き込みも行わない。
        // これは DELETE が 0 行になるのと同じ競合（他 PC が先に削除した）なので、
        // 無言で戻らず下で同じ案内を出す。
        if (fullLedger != null)
        {
            // Issue #1458: Ledger DELETE と監査ログ INSERT を同一トランザクションで実行
            using (var scope = await _dbContext.BeginTransactionAsync())
            {
                deleted = await _ledgerRepository.DeleteAsync(ledger.Id, scope.Transaction);
                if (deleted)
                {
                    await _operationLogger.LogLedgerDeleteAsync(fullLedger, scope.Transaction);
                    scope.Commit();
                }
                else
                {
                    // 監査ログを書かずに巻き戻す。業務的失敗による巻き戻しなので
                    // 素の Rollback() でよい（Issue #1831 の対象は catch 内の巻き戻し）。
                    scope.Rollback();
                }
            }

            if (deleted)
            {
                // Issue #1574: 貸出中レコードを削除した場合、ic_card.is_lent を整合性リセット。
                // Issue #1760: 書き込みをやめたら、書き込みに紐付いた副作用もやめる。
                await ResetIsLentIfNoOtherLentRecordsAsync(fullLedger);
            }
        }

        // Issue #1753: 競合の文言が「一覧を再読み込みしました」と述べる以上、案内より前に再読込する。
        // 成否によらず再読込するのは、競合＝他 PC が実際にデータを変えたということであり、
        // 一覧だけでなくダッシュボード・警告も古くなっているため。
        await LoadHistoryLedgersAsync();
        await Host.RefreshDashboardAsync();
        await Host.CheckWarningsAsync();
        await CheckAndNotifyConsistencyAsync();

        if (!deleted)
        {
            _navigationService.ShowError(
                ConcurrencyConflictMessage.ForDelete(target, "履歴一覧"), "削除エラー");
        }
    }

    /// <summary>
    /// 削除した履歴が貸出中レコードだった場合、同じカードに他の貸出中レコードが残っていなければ
    /// <c>ic_card.is_lent</c> を false にリセットする（Issue #1574）。
    /// </summary>
    /// <remarks>
    /// 多重貸出中の異常状態（複数の貸出中レコードが残っている場合）では <c>is_lent=true</c> を維持し、
    /// 段階的な復旧を可能にする。
    /// </remarks>
    private async Task ResetIsLentIfNoOtherLentRecordsAsync(Ledger deletedLedger)
    {
        if (deletedLedger == null || !deletedLedger.IsLentRecord) return;
        if (string.IsNullOrEmpty(deletedLedger.CardIdm)) return;

        var hasOther = await _ledgerRepository.HasOtherLentRecordsAsync(deletedLedger.CardIdm, deletedLedger.Id);
        if (!hasOther)
        {
            // Issue #1953: 影響行数 0 は競合（他 PC がこのカードを論理削除した）。
            // ここは履歴削除のコミット確定後に走る後処理であり、成否の判定には巻き込まない
            // （#1805）。削除済みカードは論理削除の条件が is_lent = 0 のため
            // is_lent は既に 0 であり、放置しても運用に影響しない。ただし
            // 「無言で握りつぶさない」ため本番ログ（Information 以上）へ痕跡を残す。
            var updated = await _cardRepository.UpdateLentStatusAsync(
                deletedLedger.CardIdm, isLent: false, lentAt: null, staffIdm: null);
            if (!updated)
            {
                _logger?.LogWarning(
                    "Issue #1953: 履歴削除後の貸出状態リセットが競合しました。" +
                    "他のパソコンでこのカードが削除された可能性があります: CardIdm={CardIdm}",
                    IdmMasker.Mask(deletedLedger.CardIdm));
                return;
            }

            // Issue #2159: is_lent を戻したら、メイン画面の「貸出中」一覧も読み直す。
            // 抽出前はここで読み直しておらず、次のカード操作か共有モードの定期更新まで
            // 貸出中でなくなったカードが一覧に残っていた（境界を明示して表面化した）。
            // 競合（影響行数 0）のときは何も変えていないので読み直さない。
            await Host.RefreshLentCardsAsync();
        }
    }

    /// <summary>
    /// 履歴を変更
    /// </summary>
    [RelayCommand]
    public async Task EditLedger(LedgerDto ledger)
    {
        if (ledger == null) return;

        // 認証
        var authResult = await _staffAuthService.RequestAuthenticationAsync("履歴の変更");
        if (authResult == null) return;

        await EditLedgerWithAuthAsync(ledger, authResult.Idm, showSaveAndNext: true);
    }

    /// <summary>
    /// 編集対象行の直前行の残高を、履歴一覧の表示順から求める（Issue #1740）。
    /// 直前行が表示範囲に無い場合（ページ先頭行など）は null を返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 供給源に <see cref="HistoryLedgers"/> を使うのは、追加モードの挿入位置プレビューと同じ並び
    /// （<see cref="Services.LedgerOrderHelper.ReorderByBalanceChain"/> で残高チェーン順に整列済み）
    /// を起点にするため。同一日内の利用系レコードは時刻をすべて 00:00:00 で保存するため、
    /// 日付や id から直前行を引くと同日統合（Issue #837）の影響で時系列と食い違う（Issue #1731）。
    /// </para>
    /// <para>
    /// 1ページ目の先頭には繰越行（Issue #1155、<see cref="BuildCarryoverRowAsync"/>）が入るため、
    /// 表示期間の最初の実データ行にも直前行の残高が供給される。繰越行の Id は 0 で実レコードと衝突しない。
    /// </para>
    /// <para>
    /// null を返した場合、編集ダイアログ側は自動計算を無効化して手入力のみとする。
    /// 「前行が無いから 0 から計算する」としてはならない（Issue #1740 の不具合そのもの）。
    /// </para>
    /// </remarks>
    internal int? FindPreviousBalanceForEdit(LedgerDto ledger)
    {
        var index = IndexOfHistoryLedger(ledger);
        return index > 0 ? HistoryLedgers[index - 1].Balance : (int?)null;
    }

    /// <summary>
    /// 履歴一覧における行の位置を返す（見つからない場合は -1）。
    /// </summary>
    /// <remarks>
    /// Issue #1740: 「保存して次へ」「次へ」「戻る」と自動計算の起点特定が同じ索引を使う。
    /// 別々に書くと、照合条件を変えたときに片方だけ直して「次へが開く行」と
    /// 「自動計算の起点」がずれる。
    /// </remarks>
    private int IndexOfHistoryLedger(LedgerDto ledger)
    {
        if (ledger == null) return -1;

        for (int i = 0; i < HistoryLedgers.Count; i++)
        {
            if (HistoryLedgers[i].Id == ledger.Id) return i;
        }
        return -1;
    }

    /// <summary>
    /// 履歴一覧の指定位置にある行が編集可能か（Issue #1740）。
    /// </summary>
    /// <remarks>
    /// 繰越行（<see cref="BuildCarryoverRow"/>）は DB に実体を持たない表示専用の合成行で、
    /// 一覧では修正ボタンを隠している。「次へ」「戻る」のナビゲーションにも同じガードが要る
    /// （無いと全項目空欄のダイアログが開き、存在しない行を編集させられているように見える）。
    /// </remarks>
    private bool IsEditableHistoryLedger(int index)
    {
        if (index < 0 || index >= HistoryLedgers.Count) return false;
        return !HistoryLedgers[index].IsCarryoverRow;
    }

    /// <summary>
    /// 認証済みの状態で履歴を編集（Issue #1134: 「保存して次へ」ループ対応）
    /// </summary>
    private async Task EditLedgerWithAuthAsync(LedgerDto ledger, string operatorIdm, bool showSaveAndNext = false)
    {
        var cardName = HistoryCard?.DisplayName;

        // Issue #1740: 残高の自動計算に使う直前行の残高を、ダイアログを開く前に確定させる
        var previousBalance = FindPreviousBalanceForEdit(ledger);

        // Issue #2007: 導入行なら、導入時残高の誤りの訂正案をダイアログへ渡す
        var initialBalanceCorrection = await ResolveInitialBalanceCorrectionForEditAsync(ledger);

        // 全項目編集ダイアログ表示
        Views.Dialogs.LedgerRowEditDialog capturedEditDialog = null;
        var dialogResult = await _navigationService.ShowDialogAsync<Views.Dialogs.LedgerRowEditDialog>(
            async d =>
            {
                await d.InitializeForEditAsync(ledger, operatorIdm, previousBalance, initialBalanceCorrection);
                if (showSaveAndNext)
                {
                    d.SetShowSaveAndNextButton(true);
                }
                if (!string.IsNullOrEmpty(cardName))
                {
                    d.SetBreadcrumb($"{cardName} > 行修正");
                }
                capturedEditDialog = d;
            });

        // Issue #750: 削除がリクエストされた場合
        if (capturedEditDialog?.IsDeleteRequested == true)
        {
            // Issue #1944: 削除と監査ログの書き込みは DeleteLedgerRow と同一の経路へ寄せる。
            // 旧実装はここにも同じコードの写しを持ち、そちらだけが DeleteAsync の戻り値を
            // 捨てたまま残る形だった。
            await DeleteLedgerRowCoreAsync(ledger);
        }
        else if (dialogResult == true)
        {
            await LoadHistoryLedgersAsync();
            await Host.RefreshDashboardAsync();
            await Host.CheckWarningsAsync();
            await CheckAndNotifyConsistencyAsync();

            // Issue #1134: 「保存して次へ」が要求された場合、次の行を開く
            if (capturedEditDialog?.IsSaveAndEditNextRequested == true)
            {
                await EditAdjacentLedgerAsync(ledger, operatorIdm, offset: 1);
            }
        }
        // Issue #1134: 「次へ（保存しない）」が要求された場合
        else if (capturedEditDialog?.IsSkipToNextRequested == true)
        {
            await EditAdjacentLedgerAsync(ledger, operatorIdm, offset: 1);
        }
        // Issue #1134: 「戻る」が要求された場合
        else if (capturedEditDialog?.IsBackRequested == true)
        {
            await EditAdjacentLedgerAsync(ledger, operatorIdm, offset: -1);
        }
    }

    /// <summary>
    /// 履歴一覧で隣接する行の編集ダイアログを開く（Issue #1134 の「次へ」「戻る」）。
    /// </summary>
    /// <remarks>
    /// Issue #1740: 隣が繰越行（DB に実体を持たない合成行）の場合は何もしない。
    /// ガードが無いと全項目空欄のダイアログが開く。
    /// </remarks>
    private async Task EditAdjacentLedgerAsync(LedgerDto ledger, string operatorIdm, int offset)
    {
        var currentIndex = IndexOfHistoryLedger(ledger);
        if (currentIndex < 0) return;

        var targetIndex = currentIndex + offset;
        if (!IsEditableHistoryLedger(targetIndex)) return;

        await EditLedgerWithAuthAsync(HistoryLedgers[targetIndex], operatorIdm, showSaveAndNext: true);
    }

    #endregion
}
