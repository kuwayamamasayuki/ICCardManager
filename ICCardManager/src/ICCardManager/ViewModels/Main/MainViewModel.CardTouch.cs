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
    // === カードタッチ状態機械（貸出・返却・30秒ルール・失敗通知） ===

    /// <summary>
    /// カード読み取りイベント
    /// </summary>
    private void OnCardRead(object? sender, CardReadEventArgs e)
    {
        // UIスレッドで処理を実行（即時応答のため）
        // Func<Task>オーバーロードを使用し、async void化を防止
        _dispatcherService.InvokeAsync(() => HandleCardReadAsync(e.Idm));
    }

    /// <summary>
    /// カード読み取り処理
    /// </summary>
    private async Task HandleCardReadAsync(string idm)
    {
        // 処理中は無視
        if (CurrentState == AppState.Processing)
        {
            return;
        }

        // カード読み取り抑制中は処理をスキップ（Issue #852）
        // ダイアログ側（CardManageViewModel / StaffManageViewModel / StaffAuthDialog）が処理する
        // ※登録済みカード/職員証も含め、すべてのカード読み取りを無視する
        if (_suppressionSources.Count > 0)
        {
            return;
        }

        switch (CurrentState)
        {
            case AppState.WaitingForStaffCard:
                await HandleCardInStaffWaitingStateAsync(idm);
                break;

            case AppState.WaitingForIcCard:
                await HandleCardInIcCardWaitingStateAsync(idm);
                break;
        }
    }

    /// <summary>
    /// 職員証待ち状態でのカード処理
    /// </summary>
    private async Task HandleCardInStaffWaitingStateAsync(string idm)
    {
        // Issue #1452: 同一の SQLiteConnection 上で SQLiteCommand が並列実行されると
        // SQLITE_MISUSE 不定動作の原因となるため、リポジトリ呼び出しは直列化する。
        // Issue #1842: card は職員証でなかったときにしか使わないため、
        // HandleCardInIcCardWaitingStateAsync と同じく職員証判定が null のときだけ問い合わせる。
        // 待機（await）を 1 つ減らすことは、この直後の再判定が守る「前提が変わり得る窓」を
        // 狭めることでもある（共有モードでは 1 クエリが数十〜数百 ms かかる）。
        var staff = await _staffRepository.GetByIdmAsync(idm);
        var card = staff == null ? await _cardRepository.GetByIdmAsync(idm) : null;

        // Issue #1842: 上の await 中に届いた別のタッチは HandleCardReadAsync の入口ゲートを
        // 通過済みであり、その 2 件目が先に処理を終えると本メソッドの継続は「古い前提」で走る。
        // ここで前提（抑制なし・職員証待ち）を取り直さないと次の 2 つが起きる。
        //   ・2 件目が未登録カードだった場合: 抑制と種別選択ダイアログを取得済みなので、
        //     本メソッドがその背後で職員証認識や履歴表示を進めてしまう（#1807 の趣旨に反する）
        //   ・2 件目が職員証だった場合: 状態は WaitingForIcCard・操作者は確定済みなのに、
        //     本メソッドが未登録カード処理へ進み、末尾の ResetState() が確定済みの操作者を消す
        // タイムアウト発火（ResetState）で状態が戻った場合も同じ判定で中止できる。
        // 中止したタッチは再タッチで通常どおり処理される（副作用を残さない側へ倒す）。
        if (IsCardReadingSuppressed || CurrentState != AppState.WaitingForStaffCard)
        {
            return;
        }

        // 職員証かどうか確認
        if (staff != null)
        {
            // 職員証認識
            _currentStaffIdm = idm;
            _currentStaffName = staff.Name;

            // 認識音を再生（Issue #411, #832: 音声モードでも常にビープ音）
            _soundPlayer.Play(SoundType.Notify);

            // メイン画面は変更せず、ポップアップ通知のみ表示（Issue #186）
            // 「職員証をタッチしてください」のメッセージはクリアする
            SetInternalState(AppState.WaitingForIcCard, clearStatusMessage: true);

            // Issue #1907: 前の職員の返却確認（自動表示した履歴）は、次の職員証タッチ＝次の操作の開始で閉じる。
            // 職員が操作していた履歴は閉じない。#186（メイン画面を変更しない）の例外だが、
            // 閉じるのは本システム自身が自動で開いたパネルに限る。
            History.CloseReturnHistoryReviewIfUntouched();

            // Issue #2141: 前の職員宛てのエラー・記録済みの案内（自動では消えない通知）も、次の操作の開始で閉じる
            _toastNotificationService.DismissPersistentNotifications();
            _toastNotificationService.ShowStaffRecognizedNotification(staff.Name);
            StartTimeout();
            return;
        }

        // 交通系ICカードかどうか確認
        if (card != null)
        {
            // 30秒ルールチェック：職員証スキップモードでない場合も適用
            if (_lendingService.IsRetouchWithinTimeout(idm))
            {
                // 30秒以内の再タッチ → 逆の処理を行う
                await Process30SecondRuleAsync(card);
                return;
            }

            // Issue #1908: 履歴を開く前に、カードの実残額とピッすいの記録の食い違いを判定する。
            // 実残額はカードがリーダーに載っている「いま」しか読めないため、履歴の読み込み（DB I/O）より前に行う。
            // Issue #1947: 母集団の判定は CheckCardBalanceMismatchAsync の内側で行う（呼び出し元へ配らない）。
            await CheckCardBalanceMismatchAsync(card);

            // 履歴表示画面を開く
            await History.ShowCardHistoryAsync(card);
            return;
        }

        // 未登録カード
        await HandleUnregisteredCardAsync(idm);
    }

    /// <summary>
    /// ICカード待ち状態でのカード処理
    /// </summary>
    private async Task HandleCardInIcCardWaitingStateAsync(string idm)
    {
        // Issue #1211: ICカード待ち状態で職員証がタッチされた場合の処理。
        // 運用上、ICカードリーダー上に職員証を置きっぱなしにしている職員がおり、
        // 他の職員が操作しようとすると置きっぱなしの職員証が先に反応してしまう。
        // そのため、ICカード待ち中の職員証タッチは初回タッチと完全に同じ挙動で
        // 扱い、操作者を上書きする（Notify 音 + 認識トースト）。同一/別職員の
        // 区別はせず、毎回通常の職員証認識フローを通す。
        var staff = await _staffRepository.GetByIdmAsync(idm);
        var card = staff == null ? await _cardRepository.GetByIdmAsync(idm) : null;

        // Issue #1842: 上の await 中に届いた別のタッチが先に処理を終えている場合は中止する
        // （判断の根拠は HandleCardInStaffWaitingStateAsync のコメントを参照）。
        // StopTimeout() はこの判定の後に置く。前に置くと、中止する経路でタイマーだけが止まり
        // WaitingForIcCard から抜ける手段（タイムアウト）を失った状態で放置される。
        if (IsCardReadingSuppressed || CurrentState != AppState.WaitingForIcCard)
        {
            return;
        }

        StopTimeout();

        if (staff != null)
        {
            _currentStaffIdm = idm;
            _currentStaffName = staff.Name;

            // Issue #1684: 持ち替えでは CurrentState が変化しない（WaitingForIcCard のまま）ため、
            // 操作者名を含む次アクションガイドの文言を明示的に更新する
            OnPropertyChanged(nameof(NextActionMessage));

            _soundPlayer.Play(SoundType.Notify);
            // Issue #2141: 持ち替え（別の職員証）も次の操作の開始なので、自動では消えない通知を閉じる
            _toastNotificationService.DismissPersistentNotifications();
            _toastNotificationService.ShowStaffRecognizedNotification(staff.Name);
            StartTimeout();
            return;
        }

        // 交通系ICカードかどうか確認（card は上のガード前に取得済み）
        if (card == null)
        {
            // 未登録カード
            await HandleUnregisteredCardAsync(idm);
            ResetState();
            return;
        }

        // Issue #530: 払戻済カードは貸出対象外
        if (card.IsRefunded)
        {
            _soundPlayer.Play(SoundType.Error);
            _toastNotificationService.ShowError(
                "払戻済カード",
                $"{card.CardType} {card.CardNumber} は払い戻し済みのため貸出できません");
            ResetState();
            return;
        }

        // 30秒ルールチェック
        if (_lendingService.IsRetouchWithinTimeout(idm))
        {
            // 逆の処理を行う
            await Process30SecondRuleAsync(card);
        }
        else
        {
            // 通常の貸出・返却判定
            if (card.IsLent)
            {
                await ProcessReturnAsync(card);
            }
            else
            {
                await ProcessLendAsync(card);
            }
        }
    }

    /// <summary>
    /// 30秒ルールによる逆操作を実行します。
    /// </summary>
    /// <param name="card">対象のICカード</param>
    /// <remarks>
    /// <para>
    /// 同一カードが30秒以内に再タッチされた場合に呼び出されます。
    /// 直前の処理と逆の処理（貸出→返却、返却→貸出）を実行します。
    /// </para>
    /// <para>
    /// 職員証タッチ待ち状態（<see cref="AppState.WaitingForStaffCard"/>）からも動作するよう、
    /// <b>操作者が未確定のときに限り</b>最後に操作を行った職員の情報で補完します。
    /// </para>
    /// <para>
    /// <b>Issue #1729: 操作者が確定している場合は上書きしない。</b>
    /// ICカード待ち状態（<see cref="AppState.WaitingForIcCard"/>）から呼ばれる場合、
    /// 直前の職員証タッチで <c>_currentStaffIdm</c> が確定している。ここで前回操作者に
    /// 差し替えると、実際に操作した職員とは別の職員が
    /// <c>ledger.StaffName</c> / <c>ic_card.lender_idm</c> / <c>operation_log</c> に記録され、
    /// 長期未返却の督促も誤った職員へ向かう。
    /// なお <see cref="AppState.WaitingForStaffCard"/> へ遷移する経路は
    /// <c>ResetState()</c> ただ 1 つで、そこで <c>_currentStaffIdm</c> は必ず null になるため、
    /// 「未確定＝職員証タッチ待ち経路」と判定できる。
    /// </para>
    /// </remarks>
    private async Task Process30SecondRuleAsync(IcCard card)
    {
        // Issue #1729: 操作者が未確定のときだけ、30秒ルール用に保存した職員情報で補完する。
        // 職員証タッチ済み（ICカード待ち経路）では、いま操作している職員をそのまま使う。
        if (string.IsNullOrEmpty(_currentStaffIdm))
        {
            if (string.IsNullOrEmpty(_lastProcessedStaffIdm))
            {
                _soundPlayer.Play(SoundType.Error);
                _toastNotificationService.ShowError("エラー", "操作者情報がありません。職員証をタッチしてください。");
                return;
            }

            _currentStaffIdm = _lastProcessedStaffIdm;
            _currentStaffName = _lastProcessedStaffName;
        }

        // 逆の処理を行う
        if (_lendingService.LastOperationType == LendingOperationType.Lend)
        {
            // 貸出直後の再タッチ → 返却へ
            await ProcessReturnAsync(card);
        }
        else
        {
            // 返却直後の再タッチ → 貸出へ
            await ProcessLendAsync(card);
        }
    }

    /// <summary>
    /// ICカードの貸出処理を実行します。
    /// </summary>
    /// <param name="card">貸出対象のICカード</param>
    /// <remarks>
    /// <para>処理フロー：</para>
    /// <list type="number">
    /// <item><description>状態を <see cref="AppState.Processing"/> に変更</description></item>
    /// <item><description><see cref="LendingService.LendAsync"/> を呼び出して貸出処理</description></item>
    /// <item><description>成功時: 貸出音を再生、トースト通知を表示、画面を薄いオレンジ色に</description></item>
    /// <item><description>失敗時: エラー音を再生、エラーメッセージを表示</description></item>
    /// <item><description>2-3秒後に状態をリセット</description></item>
    /// </list>
    /// </remarks>
    private async Task ProcessLendAsync(IcCard card)
    {
        // メイン画面は変更せず、内部状態のみ更新（Issue #186）
        SetInternalState(AppState.Processing);

        // Issue #1725: 台帳への記録が確定したかを追跡する。
        // 記録後に後処理（画面更新）が失敗した場合、「もう一度タッチ」と案内すると
        // 30秒ルールの逆処理が走り、記録済みの貸出が取り消されてしまうため。
        var recorded = false;
        try
        {
            // カードから残高を読み取る（Issue #526: 貸出時も残高を記録）
            // Issue #656: エラーイベントを一時的に抑制（カード離脱時の警告メッセージを防止）
            int? balance = null;
            _cardReader.Error -= OnCardReaderError;
            try
            {
                balance = await _cardReader.ReadBalanceAsync(card.CardIdm);
            }
            catch
            {
                // 残高読み取りエラーは無視（貸出処理は続行）
            }
            finally
            {
                _cardReader.Error += OnCardReaderError;
            }

            var result = await _lendingService.LendAsync(_currentStaffIdm!, card.CardIdm, balance);

            if (result.Success)
            {
                recorded = true;

                _soundPlayer.Play(SoundType.Lend);

                // トースト通知を表示（表示位置は設定に従う、フォーカスを奪わない）
                _toastNotificationService.ShowLendNotification(card.CardType, card.CardNumber);

                // メイン画面は変更しない（Issue #186: 職員の操作を妨げない）

                // 30秒ルール用に職員情報を保存（Issue #1725: 後処理より前に確定させる。
                // リフレッシュの後に置くと、後処理が例外で終わったとき保存されず、
                // 直後の再タッチが「操作者情報がありません」で止まる）
                _lastProcessedStaffIdm = _currentStaffIdm;
                _lastProcessedStaffName = _currentStaffName;

                // Issue #1908: 実残額を読み取れたときだけ食い違い警告を取り除く。
                // 読み取れていない場合は台帳の残額が現物と一致する保証が無い。
                if (balance.HasValue)
                {
                    ClearCardBalanceMismatchWarning(card.CardIdm);
                }

                await RefreshLentCardsAsync();
                await RefreshDashboardAsync();

                // 履歴が開いていれば再読み込み（Issue #526）
                if (History.IsHistoryVisible)
                {
                    // Issue #1923: 貸出は「カードをタッチした職員」の操作であり、履歴画面で行を選んでいる職員の操作ではない。
                    // 本システムは 1 台のカードリーダーを複数職員で共有するため、
                    // 定期リフレッシュ（RefreshSharedDataAsync）と同じ理由で統合対象のチェックを引き継ぐ。
                    await History.LoadHistoryLedgersAsync(preserveCheckedRows: true);
                }
            }
            else
            {
                _soundPlayer.Play(SoundType.Error);

                // エラー時はトースト通知で表示（メイン画面は変更しない）
                // フォールバック文言にも行動指示を付与（Issue #1614）。トーストは文字数制約があるため簡潔に。
                // Issue #2141: finally の ResetState() で職員証タッチ待ちへ戻るので「職員証のタッチから」と案内する
                _toastNotificationService.ShowError("エラー", result.ErrorMessage ?? OperationRetryGuidance.BuildFailureMessage("貸出"));
            }
        }
        catch (Exception ex)
        {
            NotifyProcessingFailure(ex, "貸出", card, recorded);
        }
        finally
        {
            // Issue #1725: 例外経路でも必ず Processing を解除する。
            // 解除しないと以後の全カードタッチが HandleCardReadAsync 冒頭の
            // 「処理中は無視」で破棄され、タイムアウトタイマーも停止済みのため
            // アプリ再起動以外に復帰手段が無くなる。
            ResetState();
        }
    }

    /// <summary>
    /// ICカードの返却処理を実行します。
    /// </summary>
    /// <param name="card">返却対象のICカード</param>
    /// <remarks>
    /// <para>処理フロー：</para>
    /// <list type="number">
    /// <item><description>状態を <see cref="AppState.Processing"/> に変更</description></item>
    /// <item><description>カードリーダーで利用履歴を読み取り</description></item>
    /// <item><description><see cref="LendingService.ReturnAsync"/> を呼び出して返却処理</description></item>
    /// <item><description>成功時: 返却音を再生、残額付きのトースト通知を表示（メイン画面は変更しない。Issue #186）</description></item>
    /// <item><description>成功したがコミット後の付帯情報（残額・残額警告）を取得できなかった場合（<see cref="LendingResult.HasPostCommitFailure"/>、Issue #1805）: 警告音＋「返却は記録済み・再タッチしないでください」の警告トーストを表示し、残額付きの通知は出さない</description></item>
    /// <item><description>バス利用がある場合: バス停入力ダイアログを表示</description></item>
    /// <item><description>残額が警告閾値以下の場合（境界を含む。Issue #1998）: 警告メッセージを表示</description></item>
    /// <item><description>失敗時: エラー音を再生、エラーメッセージを表示</description></item>
    /// </list>
    /// </remarks>
    private async Task ProcessReturnAsync(IcCard card)
    {
        // メイン画面は変更せず、内部状態のみ更新（Issue #186）
        SetInternalState(AppState.Processing);

        // Issue #1725: 台帳への記録が確定したかを追跡する（ProcessLendAsync と同じ理由）
        var recorded = false;
        // Issue #1805: LendingService 側で「記録済み・再タッチしない」を案内済みかを追跡する
        var recordedNotified = false;
        try
        {
            // Issue #1169: カードから履歴を読み取る（リーダーエラーと履歴ゼロ件を区別）
            var historyResult = await _cardReader.TryReadHistoryAsync(card.CardIdm);
            if (!historyResult.Success)
            {
                // リーダーエラー: 不正確なデータをDBに記録しないため返却処理を中断
                _soundPlayer.Play(SoundType.Error);
                // Issue #2141: 状態リセット（finally）で職員証が消えるため、交通系ICカードだけを
                // タッチし直すと履歴表示になる。やり直しは職員証のタッチからと案内する
                _toastNotificationService.ShowError(
                    "カードリーダーエラー",
                    "履歴の読み取りに失敗しました。" + OperationRetryGuidance.RestartFromStaffCard);
                return; // 状態リセットは finally が行う
            }
            var usageDetailsList = historyResult.Value.ToList();

            var result = await _lendingService.ReturnAsync(_currentStaffIdm!, card.CardIdm, usageDetailsList);

            if (result.Success)
            {
                recorded = true;
                // HandleReturnSuccessAsync の冒頭で HasPostCommitFailure の案内を出すため、
                // その後の画面更新が同じ原因で失敗しても NotifyProcessingFailure が同題の案内を重ねない
                recordedNotified = result.HasPostCommitFailure;

                // 30秒ルール用に職員情報を保存（Issue #1725: 後処理より前に確定させる）
                _lastProcessedStaffIdm = _currentStaffIdm;
                _lastProcessedStaffName = _currentStaffName;

                // 返却成功時の共通後処理（仮想タッチからも同じ処理を呼び出す。Issue #1577）
                await HandleReturnSuccessAsync(card, result);
            }
            else
            {
                _soundPlayer.Play(SoundType.Error);

                // エラー時はトースト通知で表示（メイン画面は変更しない）
                // フォールバック文言にも行動指示を付与（Issue #1614）。トーストは文字数制約があるため簡潔に。
                // Issue #2141: finally の ResetState() で職員証タッチ待ちへ戻るので「職員証のタッチから」と案内する
                _toastNotificationService.ShowError("エラー", result.ErrorMessage ?? OperationRetryGuidance.BuildFailureMessage("返却"));
            }
        }
        catch (Exception ex)
        {
            NotifyProcessingFailure(ex, "返却", card, recorded, recordedNotified);
        }
        finally
        {
            // Issue #1725: 例外経路でも必ず Processing を解除する（ProcessLendAsync と同じ理由）
            ResetState();
        }
    }

    /// <summary>
    /// 貸出／返却処理で捕捉した例外をログへ残し、ユーザーへ通知します（Issue #1725）。
    /// </summary>
    /// <param name="ex">捕捉した例外</param>
    /// <param name="operationName">ユーザー視点の操作名（「貸出」「返却」）</param>
    /// <param name="card">対象の交通系ICカード</param>
    /// <param name="recorded">
    /// 台帳への記録が確定済みかどうか。<c>true</c> の場合は「記録済み」として案内し、
    /// 再タッチを促さない。
    /// </param>
    /// <param name="alreadyNotified">
    /// 「記録済み・再タッチしない」の案内を既に出しているか（Issue #1805。
    /// <see cref="LendingResult.HasPostCommitFailure"/> の案内後に同じ原因で画面更新も失敗した場合）。
    /// <c>true</c> かつ <paramref name="recorded"/> のときはログのみ残し、同題のトーストと警告音を重ねない。
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>記録済みのときに「もう一度タッチしてください」と案内してはならない。</b>
    /// 30秒以内の再タッチは逆処理（貸出→返却）として扱われるため、
    /// 案内どおりに操作すると記録済みの貸出／返却が取り消される。
    /// </para>
    /// <para>
    /// 音も記録済みかどうかで分ける。記録は成功しているのにエラー音（ピー）を鳴らすと
    /// 事実と矛盾するため、中立的な <see cref="SoundType.Warning"/> を使う
    /// （時間切れを警告音で扱う Issue #1683 と同じ考え方）。
    /// </para>
    /// <para>
    /// ログは <c>LogError</c>。<c>LogDebug</c> では本番の
    /// <c>Logging:LogLevel:Default = Information</c> によりファイルへ出力されず、
    /// 障害調査で経路を追えない（Issue #1716 の教訓）。
    /// </para>
    /// </remarks>
    private void NotifyProcessingFailure(Exception ex, string operationName, IcCard card, bool recorded, bool alreadyNotified = false)
    {
        _logger?.LogError(
            ex,
            "{Operation}処理で予期しない例外が発生しました（CardIdm={CardIdm}, 記録済み={Recorded}）",
            operationName,
            IdmMasker.Mask(card?.CardIdm),
            recorded);

        if (recorded)
        {
            // Issue #1805: LendingService 側の付帯情報の欠落（HasPostCommitFailure）で既に
            // 「記録済み・再タッチしない」を案内済みなら、同じ原因（DB ロック・共有フォルダー断）で
            // 続く画面更新も失敗したときに同題の警告トーストと警告音を重ねて出さない（ログには残す）。
            if (!alreadyNotified)
            {
                NotifyRecordedButIncomplete(operationName, "画面の更新に失敗しました。");
            }
        }
        else
        {
            _soundPlayer.Play(SoundType.Error);
            // Issue #2141: 呼び出し元の finally が職員証タッチ待ちへ戻すので「職員証のタッチから」と案内する
            _toastNotificationService.ShowError(
                "エラー",
                OperationRetryGuidance.BuildFailureMessage(operationName));
        }
    }

    /// <summary>
    /// 「台帳への記録は確定したが後処理が完了しなかった」ことを案内します（Issue #1725 / #1805）。
    /// </summary>
    /// <param name="operationName">ユーザー視点の操作名（「貸出」「返却」）</param>
    /// <param name="reason">何が得られなかったか（「画面の更新に失敗しました。」「残額を確認できませんでした。」等。句点で終える）</param>
    /// <remarks>
    /// 中立的な <see cref="SoundType.Warning"/> と「{操作}は記録済み」＋「再タッチしないでください」の組を
    /// 1 か所に集約する。「もう一度タッチ」と案内すると30秒ルールの逆処理で記録済みの操作が取り消されるため、
    /// 記録済みの案内はすべてここを通す（文言・音の変更が片方だけに入る事故を防ぐ）。
    /// </remarks>
    private void NotifyRecordedButIncomplete(string operationName, string reason)
    {
        _soundPlayer.Play(SoundType.Warning);
        // Issue #2141: 再タッチを止める最重要の指示なので、自動では消さない（3 秒で消えると、見逃した職員の
        // 再タッチが逆の操作として新たに記録される）。次の職員証タッチ・クリック・Esc で閉じる
        _toastNotificationService.ShowRecordedNotice(
            $"{operationName}は記録済み",
            $"{reason}再タッチしないでください。");
    }
}
