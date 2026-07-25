using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ICCardManager.Services;

namespace ICCardManager.ViewModels
{
    /// <summary>
    /// 帳票出力前プリフライトチェック結果ダイアログのViewModel（Issue #1688）
    /// </summary>
    /// <remarks>
    /// 2つのモードを持つ。
    /// 確認モード（<see cref="IsConfirmationMode"/> = true）は「作成」押下時の自動チェック経由で、
    /// 「中止して修正する」／「このまま作成する」を選ばせる。
    /// 参照モード（false）は「事前チェック」ボタン経由で、閉じるだけの表示専用。
    /// </remarks>
    public partial class ReportPreflightViewModel : ViewModelBase
    {
        [ObservableProperty]
        private ObservableCollection<ReportPreflightWarning> _warnings = new();

        [ObservableProperty]
        private ReportPreflightWarning _selectedWarning;

        /// <summary>
        /// 確認モード（作成フロー経由）かどうか。false の場合は表示専用の参照モード。
        /// </summary>
        [ObservableProperty]
        private bool _isConfirmationMode;

        /// <summary>
        /// チェック対象の年月を示す見出し（例: "2026年7月分"）
        /// </summary>
        [ObservableProperty]
        private string _targetPeriodText = string.Empty;

        /// <summary>
        /// 警告が1件以上あるか
        /// </summary>
        public bool HasWarnings => Warnings.Count > 0;

        /// <summary>
        /// 警告が0件か（「問題は見つかりませんでした」の表示制御に使用）
        /// </summary>
        public bool HasNoWarnings => Warnings.Count == 0;

        /// <summary>
        /// 警告件数の要約（例: "3件の警告が見つかりました（2枚のカード）"）
        /// </summary>
        public string SummaryText
        {
            get
            {
                if (Warnings.Count == 0)
                {
                    return "問題は見つかりませんでした。このまま帳票を作成できます。";
                }

                var cardCount = Warnings.Select(w => w.CardIdm).Distinct().Count();
                return $"{Warnings.Count}件の警告が見つかりました（対象カード {cardCount}枚）。";
            }
        }

        /// <summary>
        /// 選択中の警告の詳細説明。未選択時は操作を促す文言を返す。
        /// </summary>
        public string SelectedDetailText =>
            SelectedWarning?.DetailText ?? "一覧から警告を選択すると、詳細と対処方法が表示されます。";

        /// <summary>
        /// チェック結果を設定する
        /// </summary>
        /// <param name="result">プリフライトチェック結果</param>
        /// <param name="year">対象年</param>
        /// <param name="month">対象月</param>
        /// <param name="isConfirmationMode">確認モード（作成フロー経由）かどうか</param>
        public void SetResult(ReportPreflightResult result, int year, int month, bool isConfirmationMode)
        {
            var items = result?.Warnings ?? new List<ReportPreflightWarning>();

            // カードごとにまとまるよう、カード名 → 日付 → 種別の順で並べ替える
            Warnings = new ObservableCollection<ReportPreflightWarning>(
                items.OrderBy(w => w.CardDisplayName)
                     .ThenBy(w => w.Date ?? System.DateTime.MaxValue)
                     .ThenBy(w => w.IssueType));

            IsConfirmationMode = isConfirmationMode;
            TargetPeriodText = $"{year}年{month}月分";
            SelectedWarning = null;

            OnPropertyChanged(nameof(HasWarnings));
            OnPropertyChanged(nameof(HasNoWarnings));
            OnPropertyChanged(nameof(SummaryText));
        }

        partial void OnSelectedWarningChanged(ReportPreflightWarning value)
        {
            OnPropertyChanged(nameof(SelectedDetailText));
        }
    }
}
