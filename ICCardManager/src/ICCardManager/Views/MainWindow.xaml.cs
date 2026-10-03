#nullable enable
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

            // Issue #2150: 既定の Width（1650）は 1366px 幅の PC では画面からはみ出し、CenterScreen で
            // 左右が切れる。保存済みの位置が無い初回起動でも画面に収まるよう、作業領域の幅で切り詰める。
            // XAML の Width を上書きするため InitializeComponent() の後に置く（前に置くと XAML の 1650 に戻る）。
            // WorkArea はプライマリモニターの作業領域。CenterScreen はマウスカーソルのあるモニターの中央に置くため、
            // カーソルがプライマリより狭いモニターにあると切り詰めが足りない（既知の制限。03_画面設計書 §3.1.1a）。
            Width = WindowLayoutCalculator.FitWidth(Width, SystemParameters.WorkArea.Width);

            _viewModel = viewModel;
            _settingsRepository = settingsRepository;
            DataContext = _viewModel;

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            // Issue #2159: 返却確認（IsReturnHistoryReview）は子の履歴パネルが持つ
            _viewModel.History.PropertyChanged += HistoryPanel_PropertyChanged;
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

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (ShouldAnnounceNextAction(e.PropertyName))
            {
                LiveRegionAnnouncer.Announce(NextActionMessageText, "次の操作ガイドのスクリーンリーダーへの読み上げ");
            }
        }

        /// <summary>
        /// Issue #1907: 返却確認の履歴が開いたら、今回の返却で記録された最初の行までスクロールする。
        /// </summary>
        /// <remarks>
        /// 一覧は日付昇順なので今回の行は末尾に来る。ページは ViewModel が最終ページへ合わせるが、
        /// 1 ページに収まる場合でも DataGrid は先頭を表示するため、スクロールしないと ✔ の行が画面外に残る。
        /// ViewModel は「今回の行」を知っているが表示位置は知らない（View の責務）。
        /// </remarks>
        private void HistoryPanel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var history = _viewModel.History;
            if (e.PropertyName != nameof(HistoryPanelViewModel.IsReturnHistoryReview) || !history.IsReturnHistoryReview)
            {
                return;
            }

            var firstRecorded = history.HistoryLedgers.FirstOrDefault(d => d.IsRecentlyRecorded);
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
            _viewModel.History.MarkReturnHistoryReviewTouched();
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
            _viewModel.History.HistoryOpenMonthSelector();
        }

        private void HistoryOtherMonthButton_Click(object sender, RoutedEventArgs e)
        {
            HistoryMonthSelectorPopup.PlacementTarget = HistoryOtherMonthButton;
            _viewModel.History.HistoryOpenMonthSelector();
        }

        /// <summary>
        /// 終了時にウィンドウ位置・サイズの保存を待つ上限（共有モードの busy_timeout 15 秒より短く、終了を長く止めない）。
        /// </summary>
        private static readonly TimeSpan WindowPositionSaveTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// 終了時にウィンドウ位置・サイズを保存する。
        /// </summary>
        /// <remarks>
        /// Issue #2202: <c>async void</c> で保存を <c>await</c> すると、最初の <c>await</c> でハンドラーが戻った後にアプリの終了処理が
        /// 先に進み、保存が走らないまま終わる（UI スレッドから呼んだ DB の完了は UI スレッドへ Post してから伝えるため、終了処理より
        /// 後ろに並ぶ）。終了ボタンの経路（<c>Application.Shutdown</c>）では <c>Closing</c> を取り消せないので、取り消して待つ形も使えない。
        /// 位置は UI スレッドで読み取り、保存はスレッドプールで始めて上限付きで待つ（<see cref="UiThreadBlockingWait"/>）。
        /// #2202 以前も、終了時の保存は UI スレッドの上で同期的に走り切っていた。
        /// <para>
        /// 限界: 閉じる時点で、UI から始めた DB の処理の完了通知がまだ配られていない（UI スレッドへ Post された通知を処理する前に
        /// 閉じた）と、その処理が持つゲート・セマフォ・キャッシュのキーのロックが返らず、保存はそれを待って上限に達し、保存されずに
        /// 終わる。✕・Alt+F4・終了ボタンは確認の MessageBox の間にメッセージを処理するので通常は起きず、確認を挟まない OS の
        /// サインアウトの短い時間帯に限られる。位置が保存されないだけで、台帳のデータには影響しない。
        /// </para>
        /// </remarks>
        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                var position = CaptureWindowPosition();
                var completed = UiThreadBlockingWait.RunOnThreadPoolAndWait(
                    () => SaveWindowPositionAsync(position), WindowPositionSaveTimeout);
                if (!completed)
                {
                    // Release でも痕跡を残す（終了を止めないため、待たずに終了する）
                    ErrorDialogHelper.LogException(
                        new TimeoutException($"終了時のウィンドウ位置の保存が {WindowPositionSaveTimeout.TotalSeconds:0} 秒以内に終わらなかったため、待たずに終了しました。"),
                        "終了時のウィンドウ位置の保存");
                }
            }
            catch (Exception ex)
            {
                // 終了時のエラーはログのみ（アプリ終了を妨げない）
                ErrorDialogHelper.LogException(ex, "終了時のウィンドウ位置の保存");
            }
        }

        /// <summary>
        /// 保存するウィンドウ位置・サイズ（UI スレッドで読み取った値）。
        /// </summary>
        private readonly struct WindowPosition
        {
            public WindowPosition(bool isMaximized, double left, double top, double width, double height)
            {
                IsMaximized = isMaximized;
                Left = left;
                Top = top;
                Width = width;
                Height = height;
            }

            public bool IsMaximized { get; }

            public double Left { get; }

            public double Top { get; }

            public double Width { get; }

            public double Height { get; }
        }

        /// <summary>
        /// 現在のウィンドウ位置・サイズを読み取る（UI スレッドで呼ぶ）。最大化中は通常時の位置・サイズ（RestoreBounds）を返す。
        /// </summary>
        private WindowPosition CaptureWindowPosition()
        {
            var isMaximized = WindowState == WindowState.Maximized;
            return isMaximized
                ? new WindowPosition(true, RestoreBounds.Left, RestoreBounds.Top, RestoreBounds.Width, RestoreBounds.Height)
                : new WindowPosition(false, Left, Top, Width, Height);
        }

        /// <summary>
        /// ウィンドウ位置・サイズを保存（画面の値を読まないので、スレッドプールで走らせてよい）
        /// </summary>
        private async Task SaveWindowPositionAsync(WindowPosition position)
        {
            try
            {
                var settings = await _settingsRepository.GetAppSettingsAsync();
                settings.MainWindowSettings.IsMaximized = position.IsMaximized;
                settings.MainWindowSettings.Left = position.Left;
                settings.MainWindowSettings.Top = position.Top;
                settings.MainWindowSettings.Width = position.Width;
                settings.MainWindowSettings.Height = position.Height;

                await _settingsRepository.SaveAppSettingsAsync(settings);
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[MainWindow] ウィンドウ位置を保存: Left={settings.MainWindowSettings.Left}, Top={settings.MainWindowSettings.Top}, Width={settings.MainWindowSettings.Width}, Height={settings.MainWindowSettings.Height}, Maximized={settings.MainWindowSettings.IsMaximized}");
#endif
            }
            catch (Exception ex)
            {
                // Release でも痕跡を残す（終了時の保存の失敗は画面に出さない）
                ErrorDialogHelper.LogException(ex, "終了時のウィンドウ位置の保存");
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
                var correctedBounds = EnsureWindowIsVisible(left, top, width, height, MinWidth, MinHeight);
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
        /// <param name="minWidth">ウィンドウの最小幅（画面外補正で中央へ置くときに、WPF が引き上げる実際の幅を見込むため）</param>
        /// <param name="minHeight">ウィンドウの最小高さ（同上）</param>
        /// <returns>補正後の座標・サイズ</returns>
        private static (double Left, double Top, double Width, double Height) EnsureWindowIsVisible(
            double left, double top, double width, double height, double minWidth, double minHeight)
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
                // Issue #2150: 最小幅 1400 の時代に保存された幅は 1366px 幅の PC でも必ず 1400 以上なので、
                // そのまま復元すると右端がはみ出す。仮想スクリーンの幅で切り詰める（プライマリの作業領域で
                // 切り詰めると、より広いセカンダリモニターに置いていたウィンドウまで狭めてしまう）。
                width = WindowLayoutCalculator.FitWidth(width, virtualWidth);

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

            // 画面外の場合、プライマリモニターの作業領域の中央に配置
            // Issue #2150: 9 割へ縮めた幅は最小幅で引き上げられ得るため、実際に適用される長さで中央を計算する
            var workArea = SystemParameters.WorkArea;
            (left, width) = WindowLayoutCalculator.FitAndCenter(width, minWidth, workArea.Left, workArea.Width);
            (top, height) = WindowLayoutCalculator.FitAndCenter(height, minHeight, workArea.Top, workArea.Height);

#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[MainWindow] 画面外補正を適用: Left={left}, Top={top}");
#endif
            return (left, top, width, height);
        }
    }
}
