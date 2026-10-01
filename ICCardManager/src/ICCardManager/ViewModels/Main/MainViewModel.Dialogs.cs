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
    // === ダイアログ起動・警告クリック・再接続 ===

    /// <summary>
    /// 設定画面を開く
    /// </summary>
    [RelayCommand]
    public async Task OpenSettingsAsync()
    {
        _navigationService.ShowDialog<Views.Dialogs.SettingsDialog>();

        // 設定変更後に音声モードを再適用し、カード一覧を更新（残額警告閾値の変更を反映）
        var settings = await _settingsRepository.GetAppSettingsAsync();
        _soundPlayer.SoundMode = settings.SoundMode;
        await RefreshDashboardAsync();
        // Issue #661: 残額警告の閾値変更後に警告メッセージを更新
        await CheckWarningsAsync();
    }

    /// <summary>
    /// 帳票作成画面を開く
    /// </summary>
    [RelayCommand]
    public void OpenReport()
    {
        _navigationService.ShowDialog<Views.Dialogs.ReportDialog>();
    }

    /// <summary>
    /// カード管理画面を開く
    /// </summary>
    [RelayCommand]
    public async Task OpenCardManageAsync()
    {
        _navigationService.ShowDialog<Views.Dialogs.CardManageDialog>();

        // ダイアログを閉じた後、貸出中カード一覧とダッシュボードを更新
        await RefreshLentCardsAsync();
        await RefreshDashboardAsync();

        // Issue #1758: カードの論理削除で繰越情報消失の母集団が変わる。カード管理画面が唯一の入口のため、
        // ここで再判定しないと「クリックしても対象が無い警告」が再起動まで残る（Issue #1739 の教訓）。
        await CheckCarryoverDataLossAsync();
    }

    /// <summary>
    /// 職員管理画面を開く
    /// </summary>
    [RelayCommand]
    public void OpenStaffManage()
    {
        _navigationService.ShowDialog<Views.Dialogs.StaffManageDialog>();
    }

    /// <summary>
    /// データエクスポート/インポート画面を開く
    /// </summary>
    [RelayCommand]
    public async Task OpenDataExportImportAsync()
    {
        Views.Dialogs.DataExportImportDialog capturedExportDialog = null;
        _navigationService.ShowDialog<Views.Dialogs.DataExportImportDialog>(
            d => capturedExportDialog = d);

        // Issue #744: インポートが実行された場合、履歴一覧・ダッシュボードを即座に更新
        var viewModel = capturedExportDialog?.DataContext as DataExportImportViewModel;
        if (viewModel?.HasImported == true)
        {
            await RefreshDashboardAsync();
            if (IsHistoryVisible)
            {
                await LoadHistoryLedgersAsync();
            }
            // Issue #1058: インポート後に警告・残高整合性チェックを実行
            // CheckAndNotifyConsistencyAsyncはHistoryCard依存のため、全カード対象チェックを使用
            await CheckWarningsAsync();
            await CheckAllCardsConsistencyAsync();
        }
    }

    /// <summary>
    /// 操作ログ画面を開く
    /// </summary>
    [RelayCommand]
    public void OpenOperationLog()
    {
        _navigationService.ShowDialog<Views.Dialogs.OperationLogDialog>();
    }

    /// <summary>
    /// システム管理画面を開く
    /// </summary>
    /// <remarks>
    /// Issue #1739: 閉じたあとにバックアップ健全性を再判定する。BackupStale 警告の文言自体が
    /// 「システム管理画面（F6）で…手動バックアップを実行してください」と案内しているため、
    /// 再判定を警告クリック経由だけに置くと、案内どおり F6 を押した管理者には
    /// 「復旧したのに警告が消えない」ように見え、復旧済みの原因調査を続けさせてしまう。
    /// </remarks>
    [RelayCommand]
    public async Task OpenSystemManage()
    {
        _navigationService.ShowDialog<Views.Dialogs.SystemManageDialog>();

        // ダイアログ内で手動バックアップを実行した可能性があるため、警告を再判定する
        await CheckBackupHealthAsync();
    }

    /// <summary>
    /// 管理者ダッシュボード画面を開く（Issue #1692）
    /// </summary>
    /// <remarks>
    /// メイン画面内のカード残高ダッシュボード（<see cref="CardBalanceDashboard"/>）とは別物で、
    /// 貸出中・長期未返却・残額不足・帳票未出力の統制情報と利用分析をまとめて表示する。
    /// </remarks>
    [RelayCommand]
    public void OpenAdminDashboard()
    {
        _navigationService.ShowDialog<Views.Dialogs.AdminDashboardDialog>();
    }

    /// <summary>
    /// ヘルプ（ドキュメントフォルダ）を開く（Issue #641）
    /// </summary>
    [RelayCommand]
    public void OpenHelp()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var docsPath = System.IO.Path.Combine(exeDir, "Docs");

        // Issue #1465: ISafeFileLauncher 経由で explorer.exe を直接起動
        var result = _safeFileLauncher.LaunchFolder(docsPath);
        if (!result.Success)
        {
            _navigationService.ShowWarning(
                result.ErrorMessage + "\n\nアプリケーションの再インストールで復旧する可能性があります。",
                "ヘルプ");
        }
    }

    /// <summary>
    /// ダッシュボードから履歴を表示
    /// </summary>
    [RelayCommand]
    public async Task OpenCardHistoryFromDashboard(CardBalanceDashboardItem? item)
    {
        if (item == null) return;

        _balanceInconsistencies.Clear();
        var card = await _cardRepository.GetByIdmAsync(item.CardIdm);
        if (card != null)
        {
            await ShowHistoryAsync(card);
        }
    }

    /// <summary>
    /// Issue #672: 警告クリック時の処理
    /// </summary>
    [RelayCommand]
    public async Task HandleWarningClick(WarningItem warning)
    {
        if (warning == null) return;

        switch (warning.Type)
        {
            case WarningType.LowBalance:
                // 残額警告: 直接カード履歴を表示
                _balanceInconsistencies.Clear();
                var lowBalanceCard = await _cardRepository.GetByIdmAsync(warning.CardIdm);
                if (lowBalanceCard != null)
                {
                    await ShowHistoryAsync(lowBalanceCard);
                }
                break;

            case WarningType.CardBalanceMismatch:
                // Issue #1908: 残額の食い違い警告: 該当カードの履歴を表示する。
                // 文言が「履歴を確認し」と案内する以上、クリックでその履歴へ到達できること。
                // ここで再判定はしない（実残額はカードがリーダーに載っているときしか読めず、
                // 読めないまま「解消した」と判断すると警告が黙って消える）。
                _balanceInconsistencies.Clear();
                var mismatchCard = await _cardRepository.GetByIdmAsync(warning.CardIdm);
                if (mismatchCard != null)
                {
                    await ShowHistoryAsync(mismatchCard);
                }
                break;

            case WarningType.BalanceInconsistency:
                // Issue #1052: 残高不整合警告: カード履歴を表示し、不整合行をハイライト
                var card = await _cardRepository.GetByIdmAsync(warning.CardIdm);
                if (card != null)
                {
                    // Issue #2007: 導入時残高の誤りなら、導入行（何年も前になり得る）を画面に出すため
                    // その日付から表示する。当月だけ表示すると直すべき行が期間外で見えない。
                    var fullPeriodResult = await _ledgerConsistencyChecker.CheckBalanceConsistencyAsync(
                        card.CardIdm, FullPeriodStart, FullPeriodEnd);
                    await ShowHistoryAsync(card, fullPeriodResult.InitialBalanceCorrection?.Date);
                    // ShowHistoryAsync後に期間が確定するため、ここで整合性チェック＆ハイライト適用
                    // CheckAndNotifyConsistencyAsync内で_balanceInconsistenciesの更新とマーキングを行う
                    // （全期間の結果は直前に取ったものを渡して再取得しない）
                    await CheckAndNotifyConsistencyAsync(fullPeriodResult);
                }
                break;

            case WarningType.IncompleteBusStop:
                // バス停未入力警告: 一覧ダイアログを表示（Issue #703: ダイアログ内で直接バス停名入力）
                _navigationService.ShowDialog<Views.Dialogs.IncompleteBusStopDialog>();

                // Issue #1010: バス停名入力後に履歴画面を即時反映
                if (IsHistoryVisible)
                {
                    await LoadHistoryLedgersAsync();
                }

                // ダイアログ内でバス停名が入力された可能性があるため、警告を更新
                await CheckWarningsAsync();
                break;

            case WarningType.DatabaseConnectionLost:
                // Issue #1110: 接続断警告クリックで手動再接続を試行
                await RetryDatabaseConnectionAsync();
                break;

            case WarningType.CarryoverDataLoss:
                // Issue #1758: 繰越情報消失警告クリックで、失われた元の値の一覧を表示する。
                // 復旧は DB の直接修正でしか行えないため、ここでは値を確認できることが目的。
                _navigationService.ShowDialog<Views.Dialogs.CarryoverDataLossDialog>();

                // ダイアログを開いている間に他PCで復旧された場合に備えて再判定する
                await CheckCarryoverDataLossAsync();
                break;

            case WarningType.BackupStale:
                // Issue #1689: バックアップ健全性警告クリックでシステム管理画面を開く。
                // 警告文言が案内する「システム管理画面（F6）」へ、キー操作を覚えていなくても到達できるようにする。
                // Issue #1739: 画面表示と再判定は F6 と同一の経路（OpenSystemManage）に集約する。
                await OpenSystemManage();
                break;

            case WarningType.CardReaderError:
                // Issue #1811: カードリーダーエラー警告は利用者が確認したらクリックで取り除く。
                // 自動で解消する契機が無いため、これが唯一の除去経路（04_機能設計書 §7.4）。
                // 取り除くと繰り返し回数も振り出しに戻る（回数は警告行自身が持つ）。
                ReplaceWarnings(w => w.Type == WarningType.CardReaderError);
                break;
        }
    }

    /// <summary>
    /// Issue #1110: データベース接続の手動再接続を試行
    /// </summary>
    internal async Task RetryDatabaseConnectionAsync()
    {
        var isConnected = await _sharedModeMonitor.CheckConnectionAsync();
        UpdateConnectionWarning(isConnected);

        // 接続が復旧した場合はデータもリフレッシュ
        if (isConnected)
        {
            await RefreshSharedDataAsync();
        }
    }
}
