using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using ICCardManager.Common;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
/// <summary>
    /// 操作ログ検索ダイアログ
    /// </summary>
    public partial class OperationLogDialog : Window
    {
        private readonly OperationLogSearchViewModel _viewModel;

        public OperationLogDialog(OperationLogSearchViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            // 画面表示時に初期検索を実行
            Loaded += OperationLogDialog_Loaded;

            // Issue #1548: ViewModel の PropertyChanged を購読し、対応 TextBlock の
            // LiveRegionChanged を明示発火する（NVDA / Narrator がテキスト変化を読み上げるため）。
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Closed += OnClosed;
        }

        private async void OperationLogDialog_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.InitializeAsync();

                // Issue #787: 最新のログが下に表示されるため、一番下までスクロール
                ScrollDataGridToBottom();
            }
            catch (Exception ex)
            {
                ErrorDialogHelper.ShowError(ex, "初期化エラー");
            }
        }

        /// <summary>
        /// DataGridを一番下までスクロール（Issue #787）
        /// </summary>
        private void ScrollDataGridToBottom()
        {
            if (LogsDataGrid.Items.Count > 0)
            {
                LogsDataGrid.ScrollIntoView(LogsDataGrid.Items[LogsDataGrid.Items.Count - 1]);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Issue #1548: ViewModel の PropertyChanged を受け、対応する TextBlock に
        /// LiveRegionChanged を発火する。AutomationProperties.LiveSetting="Polite" 単独では
        /// 発火しないため、明示的に <c>UIElementAutomationPeer.RaiseAutomationEvent</c> を呼ぶ
        /// （Issue #1509 で StaffAuthDialog に確立されたパターンを ViewModel バインド向けに適用）。
        /// </summary>
        // Issue #1507 診断ログ: CurrentPageNumberText だけ Narrator が読み上げない原因切り分け用。
        // ファイル出力先: %TEMP%\ICCardManager_LiveRegionDiag.log（IDE 不要でユーザーが直接確認可能）。
        // 原因特定後にこの診断コードは revert で除去する（一時的な調査用コード）。
        private static readonly string DiagLogPath =
            Path.Combine(Path.GetTempPath(), "ICCardManager_LiveRegionDiag.log");

        private static void WriteDiagLog(string message)
        {
            try
            {
                File.AppendAllText(
                    DiagLogPath,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // 診断ログ書き込み失敗はアプリ本体に影響させない（一時コードのため）
            }
            Debug.WriteLine(message);
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            var isBusy = (sender as OperationLogSearchViewModel)?.IsBusy ?? false;
            var targetName = GetTargetElementName(e.PropertyName, isBusy);

            WriteDiagLog(
                $"[LiveRegion #1507] PropertyChanged: name='{e.PropertyName}', isBusy={isBusy}, " +
                $"target='{targetName ?? "<null>"}', threadId={System.Threading.Thread.CurrentThread.ManagedThreadId}");

            UIElement? target = targetName switch
            {
                "PageInfoText" => PageInfoText,
                "CurrentPageNumberText" => CurrentPageNumberText,
                "StatusMessageText" => StatusMessageText,
                "ProcessingOverlayText" => ProcessingOverlayText,
                _ => null
            };
            if (target is not null)
            {
                RaiseLiveRegionChanged(target, targetName!);
            }
        }

        /// <summary>
        /// Issue #1548: <see cref="OperationLogSearchViewModel"/> のプロパティ変化に対して
        /// LiveRegion 通知を発火すべき TextBlock の x:Name を返す純粋関数。
        /// 単体テスト容易化のため WPF 依存を排除した <c>static</c> メソッドに分離している。
        /// </summary>
        /// <param name="propertyName">変化した ViewModel プロパティ名。</param>
        /// <param name="isBusy">変化通知時点での IsBusy 値（ProcessingOverlay の Visibility 判定に使用）。</param>
        /// <returns>通知対象 TextBlock の x:Name、対象外なら null。</returns>
        internal static string? GetTargetElementName(string? propertyName, bool isBusy)
        {
            return propertyName switch
            {
                nameof(OperationLogSearchViewModel.PageInfo) => "PageInfoText",
                // Issue #1548/#1507: CurrentPage / TotalPages 単体の通知で旧 Run 構成の CurrentPageNumberText に
                // LiveRegion を発火しても Narrator に届かなかった。派生プロパティ PageNumberDisplay 経由（単一 Text バインド）に
                // 移行し、ViewModel 側の [NotifyPropertyChangedFor] が CurrentPage/TotalPages 変化に応じて
                // PageNumberDisplay の PropertyChanged を発火するため、このマッピングだけで読み上げをカバーできる。
                nameof(OperationLogSearchViewModel.PageNumberDisplay) => "CurrentPageNumberText",
                nameof(OperationLogSearchViewModel.StatusMessage) => "StatusMessageText",
                nameof(OperationLogSearchViewModel.BusyMessage) => "ProcessingOverlayText",
                // IsBusy=true への遷移時のみオーバーレイ出現の通知が必要。false への遷移（非表示）は不要。
                nameof(OperationLogSearchViewModel.IsBusy) => isBusy ? "ProcessingOverlayText" : null,
                _ => null
            };
        }

        private static void RaiseLiveRegionChanged(UIElement element, string targetName)
        {
            // Issue #1507 診断ログ: target.Text が新しい値（例: "2 / 3 ページ"）に更新済みか確認。
            // peer.GetName() は Narrator が読む文字列。両者が一致しない場合は Binding/Peer のキャッシュ問題。
            var textValue = element is TextBlock tb ? tb.Text : "<not TextBlock>";
            var existingPeer = UIElementAutomationPeer.FromElement(element);
            var peer = existingPeer ?? UIElementAutomationPeer.CreatePeerForElement(element);
            var peerName = peer?.GetName() ?? "<peer-null>";
            WriteDiagLog(
                $"[LiveRegion #1507]   target='{targetName}', Text='{textValue}', peer.GetName()='{peerName}', " +
                $"peerExisted={existingPeer is not null}, peerType={peer?.GetType().Name ?? "null"}");

            if (peer is null)
            {
                WriteDiagLog($"[LiveRegion #1507]   peer is null, skipped");
                return;
            }
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            WriteDiagLog($"[LiveRegion #1507]   Raised LiveRegionChanged on '{targetName}'");
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            // メモリリーク防止: コンストラクタで購読した PropertyChanged を必ず解除する。
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            Closed -= OnClosed;
        }
    }
}
