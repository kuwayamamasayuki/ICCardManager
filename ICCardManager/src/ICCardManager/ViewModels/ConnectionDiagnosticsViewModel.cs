using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Dtos;
using ICCardManager.Services;

namespace ICCardManager.ViewModels
{
    /// <summary>
    /// 接続診断ダイアログの ViewModel（Issue #1690）
    /// </summary>
    /// <remarks>
    /// 判定と文言はすべて <see cref="IConnectionDiagnosticsService"/> 側にあり、
    /// 本 ViewModel は「実行 → 表示 → コピー」の導線だけを持つ。
    /// </remarks>
    public partial class ConnectionDiagnosticsViewModel : ViewModelBase
    {
        private readonly IConnectionDiagnosticsService _diagnosticsService;
        private readonly IClipboardService _clipboardService;

        public ConnectionDiagnosticsViewModel(
            IConnectionDiagnosticsService diagnosticsService,
            IClipboardService clipboardService)
        {
            _diagnosticsService = diagnosticsService;
            _clipboardService = clipboardService;
        }

        [ObservableProperty]
        private DiagnosticReport _report;

        [ObservableProperty]
        private ObservableCollection<DiagnosticItem> _items = new();

        [ObservableProperty]
        private DiagnosticItem _selectedItem;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        private bool _isStatusError;

        /// <summary>
        /// 診断結果が存在するか（コピーの可否判定に使用）
        /// </summary>
        public bool HasResult => Report != null;

        /// <summary>
        /// 診断日時の表示文字列
        /// </summary>
        public string DiagnosedAtText =>
            Report == null ? "未実行" : Report.DiagnosedAt.ToString("yyyy年M月d日 HH:mm:ss");

        /// <summary>
        /// 総合判定の見出し文言
        /// </summary>
        public string OverallSummaryText =>
            Report == null
                ? "「再診断」を押すと接続診断を実行します。"
                : DiagnosticStatusPresenter.GetOverallSummary(Report.OverallStatus);

        /// <summary>
        /// 総合判定のアイコン（色のみに依存しない状態伝達）
        /// </summary>
        public string OverallIcon =>
            Report == null ? "－" : DiagnosticStatusPresenter.GetIcon(Report.OverallStatus);

        /// <summary>
        /// 総合判定に対応する文字色のリソースキー名
        /// </summary>
        public string OverallForegroundResourceKey =>
            Report == null
                ? "SecondaryTextBrush"
                : DiagnosticStatusPresenter.GetForegroundResourceKey(Report.OverallStatus);

        /// <summary>
        /// 選択中の項目の詳細。未選択時は操作を促す文言を返す
        /// </summary>
        public string SelectedDetailText =>
            SelectedItem?.DetailText ?? "一覧から項目を選択すると、詳細と対処方法が表示されます。";

        /// <summary>
        /// 接続診断を実行する
        /// </summary>
        /// <remarks>
        /// DB の疎通確認やフォルダへの書込確認は同期 I/O で、共有モードでは SMB 越しに
        /// 最大 15 秒（busy_timeout）待つ可能性がある。UI スレッドを塞がないよう
        /// <see cref="Task.Run(System.Func{Task})"/> で退避する（接続テストと同じ方針）。
        /// </remarks>
        [RelayCommand]
        public async Task RunDiagnosticsAsync()
        {
            using (BeginBusy("接続診断を実行中..."))
            {
                var report = await Task.Run(() => _diagnosticsService.RunDiagnosticsAsync());

                Report = report;
                Items = new ObservableCollection<DiagnosticItem>(report?.Items ?? new System.Collections.Generic.List<DiagnosticItem>());

                // 対処が必要な項目があれば、それを初期選択にして詳細を即座に見せる。
                // 利用者が一覧を目で探して選び直す手間を省くのが狙い。
                SelectedItem = Items.FirstOrDefault(i => i.IsProblem) ?? Items.FirstOrDefault();

                SetStatus(BuildResultStatusMessage(report), report != null && report.ProblemCount > 0);
            }
        }

        /// <summary>
        /// 診断結果をクリップボードへコピーする
        /// </summary>
        [RelayCommand(CanExecute = nameof(HasResult))]
        public void CopyResult()
        {
            var text = DiagnosticReportFormatter.Format(Report);

            if (_clipboardService.TrySetText(text))
            {
                SetStatus("診断結果をコピーしました。メールなどに貼り付けてIT担当へ送ってください。", false);
                return;
            }

            SetStatus(
                "診断結果をコピーできませんでした。ほかのソフトがクリップボードを使用している可能性があります。" +
                "しばらく待ってからもう一度コピーしてください。",
                true);
        }

        partial void OnReportChanged(DiagnosticReport value)
        {
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(DiagnosedAtText));
            OnPropertyChanged(nameof(OverallSummaryText));
            OnPropertyChanged(nameof(OverallIcon));
            OnPropertyChanged(nameof(OverallForegroundResourceKey));
            CopyResultCommand.NotifyCanExecuteChanged();
        }

        partial void OnSelectedItemChanged(DiagnosticItem value)
        {
            OnPropertyChanged(nameof(SelectedDetailText));
        }

        private static string BuildResultStatusMessage(DiagnosticReport report)
        {
            if (report == null)
                return "診断結果を取得できませんでした。もう一度「再診断」を押してください。";

            return report.ProblemCount > 0
                ? $"対処が必要な項目が{report.ProblemCount}件あります。"
                : "すべての項目が正常です。";
        }

        private void SetStatus(string message, bool isError)
        {
            StatusMessage = message;
            IsStatusError = isError;
        }
    }
}
