#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using ICCardManager.Common;
using ICCardManager.ViewModels;
using ICCardManager.Views.Helpers;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 繰越情報が失われたカードの一覧ダイアログ（Issue #1758）
    /// </summary>
    /// <remarks>
    /// 失われた元の値を表示し（Issue #1758）、行ごとの「復旧...」から復旧ダイアログを開く（Issue #2255）。
    /// </remarks>
    public partial class CarryoverDataLossDialog : Window
    {
        private readonly CarryoverDataLossViewModel _viewModel;

        public CarryoverDataLossDialog(CarryoverDataLossViewModel viewModel)
        {
            InitializeComponent();

            // Issue #2258: 既定の Width（1150）は文字サイズ「特大」でも横スクロールせずに収まる幅だが、1366 幅で
            // 表示倍率 125% のノート PC（作業領域 約 1093）でははみ出し、CenterOwner は画面内へ補正しないため
            // 左右が切れて閉じるボタンが画面外に出る。メイン画面（Issue #2150）と同じく作業領域の幅で切り詰める
            // （収まらない分は一覧の横スクロールに任せる）。XAML の Width を上書きするため InitializeComponent() の後に置く。
            // ここでの WorkArea はプライマリモニターの作業領域（まだどのモニターに載るか決まっていないため）。
            // メイン画面がより狭い別のモニターにあると足りない（#2150 と同じ既知の制限）。位置は Loaded で、
            // 載ったモニターの作業領域の中へ寄せる。
            Width = WindowLayoutCalculator.FitWidth(Width, SystemParameters.WorkArea.Width);

            _viewModel = viewModel;
            DataContext = _viewModel;

            Loaded += async (s, e) =>
            {
                // CenterOwner はオーナーの中心に置くだけで画面内へ寄せない。オーナー（メイン画面）が右寄りにあると、
                // 幅を詰めても右端がはみ出して閉じるボタンが画面外に出るので、表示した位置を作業領域の中へ寄せる。
                // 寄せる先は「このダイアログが載っているモニター」（＝オーナーのモニター）の作業領域。
                // SystemParameters.WorkArea（プライマリー）で寄せると、メイン画面をサブモニターで使っているとき
                // ダイアログだけがプライマリーへ移ってしまう。取れなければ位置を変えない
                if (MonitorWorkArea.Of(this) is Rect workArea)
                {
                    Left = WindowLayoutCalculator.ClampStart(Left, ActualWidth, workArea.Left, workArea.Width);
                    Top = WindowLayoutCalculator.ClampStart(Top, ActualHeight, workArea.Top, workArea.Height);
                }

                try
                {
                    await _viewModel.InitializeAsync();
                }
                catch (Exception ex)
                {
                    // Issue #1614: 生の例外メッセージをユーザーへ出さない。技術的詳細はログへ逃がす。
                    ErrorDialogHelper.LogException(ex, "繰越情報消失一覧の読み込み");
                    // Issue #1837: オーナーを渡さないと ownerless になり、背後のこのダイアログが
                    // 無効化されない。自ウィンドウが正しいオーナーなので this を渡す。
                    MessageBox.Show(
                        this,
                        ExceptionMessageFormatter.ToUserMessage(ex, "繰越情報消失一覧の読み込み"),
                        "エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            };
        }

        /// <summary>
        /// コピー（Ctrl+C）から「復旧」の列を除く（Issue #2258）
        /// </summary>
        /// <remarks>
        /// 「復旧」は横スクロールしても見えるよう左から 2 列目に固定している。値を持たないボタンの列が
        /// 値の列の間に空の列として出ると、貼り付けた先で値の列がずれる。
        /// </remarks>
        private void CarryoverDataLossDataGrid_CopyingRowClipboardContent(object sender, DataGridRowClipboardEventArgs e)
            => DataGridClipboardColumnFilter.RemoveColumn(e.ClipboardRowContent, RecoverColumn);

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
