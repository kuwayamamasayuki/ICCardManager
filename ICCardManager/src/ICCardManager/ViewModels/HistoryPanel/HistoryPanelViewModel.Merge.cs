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
    // === 履歴統合・取り消し ===

    #region 履歴統合（Issue #548）

    /// <summary>
    /// 元に戻せる統合履歴が存在するか（「統合を元に戻す」ボタンの有効/無効制御用）
    /// </summary>
    private bool _hasUndoableMergeHistories;

    /// <summary>
    /// チェックされた履歴を取得
    /// </summary>
    private List<LedgerDto> GetCheckedLedgers()
    {
        return HistoryLedgers.Where(d => d.IsChecked).ToList();
    }

    /// <summary>
    /// チェックボックスの変更を監視するためのハンドラを登録
    /// </summary>
    private void SubscribeLedgerCheckedChanged(LedgerDto dto)
    {
        dto.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(LedgerDto.IsChecked))
            {
                MergeHistoryLedgersCommand.NotifyCanExecuteChanged();
            }
        };
    }

    /// <summary>
    /// チェックされた履歴を統合
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMergeHistoryLedgers))]
    public async Task MergeHistoryLedgers()
    {
        var checkedDtos = GetCheckedLedgers();
        if (checkedDtos.Count < 2)
        {
            return;
        }

        // 隣接チェック: チェックされたアイテムがHistoryLedgers内で連続しているか
        var indices = checkedDtos
            .Select(dto => HistoryLedgers.IndexOf(dto))
            .OrderBy(i => i)
            .ToList();

        for (int i = 1; i < indices.Count; i++)
        {
            if (indices[i] != indices[i - 1] + 1)
            {
                _navigationService.ShowWarning(
                    "隣接する履歴のみ統合できます。\n連続した行にチェックを入れてください。",
                    "統合できません");
                return;
            }
        }

        // 表示順（古い順）でソートされたDTOリスト
        var sortedDtos = indices.Select(i => HistoryLedgers[i]).ToList();

        // 履歴統合は ledger を改変する監査対象の重要操作のため職員認証を要求する
        // （設計 06_シーケンス図 §10 / SEQ-AUTH-01。追加・削除・変更と同じゲート）
        var authResult = await _staffAuthService.RequestAuthenticationAsync("履歴の統合");
        if (authResult == null)
        {
            return;
        }

        // 確認ダイアログ
        var message = "以下の履歴を統合します。\n\n";
        foreach (var dto in sortedDtos)
        {
            message += $"  • {dto.DateDisplay}  {dto.Summary}  残高:{dto.BalanceDisplay}\n";
        }
        message += "\n統合してよろしいですか？（統合後に「元に戻す」ことができます）";

        if (!_navigationService.ShowConfirmation(message, "履歴の統合"))
        {
            return;
        }

        // 統合実行
        var ledgerIds = sortedDtos.Select(dto => dto.Id).ToList();
        var mergeResult = await _ledgerMergeService.MergeAsync(ledgerIds, authResult.Idm);

        if (mergeResult.Success && mergeResult.HasPostCommitFailure)
        {
            // Issue #1954: 統合は確定済み。再実行を促すと「統合対象の履歴が見つかりません」に
            // 行き着くだけなので、再実行を促さず「取り消せない」ことと代替手段を案内する（Issue #1725）。
            // Issue #1727: 通知は再読込より「前」に出す。取り消し情報の保存が失敗する原因
            // （共有フォルダーの切断・DB ロック・他 PC との競合）は一覧再読込・ダッシュボード更新も
            // 同じように失敗させるため、後ろに置くと「統合は完了済み・やり直し不要」という
            // 肝心の案内が、まさにこの分岐が対象とする状況でだけ失われる（例外は非同期コマンドの
            // 外へ抜けて誰にも観測されない）。
            _navigationService.ShowWarning(
                "履歴の統合は完了しましたが、「元に戻す」ための取り消し情報を記録できませんでした。\n" +
                "他のパソコンとの競合や、データベースの保存先への一時的な接続不良が原因の可能性があります。\n\n" +
                "統合そのものはやり直す必要はありません。この統合は「統合を元に戻す」では取り消せないため、" +
                "内容を戻したい場合は履歴一覧の分割・編集で修正してください。",
                "統合完了（取り消し情報なし）");

            // 再読込自体の失敗は上の案内と同じ原因なので、ここで握って二重のダイアログにしない
            // （ExecuteUnmergeAsync の失敗分岐と同じ作法）。
            try
            {
                await LoadHistoryLedgersAsync();
                await Host.RefreshDashboardAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to reload history after merge undo-save failure");
            }

            UndoMergeHistoryLedgersCommand.NotifyCanExecuteChanged();
        }
        else if (mergeResult.Success)
        {
            await LoadHistoryLedgersAsync();
            await Host.RefreshDashboardAsync();
            UndoMergeHistoryLedgersCommand.NotifyCanExecuteChanged();

            _navigationService.ShowInformation(
                "履歴を統合しました。\n「統合を元に戻す」ボタンで取り消せます。",
                "統合完了");
        }
        else
        {
            // Issue #1753: 失敗時も一覧を再読込する。共有モードでは他 PC が同じ履歴を統合・削除した
            // ことが失敗要因になり得るため、古い一覧のままだと再試行しても同じエラーで止まる。
            // エラー文言（「画面を最新の状態に更新してから再度お試しください」）とも整合させる。
            // 通知を先に出すのは、再読込が同じ原因（共有フォルダーの切断・DB ロック）で失敗しても
            // 案内を届けるため（#1727。ExecuteUnmergeAsync の失敗分岐と同じ順序・同じ握り方）。
            _navigationService.ShowError(mergeResult.ErrorMessage, "統合エラー");

            try
            {
                await LoadHistoryLedgersAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to reload history after merge failure");
            }
        }
    }

    /// <summary>
    /// 統合コマンドの実行可否
    /// </summary>
    private bool CanMergeHistoryLedgers()
    {
        var checkedDtos = GetCheckedLedgers();

        if (checkedDtos.Count < 2)
        {
            return false;
        }

        // 同一カードかチェック
        if (checkedDtos.Select(d => d.CardIdm).Distinct().Count() > 1)
        {
            return false;
        }

        // 貸出中レコードがないかチェック
        if (checkedDtos.Any(d => d.IsLentRecord))
        {
            return false;
        }

        // チャージと利用の混在チェック
        if (checkedDtos.Any(d => d.Income > 0) && checkedDtos.Any(d => d.Expense > 0))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 統合取り消しコマンドの実行可否
    /// </summary>
    private bool CanUndoMergeHistoryLedgers() => _hasUndoableMergeHistories;

    /// <summary>
    /// 元に戻せる統合履歴の有無を非同期にチェックし、ボタンの有効/無効を更新する
    /// </summary>
    private async Task RefreshUndoMergeAvailabilityAsync()
    {
        var histories = await _ledgerMergeService.GetUndoableMergeHistoriesAsync();
        _hasUndoableMergeHistories = histories.Count > 0;
        UndoMergeHistoryLedgersCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 過去の統合を元に戻す（ダイアログで履歴を選択）
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUndoMergeHistoryLedgers))]
    public async Task UndoMergeHistoryLedgers()
    {
        // DBから元に戻せる統合履歴を取得
        var histories = await _ledgerMergeService.GetUndoableMergeHistoriesAsync();

        if (histories.Count == 0)
        {
            _hasUndoableMergeHistories = false;
            UndoMergeHistoryLedgersCommand.NotifyCanExecuteChanged();
            return;
        }

        // 新しい順に表示用アイテムを作成
        var items = histories
            .OrderByDescending(h => h.MergedAt)
            .Select(h => new Views.Dialogs.MergeHistoryItem
            {
                Id = h.Id,
                MergedAtDisplay = DisplayFormatters.FormatDateTime(h.MergedAt),
                Description = h.Description
            })
            .ToList();

        // 選択ダイアログを表示
        var dialog = new Views.Dialogs.MergeHistoryDialog(items)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true && dialog.SelectedHistoryId.HasValue)
        {
            var selectedId = dialog.SelectedHistoryId.Value;
            var selected = items.FirstOrDefault(i => i.Id == selectedId);
            if (selected != null)
            {
                await ConfirmAndExecuteUnmergeAsync(selected);
            }
        }
    }

    /// <summary>
    /// 取り消す統合を名指しして確認し、同意されたときだけ取り消す（Issue #2141）
    /// </summary>
    /// <returns>取り消しへ進んだ場合 true（取り消しの成否は問わない）</returns>
    /// <remarks>
    /// <para>
    /// 統合履歴の一覧はダブルクリックでも閉じる（「選択した統合を元に戻す」と同じ結果になる）。
    /// 旧実装は確認なしで取り消しへ進み、しかもダブルクリックが一覧全体に付いていたため、
    /// 行を選んだあとのスクロールバーや列見出しのダブルクリックでも 6 年保存の台帳が書き換わった。
    /// </para>
    /// <para>
    /// 確認はダイアログの外（ここ）の 1 か所に置く。ボタンとダブルクリックの両方の経路が通るため、
    /// 片方にだけ確認がある状態を作らない（#2080「確認を入れるなら両方に入れる」）。
    /// <see cref="Views.Dialogs.MergeHistoryDialog"/> は <c>Window</c> を直接生成するため単体テストから踏めず、
    /// 確認と実行をこのメソッドへ切り出して検査できるようにしている。
    /// </para>
    /// </remarks>
    internal async Task<bool> ConfirmAndExecuteUnmergeAsync(Views.Dialogs.MergeHistoryItem item)
    {
        if (!_navigationService.ShowWarningConfirmation(BuildUnmergeConfirmationMessage(item), "統合の取り消し"))
        {
            return false;
        }

        await ExecuteUnmergeAsync(item.Id);
        return true;
    }

    /// <summary>
    /// 統合の取り消し確認の文言（Issue #2141）。取り消す対象を統合日時と内容で名指しする。
    /// </summary>
    internal static string BuildUnmergeConfirmationMessage(Views.Dialogs.MergeHistoryItem item)
        => "次の統合を取り消して、統合する前の履歴に戻します。\n\n" +
           $"統合日時: {item.MergedAtDisplay}\n" +
           $"内容: {item.Description}\n\n" +
           "取り消してよろしいですか？";

    /// <summary>
    /// undo実行の共通処理
    /// </summary>
    private async Task ExecuteUnmergeAsync(int mergeHistoryId)
    {
        var undoResult = await _ledgerMergeService.UnmergeAsync(mergeHistoryId);

        if (undoResult.Success)
        {
            await LoadHistoryLedgersAsync();
            await Host.RefreshDashboardAsync();
            UndoMergeHistoryLedgersCommand.NotifyCanExecuteChanged();
            _navigationService.ShowInformation("統合を元に戻しました。", "取り消し完了");
        }
        else
        {
            _navigationService.ShowError(undoResult.ErrorMessage, "取り消しエラー");

            // Issue #1806: 失敗時も一覧を再読込する（統合の失敗分岐と同じ #1753 の作法）。
            // 失敗要因は「統合後の編集・削除」「他 PC の先行取り消し」で、いずれも一覧が古いままだと
            // 利用者は案内どおりに履歴を確認できない。通知を先に出すのは、再読込が同じ原因
            // （共有フォルダーの切断・DB ロック）で失敗しても案内を届けるため（#1727）。
            // 再読込自体の失敗は上の案内と同じ原因なので、ここで握って二重のエラーダイアログにしない。
            try
            {
                // LoadHistoryLedgersAsync は履歴カード選択中なら末尾で RefreshUndoMergeAvailabilityAsync を呼ぶ。
                // 未選択（早期 return）のときだけボタン状態を別途更新する（同じ問い合わせを 2 回投げない）。
                await LoadHistoryLedgersAsync();
                if (HistoryCard == null)
                {
                    await RefreshUndoMergeAvailabilityAsync();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to reload history after unmerge failure (history {HistoryId})", mergeHistoryId);
            }
        }
    }

    #endregion
}
