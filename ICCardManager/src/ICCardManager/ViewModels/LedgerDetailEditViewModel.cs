using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Models;

namespace ICCardManager.ViewModels
{
    /// <summary>
    /// 利用履歴詳細の追加/編集ダイアログ用ViewModel（Issue #635）
    /// </summary>
    public partial class LedgerDetailEditViewModel : ObservableObject
    {
        /// <summary>
        /// 追加モードかどうか（false=編集モード）
        /// </summary>
        [ObservableProperty]
        private bool _isInsertMode;

        /// <summary>
        /// ダイアログタイトル
        /// </summary>
        [ObservableProperty]
        private string _dialogTitle = "利用詳細の追加";

        /// <summary>
        /// 選択された利用種別
        /// </summary>
        [ObservableProperty]
        private UsageType _selectedUsageType = UsageType.Rail;

        /// <summary>
        /// 利用種別の選択肢
        /// </summary>
        public List<UsageTypeOption> UsageTypeOptions { get; } = new()
        {
            new UsageTypeOption(UsageType.Rail, "鉄道利用"),
            new UsageTypeOption(UsageType.Bus, "バス利用"),
            new UsageTypeOption(UsageType.Charge, "チャージ"),
            new UsageTypeOption(UsageType.PointRedemption, "ポイント還元"),
        };

        /// <summary>
        /// 利用日
        /// </summary>
        [ObservableProperty]
        private DateTime? _useDate;

        /// <summary>
        /// 利用時刻（時:分）
        /// </summary>
        [ObservableProperty]
        private string _useTimeText = string.Empty;

        /// <summary>
        /// 乗車駅
        /// </summary>
        [ObservableProperty]
        private string _entryStation = string.Empty;

        /// <summary>
        /// 降車駅
        /// </summary>
        [ObservableProperty]
        private string _exitStation = string.Empty;

        /// <summary>
        /// バス停名
        /// </summary>
        [ObservableProperty]
        private string _busStops = string.Empty;

        /// <summary>
        /// 金額（文字列でバリデーション用）
        /// </summary>
        [ObservableProperty]
        private string _amountText = string.Empty;

        /// <summary>
        /// 残高（文字列で表示）
        /// </summary>
        [ObservableProperty]
        private string _balanceText = string.Empty;

        /// <summary>
        /// 残高自動計算フラグ
        /// </summary>
        [ObservableProperty]
        private bool _autoCalculateBalance = true;

        /// <summary>
        /// 挿入位置（追加モード時）
        /// </summary>
        [ObservableProperty]
        private int _insertIndex;

        /// <summary>
        /// 挿入位置の説明テキスト
        /// </summary>
        [ObservableProperty]
        private string _insertPositionDescription = string.Empty;

        /// <summary>
        /// 確定フラグ（trueでダイアログ自動閉じ）
        /// </summary>
        [ObservableProperty]
        private bool _isCompleted;

        /// <summary>
        /// バリデーションエラーがあるかどうか
        /// </summary>
        [ObservableProperty]
        private bool _hasValidationError;

        /// <summary>
        /// バリデーションメッセージ
        /// </summary>
        [ObservableProperty]
        private string _validationMessage = string.Empty;

        /// <summary>
        /// 鉄道利用フィールドを表示するか
        /// </summary>
        [ObservableProperty]
        private bool _showRailFields = true;

        /// <summary>
        /// バス利用フィールドを表示するか
        /// </summary>
        [ObservableProperty]
        private bool _showBusFields;

        /// <summary>
        /// 確定結果のLedgerDetail
        /// </summary>
        public LedgerDetail? Result { get; private set; }

        /// <summary>
        /// 現在の全アイテムリスト（挿入位置計算用）
        /// </summary>
        private IList<LedgerDetailItemViewModel> _items = new List<LedgerDetailItemViewModel>();

        /// <summary>
        /// 編集対象のインデックス（編集モード時）
        /// </summary>
        private int _editTargetIndex = -1;

        /// <summary>
        /// 初期化中フラグ（プロパティ変更通知の連鎖を防止）
        /// </summary>
        private bool _isInitializing;

        /// <summary>
        /// 追加モードで初期化
        /// </summary>
        public void InitializeForInsert(IList<LedgerDetailItemViewModel> items, int suggestedIndex)
        {
            _items = items;
            _isInitializing = true;
            IsInsertMode = true;
            DialogTitle = "利用詳細の追加";
            UseDate = DateTime.Today;
            InsertIndex = Math.Max(0, Math.Min(suggestedIndex, items.Count));
            _isInitializing = false;
            UpdateInsertPositionDescription();
        }

        /// <summary>
        /// 編集モードで初期化
        /// </summary>
        public void InitializeForEdit(LedgerDetailItemViewModel editTarget, IList<LedgerDetailItemViewModel> items)
        {
            _items = items;
            _editTargetIndex = editTarget.Index;
            IsInsertMode = false;
            DialogTitle = "利用詳細の編集";

            var detail = editTarget.Detail;

            // 利用種別を判定
            if (detail.IsCharge)
                SelectedUsageType = UsageType.Charge;
            else if (detail.IsPointRedemption)
                SelectedUsageType = UsageType.PointRedemption;
            else if (detail.IsBus)
                SelectedUsageType = UsageType.Bus;
            else
                SelectedUsageType = UsageType.Rail;

            UseDate = detail.UseDate?.Date;
            UseTimeText = detail.UseDate?.ToString("HH:mm") ?? string.Empty;
            EntryStation = detail.EntryStation ?? string.Empty;
            ExitStation = detail.ExitStation ?? string.Empty;
            BusStops = detail.BusStops ?? string.Empty;
            AmountText = detail.Amount?.ToString() ?? string.Empty;
            BalanceText = detail.Balance?.ToString() ?? string.Empty;
            AutoCalculateBalance = false; // 編集時は既存値を表示
        }

        partial void OnSelectedUsageTypeChanged(UsageType value)
        {
            ShowRailFields = value == UsageType.Rail;
            ShowBusFields = value == UsageType.Bus;
            RecalculateBalance();
        }

        partial void OnUseDateChanged(DateTime? value)
        {
            if (_isInitializing) return;
            if (IsInsertMode && value.HasValue)
            {
                InsertIndex = SuggestInsertIndex(value.Value);
                UpdateInsertPositionDescription();
            }
        }

        partial void OnAmountTextChanged(string value)
        {
            RecalculateBalance();
        }

        partial void OnAutoCalculateBalanceChanged(bool value)
        {
            if (value)
            {
                RecalculateBalance();
            }
        }

        /// <summary>
        /// 日付から挿入位置を自動計算（use_date昇順で合致する位置）
        /// </summary>
        public int SuggestInsertIndex(DateTime useDate)
        {
            // 時刻テキストがあればそれを結合、なければ渡された日時をそのまま使用
            var fullDate = TryParseTime(UseTimeText, out var time) ? useDate.Date.Add(time) : useDate;

            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Detail.UseDate.HasValue && _items[i].Detail.UseDate > fullDate)
                {
                    return i;
                }
            }
            return _items.Count;
        }

        /// <summary>
        /// 残高を自動計算
        /// </summary>
        public void RecalculateBalance()
        {
            if (!AutoCalculateBalance) return;
            if (!int.TryParse(AmountText, out var amount) || amount < 0)
            {
                BalanceText = string.Empty;
                return;
            }

            int prevBalance = GetPreviousBalance();

            bool isIncome = SelectedUsageType == UsageType.Charge || SelectedUsageType == UsageType.PointRedemption;
            int newBalance = isIncome ? prevBalance + amount : prevBalance - amount;
            BalanceText = newBalance.ToString();
        }

        /// <summary>
        /// 前行の残高を取得
        /// </summary>
        private int GetPreviousBalance()
        {
            int targetIndex = IsInsertMode ? InsertIndex : _editTargetIndex;

            // 挿入位置の前の行を探す
            int prevIndex = targetIndex - 1;
            if (prevIndex >= 0 && prevIndex < _items.Count)
            {
                return _items[prevIndex].Detail.Balance ?? 0;
            }
            return 0;
        }

        /// <summary>
        /// 挿入位置を上に移動
        /// </summary>
        [RelayCommand]
        private void MoveInsertPositionUp()
        {
            if (InsertIndex > 0)
            {
                InsertIndex--;
                UpdateInsertPositionDescription();
                RecalculateBalance();
            }
        }

        /// <summary>
        /// 挿入位置を下に移動
        /// </summary>
        [RelayCommand]
        private void MoveInsertPositionDown()
        {
            if (InsertIndex < _items.Count)
            {
                InsertIndex++;
                UpdateInsertPositionDescription();
                RecalculateBalance();
            }
        }

        /// <summary>
        /// 挿入位置の説明テキストを更新
        /// </summary>
        private void UpdateInsertPositionDescription()
        {
            if (_items.Count == 0)
            {
                InsertPositionDescription = "先頭に挿入";
                return;
            }

            if (InsertIndex == 0)
            {
                InsertPositionDescription = "先頭に挿入";
            }
            else if (InsertIndex >= _items.Count)
            {
                InsertPositionDescription = $"「{_items[_items.Count - 1].RouteDisplay}」の後に挿入（末尾）";
            }
            else
            {
                InsertPositionDescription = $"「{_items[InsertIndex - 1].RouteDisplay}」の後に挿入";
            }
        }

        /// <summary>
        /// 確定コマンド
        /// </summary>
        [RelayCommand]
        private void Confirm()
        {
            if (!Validate()) return;

            Result = BuildLedgerDetail();
            IsCompleted = true;
        }

        /// <summary>
        /// バリデーション
        /// </summary>
        public bool Validate()
        {
            HasValidationError = false;
            ValidationMessage = string.Empty;

            if (!UseDate.HasValue)
            {
                HasValidationError = true;
                ValidationMessage = "利用日を入力してください。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(AmountText))
            {
                HasValidationError = true;
                ValidationMessage = "金額を入力してください。";
                return false;
            }

            if (!int.TryParse(AmountText, out var amount) || amount < 0)
            {
                HasValidationError = true;
                ValidationMessage = "金額は0以上の数値を入力してください。";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(BalanceText) && !int.TryParse(BalanceText, out _))
            {
                HasValidationError = true;
                ValidationMessage = "残高は数値を入力してください。";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(UseTimeText) && !TryParseTime(UseTimeText, out _))
            {
                HasValidationError = true;
                ValidationMessage = "時刻は HH:mm 形式で入力してください。";
                return false;
            }

            return true;
        }

        /// <summary>
        /// フォーム値からLedgerDetailを生成
        /// </summary>
        public LedgerDetail BuildLedgerDetail()
        {
            int.TryParse(AmountText, out var amount);
            int? balance = int.TryParse(BalanceText, out var b) ? b : null;

            var useDate = CombineDateTime(UseDate!.Value);

            return new LedgerDetail
            {
                UseDate = useDate,
                EntryStation = SelectedUsageType == UsageType.Rail ? EntryStation : string.Empty,
                ExitStation = SelectedUsageType == UsageType.Rail ? ExitStation : string.Empty,
                BusStops = SelectedUsageType == UsageType.Bus ? BusStops : string.Empty,
                Amount = amount,
                Balance = balance,
                IsCharge = SelectedUsageType == UsageType.Charge,
                IsPointRedemption = SelectedUsageType == UsageType.PointRedemption,
                IsBus = SelectedUsageType == UsageType.Bus,
                SequenceNumber = 0 // 手動入力はSequenceNumber=0
            };
        }

        /// <summary>
        /// 日付と時刻テキストを結合
        /// </summary>
        private DateTime CombineDateTime(DateTime date)
        {
            if (TryParseTime(UseTimeText, out var time))
            {
                return date.Date.Add(time);
            }
            return date.Date;
        }

        /// <summary>
        /// 時刻文字列をパース
        /// </summary>
        private static bool TryParseTime(string text, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            return TimeSpan.TryParse(text, out result);
        }
    }

    /// <summary>
    /// 利用種別のComboBox選択肢
    /// </summary>
    public class UsageTypeOption
    {
        public UsageType Value { get; }
        public string DisplayName { get; }

        public UsageTypeOption(UsageType value, string displayName)
        {
            Value = value;
            DisplayName = displayName;
        }
    }
}
