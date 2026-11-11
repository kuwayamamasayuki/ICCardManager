#nullable enable
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
        using var authResult = (await _staffAuthService.RequestAuthenticationAsync("履歴の統合"))?.Enter();
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
        var mergeResult = await _ledgerMergeService.MergeAsync(ledgerIds);

        // Issue #2373: 統合履歴（取り消し用の情報）は統合と同じトランザクションで保存するため、
        // 「統合は完了したが取り消せない」状態は起きない（保存できなければ統合ごと巻き戻り、下の失敗分岐で案内する）。
        if (mergeResult.Success)
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

        var items = ToMergeHistoryItems(histories);

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
    /// 統合履歴を、新しい順の表示用アイテムにする。
    /// Issue #2374: 統合した後の変更を確かめられるか（<see cref="Services.MergeHistoryEntry.CanVerifyChangesAfterMerge"/>）を
    /// 確認の文言（<see cref="BuildUnmergeConfirmationMessage"/>）まで運ぶ。
    /// </summary>
    internal static List<Views.Dialogs.MergeHistoryItem> ToMergeHistoryItems(IEnumerable<Services.MergeHistoryEntry> histories)
        => histories
            .OrderByDescending(h => h.MergedAt)
            .Select(h => new Views.Dialogs.MergeHistoryItem
            {
                Id = h.Id,
                MergedAtDisplay = DisplayFormatters.FormatDateTime(h.MergedAt),
                Description = h.Description,
                CanVerifyChangesAfterMerge = h.CanVerifyChangesAfterMerge
            })
            .ToList();

    /// <summary>
    /// 取り消す統合を名指しして確認し、同意されたときだけ取り消す（Issue #2141）
    /// </summary>
    /// <returns>取り消しへ進んだ場合 true（取り消しの成否は問わない）。確認で「いいえ」・認証が完了しなかった場合 false</returns>
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
    /// <para>
    /// Issue #2265: 統合の取り消しは統合先を書き戻し統合元を挿入し直す、統合と同じ重さで台帳を改変する操作のため、
    /// 統合と同じく職員証の認証を求める（SEQ-AUTH-01）。認証は取り消す対象を確かめた<b>後</b>に求める
    /// （対象を選んで同意してから職員証をタッチする。職員の復元 #2263 と同じ順序）。取り消しは
    /// 認証した 1 操作のスコープ（#2260）の内側で行う。
    /// </para>
    /// </remarks>
    internal async Task<bool> ConfirmAndExecuteUnmergeAsync(Views.Dialogs.MergeHistoryItem item)
    {
        if (!_navigationService.ShowWarningConfirmation(BuildUnmergeConfirmationMessage(item), "統合の取り消し"))
        {
            return false;
        }

        using var authResult = (await _staffAuthService.RequestAuthenticationAsync("統合の取り消し"))?.Enter();
        if (authResult == null)
        {
            // 同意した直後に何も起きないと、取り消されたのかどうかが分からない（認証のタイムアウトは自動で閉じる）
            _navigationService.ShowInformation(UnmergeAuthenticationCancelledMessage, "統合の取り消し");
            return false;
        }

        await ExecuteUnmergeAsync(item.Id);
        return true;
    }

    /// <summary>
    /// 統合の取り消しで職員証の認証が完了しなかったときの案内（Issue #2265）
    /// </summary>
    internal const string UnmergeAuthenticationCancelledMessage =
        "職員証の認証が完了しなかったため、統合を取り消していません。" +
        "取り消すには、もう一度「統合を元に戻す」から取り消す統合を選び、職員証で認証してください。";

    /// <summary>
    /// 統合の取り消し確認の文言（Issue #2141）。取り消す対象を統合日時と内容で名指しする。
    /// </summary>
    /// <remarks>
    /// Issue #2374: 統合した後の変更を確かめられない統合履歴（統合した時点の明細の内容・金額を持たない、
    /// 以前のバージョンで行った統合）は、取り消し自体は許すが、確認に
    /// <see cref="UnverifiableUnmergeWarning"/> を加える。確かめられる統合履歴は従来どおりの文言
    /// （統合後に変更していれば取り消しは中止され、変更は失われないため警告しない）。
    /// </remarks>
    internal static string BuildUnmergeConfirmationMessage(Views.Dialogs.MergeHistoryItem item)
        => "次の統合を取り消して、統合する前の履歴に戻します。\n\n" +
           $"統合日時: {item.MergedAtDisplay}\n" +
           $"内容: {item.Description}\n\n" +
           (item.CanVerifyChangesAfterMerge ? string.Empty : UnverifiableUnmergeWarning + "\n\n") +
           "取り消してよろしいですか？";

    /// <summary>
    /// Issue #2374: 統合した後の変更を確かめられない統合履歴を取り消すときの警告（何が・なぜ・どうすれば）。
    /// </summary>
    internal const string UnverifiableUnmergeWarning =
        "【注意】この統合は以前のバージョンで行ったため、統合した後にこの履歴の明細や金額を変更したかどうかを確かめられません。" +
        "変更していた場合、取り消すとその変更は失われ、統合する前の内容に戻ります。\n" +
        "統合した後に変更したか分からないときは「いいえ」を選び、利用履歴詳細で明細と金額を確かめてください。" +
        "変更していたときは取り消さずに、履歴の分割・変更で手作業により整えてください。";

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
