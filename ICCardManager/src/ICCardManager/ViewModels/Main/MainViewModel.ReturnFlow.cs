using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Views.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;

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
        if (IsHistoryVisible)
        {
            // Issue #1923: 返却は「カードをタッチした職員」の操作であり、履歴画面で行を選んでいる職員の操作ではない。
            // 本システムは 1 台のカードリーダーを複数職員で共有するため、
            // 定期リフレッシュ（RefreshSharedDataAsync）と同じ理由で統合対象のチェックを引き継ぐ。
            await LoadHistoryLedgersAsync(preserveCheckedRows: true);
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
                if (busLedgers.Count > 0 && IsHistoryVisible)
                {
                    // Issue #1923: バス停名の入力は返却フローの一部（カードをタッチした職員の操作）。
                    // 本システムは 1 台のカードリーダーを複数職員で共有するため、
                    // 定期リフレッシュ（RefreshSharedDataAsync）と同じ理由で統合対象のチェックを引き継ぐ。
                    await LoadHistoryLedgersAsync(preserveCheckedRows: true);
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
        await ShowReturnHistoryReviewAsync(card, result, returnDialogSettings);

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
        if (IsHistoryVisible)
        {
            // Issue #1923: 同行者数の入力は返却フローの一部（カードをタッチした職員の操作）。
            // 本システムは 1 台のカードリーダーを複数職員で共有するため、
            // 定期リフレッシュ（RefreshSharedDataAsync）と同じ理由で統合対象のチェックを引き継ぐ。
            await LoadHistoryLedgersAsync(preserveCheckedRows: true);
        }
    }

    /// <summary>
    /// Issue #1907: 返却したカードの利用履歴をメイン画面に自動表示し、記録の確認を促す（返却確認）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 表示期間は当月 1 日からだが、今回記録した行が前月以前（3/31 乗車・4/1 返却）にあれば
    /// <b>その利用があった月の 1 日</b>から表示し、記録した行がすべて画面に収まるようにする。
    /// 今回記録した行は <see cref="LedgerDto.IsRecentlyRecorded"/> で強調する。
    /// </para>
    /// <para>
    /// #186（カードタッチでメイン画面を変更しない＝職員の操作を妨げない）との両立:
    /// 履歴パネルが職員の操作で開いている（手動で開いた／返却確認を操作した）なら、表示中のカードが
    /// 同じでも乗っ取らず、トーストで確認を促すだけにする（同じカードの行を統合のためにチェックしている
    /// 最中に別の職員がそのカードを返却し得る。#1923）。ただし<b>同じカードを表示しているなら
    /// 今回の行の目印（✔）は付ける</b> — カード・表示期間・ページを変えないので #186 の範囲に収まる。
    /// 返却確認が操作されないまま残っているだけなら置き換える。
    /// </para>
    /// <para>
    /// <see cref="LendingResult.HasPostCommitFailure"/>（記録済みだが残額を確認できなかった）でも表示する。
    /// 目的は記録の確認であり、記録は確定しているため。
    /// </para>
    /// </remarks>
    /// <param name="card">返却したカード</param>
    /// <param name="result">返却結果（<see cref="LendingResult.CreatedLedgers"/> を強調対象にする）</param>
    /// <param name="settings">返却後に 1 回だけ読んだ設定。<c>null</c>（読めなかった）なら表示しない</param>
    internal async Task ShowReturnHistoryReviewAsync(IcCard card, LendingResult result, AppSettings? settings)
    {
        if (settings == null || !settings.ShowHistoryOnReturn)
        {
            return;
        }

        // 貸出中レコード（返却で物理削除済み）と未採番の行は対象外。
        // 「記録ゼロ」の判定は履歴パネルの状態より前に行う（コードレビューで検出） — 後ろに置くと、
        // 職員が履歴を開いているときだけ「確認してください」のトーストが出て、しかし確認すべき
        // 記録は 1 行も無い（借りたが使わずに返した／重複除外で全件落ちた）という案内になる。
        var recordedLedgers = (result.CreatedLedgers ?? new List<Ledger>())
            .Where(l => l != null && !l.IsLentRecord && l.Id > 0)
            .ToList();

        if (recordedLedgers.Count == 0)
        {
            // 借りたが使わずに返した（記録ゼロ）。確認すべき記録が無いのに画面を変えない（#186 の例外を広げない）
            return;
        }

        if (IsHistoryVisible && !IsReturnHistoryReviewReplaceable)
        {
            // 職員が履歴を使っている（手動で開いた／返却確認を操作した）。#186 のとおり画面を奪わず、確認だけ促す。
            // 同じカードの履歴でも奪わない — 統合のために行をチェックしている最中に別の職員がそのカードを
            // 返却し得る（#1923）
            // タイトルは #596 の警告トースト「履歴の確認」（同じ返却で同時に出得る）と区別する。
            // 案内は再読込より前に出す（再読込は DB I/O で失敗し得るため、失敗するサブシステムに
            // 通知を依存させない。#1727）
            _toastNotificationService.ShowInfo(
                "返却した履歴の確認",
                "返却した交通系ICカードの利用履歴を確認してください。");

            // 画面は奪わないが、今回の行の目印は付ける（コードレビューで検出）。
            // 「利用履歴を確認してください」と案内しながら、どの行が今回の記録かを示す手掛かりが
            // 画面に 1 つも無い状態を残さない — カード・表示期間・ページは変えないので #186 の範囲に収まる。
            // 同じカードを表示しているときだけ行う（別のカードなら今回の行はそもそも一覧に無い）。
            if (HistoryCard != null && HistoryCard.CardIdm == card.CardIdm)
            {
                _recentlyRecordedLedgerIds.UnionWith(recordedLedgers.Select(l => l.Id));
                // Issue #1923: 返却は履歴画面で行を選んでいる職員の操作ではないため、チェックは引き継ぐ
                await LoadHistoryLedgersAsync(preserveCheckedRows: true);
            }

            return;
        }

        // 当月内なら既定（当月 1 日から）。前月以前の利用があるときだけ、その利用があった月の 1 日まで遡る。
        //
        // **日付単位ではなく月の 1 日へ丸める**（コードレビューで検出）。この履歴パネルの表示期間は
        // 月単位が前提で、`GetPrecedingBalanceAsync` は「開始日の属する月の 1 日より前」の残高を返す。
        // 開始日に 3/31 のような月中の日付を入れると、その値が
        //   ・合成する「○月から繰越」行（#1155）
        //   ・残高チェーンの並べ替えシード（#1740）
        // の両方で「2 月末の残高」になり、3/1〜3/30 の行は期間外で隠れるため、
        // 画面上の「受入 − 払出 = 残額」が合わなくなる（記録の確認が目的の画面で最も困る形）。
        // シードの誤りはさらに悪く、循環する日の中間残高に偶然一致するとチェーンが回転した状態で
        // 確定する（`business-logic.md` #1999）。月の 1 日へ丸めれば両方の消費側が正しくなり、
        // 「記録した行がすべて画面に収まる」という本来の意図も満たせる。
        var today = DateTime.Today;
        var firstOfMonth = new DateTime(today.Year, today.Month, 1);
        var earliestRecordedDate = recordedLedgers.Min(l => l.Date).Date;
        var fromDate = earliestRecordedDate < firstOfMonth
            ? new DateTime(earliestRecordedDate.Year, earliestRecordedDate.Month, 1)
            : (DateTime?)null;

        _balanceInconsistencies.Clear();
        await ShowHistoryAsync(card, fromDate, recordedLedgers.Select(l => l.Id));

        // 一覧は日付昇順（GetPagedAsync の ORDER BY）なので、今回の行＝期間内で最新の行は末尾に来る。
        // 期間内の行がページサイズを超えるカード（共用カードでは日常的）では 1 ページ目に今回の行が
        // 1 つも無く、「✔ の行を確認してください」という案内が空振りする。ページ送りの「最終ページ」と
        // 同じ経路で最終ページへ移動する（コードレビューで検出。手段を 2 つにしない #1763）。
        // View 側は IsReturnHistoryReview の立ち上がりで最初の ✔ 行へスクロールする
        if (HistoryCanGoToLastPage)
        {
            await HistoryGoToLastPage();
        }

        IsReturnHistoryReview = true;
        _returnHistoryReviewTouched = false;
    }

    /// <summary>
    /// Issue #1907: 返却確認の履歴が「職員に使われていない」状態か（次の職員証タッチで閉じてよい／
    /// 別カードの返却確認で置き換えてよい）
    /// </summary>
    private bool IsReturnHistoryReviewReplaceable => IsReturnHistoryReview && !_returnHistoryReviewTouched;

    /// <summary>
    /// Issue #1907: 返却確認の履歴パネルを職員が操作したことを記録する（View のキー・クリック・ホイール操作から呼ぶ）。
    /// 以後は次の職員証タッチでも閉じず、別カードの返却確認にも置き換えない（手動で開いた履歴と同じ扱い）。
    /// 返却確認以外で開いた履歴では何もしない。
    /// </summary>
    public void MarkReturnHistoryReviewTouched()
    {
        if (IsReturnHistoryReview)
        {
            _returnHistoryReviewTouched = true;
        }
    }

    /// <summary>
    /// Issue #1907: 職員が操作していない返却確認の履歴を閉じる（次の職員証タッチで呼ぶ）。
    /// 1 台のカードリーダーを順番に使う運用では「次の職員がタッチした＝前の職員は確認を終えた」とみなせる。
    /// </summary>
    private void CloseReturnHistoryReviewIfUntouched()
    {
        if (IsReturnHistoryReviewReplaceable)
        {
            CloseHistory();
        }
    }

    /// <summary>
    /// Issue #1907: 返却確認の状態を解除する（履歴を閉じる／別の履歴を開くときに呼ぶ）
    /// </summary>
    private void EndReturnHistoryReview()
    {
        IsReturnHistoryReview = false;
        _returnHistoryReviewTouched = false;
        _recentlyRecordedLedgerIds.Clear();
    }
}
