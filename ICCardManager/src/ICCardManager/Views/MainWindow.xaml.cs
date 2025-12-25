using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.ViewModels;

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
        }

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

        private async void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                // ウィンドウ位置・サイズを保存
                await SaveWindowPositionAsync();
            }
            catch (Exception ex)
            {
                // 終了時のエラーは警告のみ（アプリ終了を妨げない）
                System.Diagnostics.Debug.WriteLine($"[MainWindow] 終了時エラー: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"[MainWindow] ウィンドウ位置を保存: Left={settings.MainWindowSettings.Left}, Top={settings.MainWindowSettings.Top}, Width={settings.MainWindowSettings.Width}, Height={settings.MainWindowSettings.Height}, Maximized={settings.MainWindowSettings.IsMaximized}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] ウィンドウ位置の保存に失敗: {ex.Message}");
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
                    System.Diagnostics.Debug.WriteLine("[MainWindow] 保存されたウィンドウ位置がありません。デフォルトを使用します。");
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

                System.Diagnostics.Debug.WriteLine($"[MainWindow] ウィンドウ位置を復元: Left={left}, Top={top}, Width={width}, Height={height}, Maximized={windowSettings.IsMaximized}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] ウィンドウ位置の復元に失敗: {ex.Message}");
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

            System.Diagnostics.Debug.WriteLine($"[MainWindow] 画面外補正を適用: Left={left}, Top={top}");
            return (left, top, width, height);
        }
    }
}
