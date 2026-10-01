using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
using ICCardManager.Common.Messages;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Views.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ICCardManager.ViewModels;

public partial class MainViewModel
{
    // === 返却後処理（バス停名・同行者数・返却確認） ===

    /// <summary>
    /// 返却成功時の共通後処理（Issue #1577）。
    /// </summary>
    /// <remarks>
    /// 通常の返却フロー（<see cref="ProcessReturnAsync"/>）と仮想タッチ
    /// （<c>ProcessVirtualTouchAsync</c>、DEBUG ビルド限定）の双方から呼び出される。
    /// 仮想タッチは <c>#if DEBUG</c> ブロック内に定義されており Release 構成では
    /// 存在しないため、cref ではなくプレーン表記で参照する（Issue #1623）。
    /// バス停入力ダイアログ・履歴再読み込み・警告再チェック等の追従処理を
    /// 1か所にまとめ、片側だけ追加されて他方に反映されない事故を防ぐ。
    /// テストから挙動を検証できるよう <c>internal</c> 公開する。
    /// </remarks>
    internal async Task HandleReturnSuccessAsync(IcCard card, LendingResult result)
    {
        if (result.HasPostCommitFailure)
        {
            // Issue #1805: 返却は台帳に記録済みだが、コミット後の付帯情報（残額・残額警告）を取得できなかった。
            // result.Balance / IsLowBalance / WarningBalance は信頼できないため残額付きの返却通知は出さない。
            // 「もう一度タッチ」と案内すると30秒ルールの逆処理で記録済みの返却が取り消される（#1725 と同じ判断）ため、
            // 「記録済み」＋「再タッチしないでください」＋中立的な警告音で案内する（エラー音は事実と矛盾する）。
            NotifyRecordedButIncomplete("返却", "残額を確認できませんでした。");
        }
        else
        {
            // 残高はLendingServiceで設定済み（カードから直接読み取った値を優先）
            _soundPlayer.Play(SoundType.Return);

            // トースト通知を表示（表示位置は設定に従う、フォーカスを奪わない）
            _toastNotificationService.ShowReturnNotification(card.CardType, card.CardNumber, result.Balance, result.IsLowBalance, result.WarningBalance);

            // Issue #1908: 返却が完了し残額も確定したので、食い違い警告は解消している。
            // HasPostCommitFailure（残額を解決できなかった）の側では消さない。
            ClearCardBalanceMismatchWarning(card.CardIdm);
        }

        // メイン画面は変更しない（Issue #186: 職員の操作を妨げない）

        await RefreshLentCardsAsync();
        await RefreshDashboardAsync();

        // 履歴が開いていれば再読み込み（Issue #889）
        if (History.IsHistoryVisible)
        {
            // Issue #1923: 返却は「カードをタッチした職員」の操作であり、履歴画面で行を選んでいる職員の操作ではない。
            // 本システムは 1 台のカードリーダーを複数職員で共有するため、
            // 定期リフレッシュ（RefreshSharedDataAsync）と同じ理由で統合対象のチェックを引き継ぐ。
            await History.LoadHistoryLedgersAsync(preserveCheckedRows: true);
        }

        await CheckWarningsAsync();

        // バス停名入力（バス利用時）・同行者数入力（利用行がある場合）・返却確認の履歴表示（Issue #1907）は
        // いずれも AppSettings を見るため、返却後の設定読み取りは 1 回にまとめる（コミット後の I/O を増やさない。#1805）
        var needsBusStopInput = result.HasBusUsage && result.CreatedLedgers.Count > 0;
        var companionCountTargets = CompanionCountInputViewModel.SelectTargetLedgers(result.CreatedLedgers);
        // 本番の SettingsRepository は null を返さない。null ガードはテストの loose モック（未設定なら null）が
        // 返却後処理そのものを NullReferenceException で落とさないためのもので、null なら各ダイアログ・
        // 返却確認は「表示しない」側へ倒れる（Issue #1907 のコードレビューで指摘）
        var returnDialogSettings = await _settingsRepository.GetAppSettingsAsync();

        // バス利用がある場合はバス停入力画面を表示
        if (needsBusStopInput)
        {
            var settings = returnDialogSettings;

            if (settings != null && !settings.SkipBusStopInputOnReturn)
            {
                // Issue #593: バス利用を含むLedgerをすべて取得（Summaryで判定）
                // LastOrDefaultでは最後のLedgerのみ取得されるため、バス利用が別日にある場合に空ダイアログになる
                var busLedgers = result.CreatedLedgers
                    // Issue #1818: バスラベルは組織設定（SummaryText.BusLabel）由来のため直書きしない
                    .Where(l => !l.IsLentRecord && SummaryGenerator.ContainsBusLabel(l.Summary))
                    .ToList();

                // Issue #1203: 複数のバス利用がある場合でも1つのダイアログでまとめて入力できるようにする
                if (busLedgers.Count > 0)
                {
                    await _navigationService.ShowDialogAsync<Views.Dialogs.BusStopInputDialog>(
                        async d => await d.InitializeWithLedgersAsync(busLedgers));
                }

                // バス停名入力後に履歴が開いていれば再読み込み
                if (busLedgers.Count > 0 && History.IsHistoryVisible)
                {
                    // Issue #1923: バス停名の入力は返却フローの一部（カードをタッチした職員の操作）。
                    // 本システムは 1 台のカードリーダーを複数職員で共有するため、
                    // 定期リフレッシュ（RefreshSharedDataAsync）と同じ理由で統合対象のチェックを引き継ぐ。
                    await History.LoadHistoryLedgersAsync(preserveCheckedRows: true);
                }

                // Issue #660: バス停名入力後に警告メッセージを再チェック
                // バス停名の入力により★が消えた場合、件数を更新し、0件なら非表示にする
                await CheckWarningsAsync();
            }
            // スキップ時は★マークがSummaryGenerator側で自動付与されるため追加処理不要
        }

        // Issue #1906: 複数名で同一の交通系ICカードを利用した場合の同行者数を入力させる。
        // バス停名入力の後に出す（利用行が確定してから氏名欄の表記を決める）。
        // 対象は利用行（払出 > 0）のみで、チャージ・ポイント還元だけの返却では出さない。
        await ShowCompanionCountInputIfNeededAsync(companionCountTargets, returnDialogSettings);

        // Issue #1907: バス停名・同行者数の入力がすべて終わったあとで、返却したカードの履歴を
        // メイン画面に自動表示して確認を促す（入力漏れ・誤りを返却した職員自身がその場で見つける）。
        await History.ShowReturnHistoryReviewAsync(card, result, returnDialogSettings);

        // Issue #596: 今月の履歴が不完全な可能性がある場合に通知
        if (result.MayHaveIncompleteHistory)
        {
            _toastNotificationService.ShowWarning(
                "履歴の確認",
                "今月の利用履歴がすべて取得できていない可能性があります。\nCSVインポートで不足分を補完してください。");
        }
    }

    /// <summary>
    /// Issue #1906: 返却で作られた利用行に対して同行者数入力ダイアログを表示する。
    /// 設定 <see cref="AppSettings.SkipCompanionCountInputOnReturn"/> が有効なら表示しない
    /// （その場合も履歴の行編集から後で入力できる）。
    /// 表示した場合は <see cref="AppSettings.CompanionCountInputTimeoutSeconds"/> 秒で
    /// 「外0名」として自動的に閉じる（Issue #2009。0 なら閉じない）。
    /// </summary>
    private async Task ShowCompanionCountInputIfNeededAsync(List<Ledger> targets, AppSettings settings)
    {
        if (targets.Count == 0 || settings == null || settings.SkipCompanionCountInputOnReturn)
        {
            return;
        }

        // Issue #2009: 入力待ちで止まったままにならないよう、設定した秒数で「外0名」として自動的に閉じる
        // （0 は「自動的に閉じない」＝ 必ず尋ねる運用）
        var autoCloseSeconds = settings.CompanionCountInputTimeoutSeconds;
        await _navigationService.ShowDialogAsync<Views.Dialogs.CompanionCountInputDialog>(
            async d => await d.InitializeWithLedgersAsync(targets, autoCloseSeconds));

        // 同行者数の入力後に履歴が開いていれば再読み込み（氏名欄の「外N名」を反映）
        if (History.IsHistoryVisible)
        {
            // Issue #1923: 同行者数の入力は返却フローの一部（カードをタッチした職員の操作）。
            // 本システムは 1 台のカードリーダーを複数職員で共有するため、
            // 定期リフレッシュ（RefreshSharedDataAsync）と同じ理由で統合対象のチェックを引き継ぐ。
            await History.LoadHistoryLedgersAsync(preserveCheckedRows: true);
        }
    }
}
