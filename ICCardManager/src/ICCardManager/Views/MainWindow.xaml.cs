using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.ViewModels;
using ICCardManager.Views.Helpers;

namespace ICCardManager.Views
{
/// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly ISettingsRepository _settingsRepository;

        public MainWindow(MainViewModel viewModel, ISettingsRepository settingsRepository)
        {
            InitializeComponent();

            _viewModel = viewModel;
            _settingsRepository = settingsRepository;
            DataContext = _viewModel;

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        /// <summary>
        /// Issue #2143: ✕・Alt+F4・システムメニューの「閉じる」で、終了してよいかを確認する。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 共有 PC でメイン画面を閉じると、誰かが起動し直すまで以後のタッチに何も反応しない。
        /// 確認は「終了」ボタンと同じ <see cref="MainViewModel.ConfirmExit"/> を通す。
        /// </para>
        /// <para>
        /// <b><see cref="Window.Closing"/> では確認しない。</b><c>Closing</c> は OS のサインアウト・シャットダウンや
        /// <c>Application.Shutdown()</c>（「終了」ボタンで確認済み）でも発生するため、そこで尋ねると
        /// 確認が二重になり、OS の終了を確認ダイアログで止めてしまう。利用者の「閉じる」操作だけが通る
        /// <c>WM_SYSCOMMAND(SC_CLOSE)</c> で判定する（判定は <see cref="BusyCloseGuard.IsUserCloseCommand"/> を共有する）。
        /// </para>
        /// </remarks>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(ConfirmUserCloseHook);
        }

        private IntPtr ConfirmUserCloseHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (!BusyCloseGuard.IsUserCloseCommand(msg, wParam))
            {
                return IntPtr.Zero;
            }

            // 確認の表示中に届いた 2 度目の閉じる操作（オーナーを解決できず確認がメイン画面を無効化しない場合の
            // タスクバーからの「閉じる」等）は握り潰す。通すと確認が 2 枚重なる
            if (_isConfirmingExit)
            {
                handled = true;
                return IntPtr.Zero;
            }

            _isConfirmingExit = true;
            try
            {
                if (!_viewModel.ConfirmExit())
                {
                    handled = true;
                }
            }
            finally
            {
                _isConfirmingExit = false;
            }
            return IntPtr.Zero;
        }

        /// <summary>終了確認を表示している間 true（<see cref="ConfirmUserCloseHook"/> の再入ガード）</summary>
        private bool _isConfirmingExit;

        /// <summary>
        /// Issue #1907: 返却確認の履歴が開いたら、今回の返却で記録された最初の行までスクロールする。
        /// </summary>
        /// <remarks>
        /// 一覧は日付昇順なので今回の行は末尾に来る。ページは ViewModel が最終ページへ合わせるが、
        /// 1 ページに収まる場合でも DataGrid は先頭を表示するため、スクロールしないと ✔ の行が画面外に残る。
        /// ViewModel は「今回の行」を知っているが表示位置は知らない（View の責務）。
        /// </remarks>
        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (ShouldAnnounceNextAction(e.PropertyName))
            {
                LiveRegionAnnouncer.Announce(NextActionMessageText, "次の操作ガイドのスクリーンリーダーへの読み上げ");
                return;
            }

            if (e.PropertyName != nameof(MainViewModel.IsReturnHistoryReview) || !_viewModel.IsReturnHistoryReview)
            {
                return;
            }

            var firstRecorded = _viewModel.HistoryLedgers.FirstOrDefault(d => d.IsRecentlyRecorded);
            if (firstRecorded != null)
            {
                HistoryDataGrid.ScrollIntoView(firstRecorded);
            }
        }

        /// <summary>
        /// Issue #2142: 状態が変わったこと（職員証タッチ待ち → 交通系ICカードタッチ待ち 等）を読み上げソフトへ伝えるか。
        /// </summary>
        /// <remarks>
        /// 次の操作ガイドは <c>LiveSetting="Polite"</c> を持っていたが、LiveRegionChanged を発火する箇所が
        /// どこにも無く、状態の変化は一度も読み上げられていなかった（ui-conventions #2073）。
        /// 通知するのは文言そのものを Name に持つ <c>NextActionMessageText</c>（Name は文言にバインド）。
        /// 判断を純関数へ切り出してあるのは、<see cref="Window"/> が STA 依存で xUnit から生成できないため。
        /// </remarks>
        internal static bool ShouldAnnounceNextAction(string propertyName)
            => propertyName == nameof(MainViewModel.NextActionMessage);

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // ウィンドウ位置・サイズを復元
                await RestoreWindowPositionAsync();

                // 初期化を実行
                await _viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                ErrorDialogHelper.ShowError(ex, "初期化エラー");
            }
        }

        /// <summary>
        /// Issue #1907: 履歴表示エリアへのキー・クリック・ホイール操作を、返却確認の履歴に対する
        /// 「職員が使い始めた」印として ViewModel へ伝える（以後は次の職員証タッチでも閉じない）。
        /// </summary>
        /// <remarks>
        /// 自動で閉じる機能の主要な故障は「読んでいる・直している途中で閉じること」。
        /// 複数行を読むためのスクロールはクリックを伴わないため、ホイールも拾う（#2009 と同じ判断）。
        /// 返却確認以外で開いた履歴では no-op。
        /// </remarks>
        private void HistoryArea_PreviewInput(object sender, InputEventArgs e)
        {
            _viewModel.MarkReturnHistoryReviewTouched();
        }

        /// <summary>
        /// 表示期間テキストクリック → 月選択ポップアップを開く (Issue #945)
        /// </summary>
        /// <remarks>
        /// MouseLeftButtonUp（リリース時）で処理する。
        /// MouseLeftButtonDownだとPopupのStaysOpen="False"が
        /// 押下中のマウスを外部クリックと判定し即座にPopupを閉じてしまうため。
        /// </remarks>
        private void HistoryPeriodDisplay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            HistoryMonthSelectorPopup.PlacementTarget = HistoryPeriodDisplayBorder;
            _viewModel.HistoryOpenMonthSelector();
        }

        private void HistoryOtherMonthButton_Click(object sender, RoutedEventArgs e)
        {
            HistoryMonthSelectorPopup.PlacementTarget = HistoryOtherMonthButton;
            _viewModel.HistoryOpenMonthSelector();
        }

        private async void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                // ウィンドウ位置・サイズを保存
                await SaveWindowPositionAsync();
            }
            catch (Exception ex)
            {
                _ = ex; // 警告抑制（DEBUGビルドでのみ使用）
                // 終了時のエラーは警告のみ（アプリ終了を妨げない）
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[MainWindow] 終了時エラー: {ex.Message}");
#endif
            }
        }

        /// <summary>
        /// ウィンドウ位置・サイズを保存
        /// </summary>
        private async Task SaveWindowPositionAsync()
        {
            try
            {
                var settings = await _settingsRepository.GetAppSettingsAsync();

                // 最大化状態を保存
                settings.MainWindowSettings.IsMaximized = WindowState == WindowState.Maximized;

                // 通常状態の位置・サイズを保存（最大化中でもRestoreBoundsから取得可能）
                if (WindowState == WindowState.Maximized)
                {
                    // 最大化中は RestoreBounds から通常時のサイズを取得
                    settings.MainWindowSettings.Left = RestoreBounds.Left;
                    settings.MainWindowSettings.Top = RestoreBounds.Top;
                    settings.MainWindowSettings.Width = RestoreBounds.Width;
                    settings.MainWindowSettings.Height = RestoreBounds.Height;
                }
                else
                {
                    settings.MainWindowSettings.Left = Left;
                    settings.MainWindowSettings.Top = Top;
                    settings.MainWindowSettings.Width = Width;
                    settings.MainWindowSettings.Height = Height;
                }

                await _settingsRepository.SaveAppSettingsAsync(settings);
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[MainWindow] ウィンドウ位置を保存: Left={settings.MainWindowSettings.Left}, Top={settings.MainWindowSettings.Top}, Width={settings.MainWindowSettings.Width}, Height={settings.MainWindowSettings.Height}, Maximized={settings.MainWindowSettings.IsMaximized}");
#endif
            }
            catch (Exception ex)
            {
                _ = ex; // 警告抑制（DEBUGビルドでのみ使用）
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[MainWindow] ウィンドウ位置の保存に失敗: {ex.Message}");
#endif
            }
        }

        /// <summary>
        /// ウィンドウ位置・サイズを復元
        /// </summary>
        private async Task RestoreWindowPositionAsync()
        {
            try
            {
                var settings = await _settingsRepository.GetAppSettingsAsync();
                var windowSettings = settings.MainWindowSettings;

                if (!windowSettings.HasValidSettings)
                {
#if DEBUG
                    System.Diagnostics.Debug.WriteLine("[MainWindow] 保存されたウィンドウ位置がありません。デフォルトを使用します。");
#endif
                    return;
                }

                // 位置・サイズを復元
                var left = windowSettings.Left!.Value;
                var top = windowSettings.Top!.Value;
                var width = windowSettings.Width!.Value;
                var height = windowSettings.Height!.Value;

                // 画面外補正
                var correctedBounds = EnsureWindowIsVisible(left, top, width, height);
                left = correctedBounds.Left;
                top = correctedBounds.Top;
                width = correctedBounds.Width;
                height = correctedBounds.Height;

                // ウィンドウに適用
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
                Width = width;
                Height = height;

                // 最大化状態を復元
                if (windowSettings.IsMaximized)
                {
                    WindowState = WindowState.Maximized;
                }

#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[MainWindow] ウィンドウ位置を復元: Left={left}, Top={top}, Width={width}, Height={height}, Maximized={windowSettings.IsMaximized}");
#endif
            }
            catch (Exception ex)
            {
                _ = ex; // 警告抑制（DEBUGビルドでのみ使用）
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[MainWindow] ウィンドウ位置の復元に失敗: {ex.Message}");
#endif
            }
        }

        /// <summary>
        /// ウィンドウが画面内に収まるように補正
        /// </summary>
        /// <param name="left">左端座標</param>
        /// <param name="top">上端座標</param>
        /// <param name="width">幅</param>
        /// <param name="height">高さ</param>
        /// <returns>補正後の座標・サイズ</returns>
        private static (double Left, double Top, double Width, double Height) EnsureWindowIsVisible(
            double left, double top, double width, double height)
        {
            // 仮想スクリーン領域（全モニターを含む）を取得
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualWidth = SystemParameters.VirtualScreenWidth;
            var virtualHeight = SystemParameters.VirtualScreenHeight;

            // ウィンドウの中心点が仮想スクリーン内にあるかチェック
            var centerX = left + width / 2;
            var centerY = top + height / 2;

            var isVisible = centerX >= virtualLeft &&
                            centerX <= virtualLeft + virtualWidth &&
                            centerY >= virtualTop &&
                            centerY <= virtualTop + virtualHeight;

            if (isVisible)
            {
                // ウィンドウが見える位置にあれば、そのまま返す
                // ただし、ウィンドウが画面端からはみ出している場合は調整
                if (left < virtualLeft)
                {
                    left = virtualLeft;
                }
                if (top < virtualTop)
                {
                    top = virtualTop;
                }
                if (left + width > virtualLeft + virtualWidth)
                {
                    left = virtualLeft + virtualWidth - width;
                }
                if (top + height > virtualTop + virtualHeight)
                {
                    top = virtualTop + virtualHeight - height;
                }

                return (left, top, width, height);
            }

            // 画面外の場合、プライマリモニターの中央に配置
            var primaryWidth = SystemParameters.PrimaryScreenWidth;
            var primaryHeight = SystemParameters.PrimaryScreenHeight;
            var workAreaWidth = SystemParameters.WorkArea.Width;
            var workAreaHeight = SystemParameters.WorkArea.Height;

            // ウィンドウサイズがモニターより大きい場合は調整
            if (width > workAreaWidth)
            {
                width = workAreaWidth * 0.9;
            }
            if (height > workAreaHeight)
            {
                height = workAreaHeight * 0.9;
            }

            // 作業領域の中央に配置
            left = (workAreaWidth - width) / 2;
            top = (workAreaHeight - height) / 2;

#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[MainWindow] 画面外補正を適用: Left={left}, Top={top}");
#endif
            return (left, top, width, height);
        }
    }
}
