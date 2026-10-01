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
    // === 起動・共有モード・定期更新・警告生成 ===

    /// <summary>
    /// アプリケーションの初期化処理を実行します。
    /// </summary>
    /// <remarks>
    /// <para>以下の処理を順次実行します：</para>
    /// <list type="number">
    /// <item><description>警告チェック（残額低下、バス停名未入力）</description></item>
    /// <item><description>貸出中カードの一覧取得</description></item>
    /// <item><description>カード残高ダッシュボードの更新</description></item>
    /// <item><description>職員証スキップ設定の読み込み</description></item>
    /// <item><description>カードリーダー監視の開始</description></item>
    /// </list>
    /// </remarks>
    /// <returns>初期化処理のTask</returns>
    [RelayCommand]
    public async Task InitializeAsync()
    {
        using (BeginBusy("初期化中..."))
        {
            // Issue #1172: ジャーナルモードがDELETE以外（degraded）の場合、UI警告を追加。
            // Issue #1739: WarningService を直接呼ばず本メソッド経由にする。インラインで Add すると
            // 重複ガードを通らず、再入時に同じ警告が2行並ぶ（04_機能設計書 §7.4 の表とも食い違う）。
            CheckJournalModeWarning();

            // Issue #790: 起動時に貸出状態の整合性をチェック・修復
            await _lendingService.RepairLentStatusConsistencyAsync();

            // ダッシュボード更新（カード情報・残高を取得）
            await RefreshDashboardAsync();

            // 設定を取得してサウンドモードを適用
            var settings = await _settingsRepository.GetAppSettingsAsync();
            _soundPlayer.SoundMode = settings.SoundMode;

            // 貸出中カードを取得
            await RefreshLentCardsAsync();

            // 警告チェック（ダッシュボードデータを使用して高速化）
            ApplyDataWarnings(settings.WarningBalance);

            // カード読み取り開始
            await _cardReader.StartReadingAsync();

            // Issue #504 / #1689 / #1758: DB を読む起動時チェックはバックグラウンドで、かつ**直列に**実行する
            // （起動を遅延させず、同一接続上のコマンド並走も避ける）。詳細は RunStartupDataChecksAsync を参照。
            _ = RunStartupDataChecksAsync();

            // Issue #1687: 更新通知チェック（latest_version.txt）もバックグラウンドで実行
            // （共有フォルダのSMB遅延で起動をブロックしないため）。
            // DB を触らずファイル読み取りのみのため、上のチェック群とは独立に走らせてよい。
            _ = CheckUpdateNotificationAsync();

            // 共有モード時はDB接続の定期ヘルスチェックを開始
            if (IsSharedMode)
            {
                _sharedModeMonitor.Start();
            }
        }
    }

    /// <summary>
    /// Issue #1172: ジャーナルモード状態をチェックし、degradedの場合は警告を追加する。
    /// internal: テストから直接呼び出して挙動を検証するため。
    /// </summary>
    /// <remarks>
    /// DbContext.IsJournalModeDegraded がtrueの場合、警告メッセージエリアに
    /// クラッシュ耐性低下の警告を表示する。重複追加は防止する。
    /// </remarks>
    /// <summary>
    /// Issue #1172: ジャーナルモード警告チェック（WarningServiceに委譲）
    /// </summary>
    internal void CheckJournalModeWarning()
    {
        if (WarningMessages.Any(w => w.Type == WarningType.DatabaseJournalModeDegraded))
            return;

        var warning = _warningService.CheckJournalModeWarning();
        if (warning != null)
            WarningMessages.Add(warning);
    }

    /// <summary>
    /// Issue #1687: 更新通知チェック（WarningServiceに委譲）。
    /// internal: テストから直接呼び出して挙動を検証するため。
    /// </summary>
    /// <remarks>
    /// latest_version.txt の読み取りは共有フォルダ（SMB）アクセスを伴うため
    /// Task.Run でバックグラウンド実行し、結果の WarningMessages 追加は
    /// await 後の UI コンテキストで行う。重複追加は防止する。
    /// </remarks>
    internal async Task CheckUpdateNotificationAsync()
    {
        var warning = await Task.Run(() => _warningService.CheckUpdateNotificationWarning());
        if (warning != null && !WarningMessages.Any(w => w.Type == WarningType.NewVersionAvailable))
            WarningMessages.Add(warning);
    }

    /// <summary>
    /// Issue #1689: バックアップ健全性チェック（WarningServiceに委譲）。
    /// internal: テストから直接呼び出して挙動を検証するため。
    /// </summary>
    /// <remarks>
    /// settings 読み取りとバックアップフォルダの走査（共有モードでは SMB アクセス）を伴うため
    /// Task.Run でバックグラウンド実行し、WarningMessages の更新は await 後の UI コンテキストで行う。
    /// 手動バックアップ後の再判定でも呼ばれるため、解消済みなら既存の警告を取り除く
    /// （追加のみだと一度出た警告が復旧後も残り続ける）。
    /// </remarks>
    internal async Task CheckBackupHealthAsync()
    {
        var sequence = ++_backupHealthCheckSequence;
        var warning = await Task.Run(() => _warningService.CheckBackupHealthWarningAsync(DateTime.Now));

        // Issue #1739: より新しいチェックが始まっていれば、この結果は陳腐化している
        // （起動時の fire-and-forget が保留している間に、手動バックアップ後の再判定が走る経路がある）
        if (sequence != _backupHealthCheckSequence) return;

        ReplaceWarnings(
            w => w.Type == WarningType.BackupStale,
            warning == null ? null : new[] { warning });
    }

    /// <summary>
    /// Issue #1758: DB を読む起動時チェックを1本のバックグラウンドタスクへ直列に並べる。
    /// internal: テストから直接呼び出して挙動を検証するため。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>なぜ直列か</b>: <see cref="Data.DbContext"/> は <c>SQLiteConnection</c> を1本しか持たず、
    /// <c>LeaseConnectionAsync</c> はセマフォを取らない（Issue #1452 の「並列起動禁止」）。
    /// 各チェックを個別に <c>_ =</c> で捨てると、戻り値を捨てた async メソッドの継続どうしが
    /// 同一接続上で並走し、<c>SQLITE_MISUSE</c> または不定動作の原因になる（Issue #1737 と同じ形）。
    /// fire-and-forget の入口を1本に絞れば、起動をブロックせずに直列性を保てる。
    /// </para>
    /// <para>
    /// <b>なぜ個別に catch するか</b>: fire-and-forget には「前段が落ちても後段は動く」という
    /// 副次的な性質がある。単純な <c>await</c> の連結はこれを失わせるため、各呼び出しを個別に
    /// 包んで明示的に保存する（Issue #1737）。
    /// </para>
    /// <para>
    /// バックアップ健全性チェックは、Issue #1737 で起動時タスクが直列 await になったことにより
    /// 本メソッドが走る時点で今回の自動バックアップが完了している（<c>StartupTaskRunner</c> の
    /// 実行後に MainWindow を表示するため）。したがって判定材料には今回の成功記録が含まれる。
    /// </para>
    /// </remarks>
    internal async Task RunStartupDataChecksAsync()
    {
        await RunGuardedStartupCheckAsync(CheckIncompleteBusStopsAsync, "バス停名未入力チェック");
        await RunGuardedStartupCheckAsync(CheckBackupHealthAsync, "バックアップ健全性チェック");
        await RunGuardedStartupCheckAsync(CheckCarryoverDataLossAsync, "繰越情報消失チェック");
    }

    /// <summary>
    /// 起動時チェック1件を実行し、失敗しても後続へ影響させない。
    /// </summary>
    private async Task RunGuardedStartupCheckAsync(Func<Task> check, string checkName)
    {
        try
        {
            await check();
        }
        catch (Exception ex)
        {
            // 障害調査で必要になるため Information ではなく Error で残す（.claude/rules のロギング規約）。
            // ユーザーへの通知は行わない（起動を妨げない補助的なチェックのため）。
            _logger?.LogError(ex, "起動時の{CheckName}に失敗しました", checkName);
        }
    }

    /// <summary>
    /// Issue #1758: 繰越情報消失チェック（WarningServiceに委譲）。
    /// internal: テストから直接呼び出して挙動を検証するため。
    /// </summary>
    /// <remarks>
    /// operation_log の走査（共有モードでは SMB アクセス）を伴うため Task.Run でバックグラウンド実行し、
    /// WarningMessages の更新は await 後の UI コンテキストで行う。<c>CheckBackupHealthAsync</c> と同じ形。
    /// DB を直接修正して復旧された場合に警告が消えるよう、解消済みなら既存の警告を取り除く。
    /// </remarks>
    internal async Task CheckCarryoverDataLossAsync()
    {
        var sequence = ++_carryoverDataLossCheckSequence;
        var warning = await Task.Run(() => _warningService.CheckCarryoverDataLossWarningAsync());

        // Issue #1739: より新しいチェックが始まっていれば、この結果は陳腐化している
        if (sequence != _carryoverDataLossCheckSequence) return;

        ReplaceWarnings(
            w => w.Type == WarningType.CarryoverDataLoss,
            warning == null ? null : new[] { warning });
    }

    /// <summary>
    /// SharedModeMonitorからのヘルスチェック結果を受けてUI警告を更新
    /// </summary>
    /// <remarks>
    /// Issue #1359: SharedModeMonitor.ExecuteHealthCheckAsync が ConfigureAwait(false) を使用するため
    /// 本イベントは thread pool スレッドから発火される。UI バインドされた ObservableCollection
    /// (WarningMessages / LentCards / CardBalanceDashboard) を安全に更新するため、
    /// IDispatcherService で UI スレッドへ明示的にマーシャリングする（OnCardRead と同一パターン）。
    /// </remarks>
    private void OnSharedModeHealthCheckCompleted(object sender, DatabaseHealthEventArgs e)
    {
        _dispatcherService.InvokeAsync(async () =>
        {
            UpdateConnectionWarning(e.IsConnected);

            // 接続断の場合はリフレッシュをスキップ
            if (!e.IsConnected)
                return;

            // 共有モード: 他PCの変更を反映するためダッシュボードと貸出中カードを定期リフレッシュ
            await RefreshSharedDataAsync();
        });
    }

    /// <summary>
    /// SharedModeMonitorからの同期表示更新を受けてUIプロパティを更新
    /// </summary>
    private void OnSyncDisplayUpdated(object sender, SyncDisplayEventArgs e)
    {
        LastRefreshText = e.Text;
        IsRefreshStale = e.IsStale;
    }

    /// <summary>
    /// Issue #1470: SharedModeMonitor からの接続状態遷移を受けて UI とトーストを更新する。
    /// </summary>
    /// <remarks>
    /// イベントは thread pool スレッドから発火される可能性があるため、
    /// UI プロパティ更新と Toast 発火は IDispatcherService で UI スレッドに
    /// マーシャリングする（OnSharedModeHealthCheckCompleted と同パターン）。
    /// Toast は「遷移エッジ」でのみ発火させ、同一状態の継続による連続通知を抑止する。
    /// </remarks>
    private void OnSharedDbConnectionStateChanged(object sender, SharedDbConnectionStateChangedEventArgs e)
    {
        _dispatcherService.InvokeAsync(() =>
        {
            SharedDbConnectionState = e.NewState;

            // 初回切断検知時のみ Toast 発火（Reconnecting → Disconnected の再失敗時は抑止）
            if (e.NewState == SharedDbConnectionState.Disconnected
                && e.OldState == SharedDbConnectionState.Connected)
            {
                _toastNotificationService.ShowWarning(
                    "共有DB接続が切断されました",
                    "ネットワーク接続を確認してください。15秒ごとに自動で再接続を試行します。");
            }
            // 切断状態（Disconnected/Reconnecting）からの復帰時のみ Toast 発火
            else if (e.NewState == SharedDbConnectionState.Connected
                     && (e.OldState == SharedDbConnectionState.Disconnected
                         || e.OldState == SharedDbConnectionState.Reconnecting))
            {
                _toastNotificationService.ShowInfo(
                    "共有DB接続が復旧しました",
                    "データの同期を再開しました。");
            }
        });
    }

    /// <summary>
    /// DB接続警告のUI表示を更新
    /// </summary>
    private void UpdateConnectionWarning(bool isConnected)
    {
        if (isConnected)
        {
            var existing = WarningMessages
                .FirstOrDefault(w => w.Type == WarningType.DatabaseConnectionLost);
            if (existing != null)
                WarningMessages.Remove(existing);
        }
        else
        {
            if (!WarningMessages.Any(w => w.Type == WarningType.DatabaseConnectionLost))
            {
                WarningMessages.Add(new WarningItem
                {
                    Type = WarningType.DatabaseConnectionLost,
                    DisplayText = "ネットワーク共有フォルダへの接続が切断されています。ネットワーク接続を確認してください。"
                });
            }
        }
    }

    /// <summary>
    /// 共有モードでの定期データリフレッシュ（他PCの変更を反映）
    /// </summary>
    /// <remarks>
    /// Issue #1923: 履歴一覧の再読込でチェック（統合対象の選択）を引き継ぐことを
    /// 検証するため internal で公開している。
    /// </remarks>
    internal async Task RefreshSharedDataAsync()
    {
        try
        {
            // 処理中（カードタッチ対応中）はリフレッシュをスキップ
            if (CurrentState == AppState.Processing)
                return;

            await RefreshLentCardsAsync();
            await RefreshDashboardAsync();

            // Issue #1381: 履歴画面が開いていれば、他PCで発生した変更を反映する
            // （貸出/返却/チャージ処理後と同じ "if (IsHistoryVisible) LoadHistoryLedgersAsync" パターン）
            //
            // Issue #1923: この再読込は利用者の操作を契機としないため、統合対象として入れた
            // チェックを引き継ぐ。引き継がないと、15 秒周期のリフレッシュが利用者の選択操作を
            // 途中で消してしまい、隣接 2 行以上を選ぶ統合が事実上できなくなる。
            if (History.IsHistoryVisible)
            {
                await History.LoadHistoryLedgersAsync(preserveCheckedRows: true);
            }

            // Issue #1110, #1131: 最終同期時刻を記録
            _sharedModeMonitor.RecordRefresh();
        }
        catch (Exception ex)
        {
            // Issue #1282: 共有モードの定期リフレッシュはタイマー起動のため、失敗しても
            // UI を止めず次回試行に委ねるのが設計意図。ただし無言握りつぶしは
            // ネットワーク切断や DB 破損の兆候を見逃すため、LogDebug で痕跡を残す。
            // 頻繁に呼ばれる処理なので LogWarning ではなく LogDebug とし、
            // 運用時のログ肥大化を避ける。SharedModeMonitor のヘルスチェックが
            // 接続断を別途 UI に通知するため、ユーザー影響は限定的。
            _logger?.LogDebug(ex, "共有モードの定期データリフレッシュに失敗（次回タイマー発火で再試行）");
        }
    }

    /// <summary>
    /// Issue #1131: 手動でデータを即時同期する
    /// </summary>
    [RelayCommand]
    private async Task ManualRefreshAsync()
    {
        if (!IsSharedMode || _sharedModeMonitor.IsHealthCheckRunning)
            return;

        _sharedModeMonitor.SetHealthCheckRunning(true);
        try
        {
            // キャッシュを全クリアして最新データを取得
            _cacheService.Clear();
            await RefreshSharedDataAsync();
        }
        finally
        {
            _sharedModeMonitor.SetHealthCheckRunning(false);
        }
    }

    /// <summary>
    /// 警告チェック（従来版、必要に応じて使用）。
    /// internal: テストから直接呼び出して挙動を検証するため（Issue #1739）。
    /// </summary>
    internal async Task CheckWarningsAsync()
    {
        var settings = await _settingsRepository.GetAppSettingsAsync();
        ApplyDataWarnings(settings.WarningBalance);
        await CheckIncompleteBusStopsAsync();
    }

    /// <summary>
    /// Issue #504: ダッシュボードデータからデータ系の警告を生成・適用（WarningServiceに委譲）
    /// </summary>
    /// <remarks>
    /// Issue #1739: 取り除くのは「本メソッドがこの直後に作り直す種別」だけに限る。
    /// 以前は保持する種別を列挙して残りを <c>Clear()</c> していたが、その形は
    /// WarningType を新設するたびに保持リストを更新する義務を生み、実際 Issue #1689 の
    /// <see cref="WarningType.BackupStale"/> と <see cref="WarningType.BalanceInconsistency"/> が
    /// 漏れて「起動直後の最初のカード操作で警告が消え、そのセッション中は復活しない」状態になっていた。
    /// 保持側ではなくクリア側を列挙すれば、再生成手段を持たない新しい種別は既定で残る。
    /// </remarks>
    private void ApplyDataWarnings(int warningBalance)
    {
        // 直後に作り直す残額警告のみ取り除く（他種別はそれぞれのチェックメソッドが管理する）
        ReplaceWarnings(
            w => w.Type == WarningType.LowBalance,
            _warningService.CheckLowBalanceWarnings(CardBalanceDashboard, warningBalance));
    }

    /// <summary>
    /// Issue #1739: 条件に一致する既存の警告を取り除き、新しい警告で置き換える。
    /// </summary>
    /// <remarks>
    /// 「各チェックメソッドは自分が生成する種別だけを入れ替える」という規約
    /// （04_機能設計書 §7.4）の実装を1か所に集約する。追加のみで書くと他メソッドの
    /// 事前クリアに依存することになり、その依存先を変えた瞬間か、fire-and-forget と
    /// 並走したときに重複表示になる。
    /// </remarks>
    /// <param name="selector">取り除く対象を選ぶ述語</param>
    /// <param name="replacements">追加し直す警告（null・空なら除去のみ行う）</param>
    private void ReplaceWarnings(
        Func<WarningItem, bool> selector,
        IEnumerable<WarningItem> replacements = null)
    {
        foreach (var stale in WarningMessages.Where(selector).ToList())
        {
            WarningMessages.Remove(stale);
        }

        if (replacements == null) return;

        foreach (var warning in replacements)
        {
            WarningMessages.Add(warning);
        }
    }

    /// <summary>
    /// Issue #1739: 非同期チェックの結果が陳腐化していないかを判定するための世代番号。
    /// </summary>
    /// <remarks>
    /// バス停未入力チェックとバックアップ健全性チェックは起動時に fire-and-forget で走る。
    /// 共有モードの SMB 遅延でそれが保留している間に、ユーザー操作起点の同じチェックが
    /// 完了することがある。await 前に取得したデータから作った警告をそのまま書き戻すと、
    /// 解消済みの警告を復活させてしまうため、より新しいチェックが始まっていたら破棄する。
    /// ViewModel はすべて UI スレッド上で動くため、単純なインクリメントで足りる。
    /// </remarks>
    private int _busStopCheckSequence;
    private int _backupHealthCheckSequence;
    private int _carryoverDataLossCheckSequence;

    /// <summary>
    /// バス停名未入力チェック（WarningServiceに委譲）。
    /// internal: テストから直接呼び出して挙動を検証するため（Issue #1739）。
    /// </summary>
    /// <remarks>
    /// Issue #1739: 自分が出す種別を入れ替える形（<see cref="ReplaceWarnings"/>）にして、
    /// <see cref="ApplyDataWarnings"/> の事前クリアに依存しない。起動時の本メソッドは
    /// fire-and-forget で走るため、完了前にカード操作が入ると警告再チェックと並走して
    /// 二重に追加され得た。<c>CheckBackupHealthAsync</c> と同じ形。
    /// </remarks>
    internal async Task CheckIncompleteBusStopsAsync()
    {
        var sequence = ++_busStopCheckSequence;
        var warning = await _warningService.CheckIncompleteBusStopsAsync();

        // より新しいチェックが始まっていれば、この結果は陳腐化している（そちらが書き戻す）
        if (sequence != _busStopCheckSequence) return;

        ReplaceWarnings(
            w => w.Type == WarningType.IncompleteBusStop,
            warning == null ? null : new[] { warning });
    }


    /// <summary>
    /// 貸出中カードを更新
    /// </summary>
    private async Task RefreshLentCardsAsync()
    {
        var lentCards = await _cardRepository.GetLentAsync();
        LentCards.Clear();
        foreach (var card in lentCards)
        {
            LentCards.Add(card.ToDto());
        }
    }

    /// <summary>
    /// カード残高ダッシュボードを更新（DashboardServiceに委譲）
    /// </summary>
    private async Task RefreshDashboardAsync()
    {
        var result = await _dashboardService.BuildDashboardAsync(DashboardSortOrder);
        CardBalanceDashboard.Clear();
        foreach (var item in result.Items)
        {
            CardBalanceDashboard.Add(item);
        }

        // Issue #1739: 有効でなくなったカードの残高不整合警告を取り除く。
        // 生成元（履歴パネル HistoryPanelViewModel の CheckAndNotifyConsistencyAsync / CheckAllCardsConsistencyAsync）はどちらも
        // is_deleted = 0 のカードしか走査しないため、カードを論理削除すると除去経路が無くなり、
        // クリックしても履歴が開かない警告が再起動まで残る（旧実装では ApplyDataWarnings の
        // Clear() が巻き添えで消していた）。ダッシュボードは DashboardService が
        // CardRepository.GetAllAsync から組む「有効なカードの母集団」そのもののため、
        // 最新のカード集合を知れるのはここ。
        //
        // Issue #1908: 残額の食い違い警告（CardBalanceMismatch）も生成元が単独タッチ時に限られ、
        // カードを論理削除・払い戻しするとその経路自体が無くなるため、同じ手当てが要る。
        var activeCardIdms = new HashSet<string>(CardBalanceDashboard.Select(i => i.CardIdm));
        ReplaceWarnings(w => (w.Type == WarningType.BalanceInconsistency
                              || w.Type == WarningType.CardBalanceMismatch)
                             && !activeCardIdms.Contains(w.CardIdm));
    }

    /// <summary>
    /// ソート順変更時にダッシュボードを再ソート（DashboardServiceに委譲）
    /// </summary>
    partial void OnDashboardSortOrderChanged(DashboardSortOrder value)
    {
        var sortedItems = _dashboardService.SortItems(CardBalanceDashboard.ToList(), value);
        CardBalanceDashboard.Clear();
        foreach (var item in sortedItems)
        {
            CardBalanceDashboard.Add(item);
        }
    }
}
