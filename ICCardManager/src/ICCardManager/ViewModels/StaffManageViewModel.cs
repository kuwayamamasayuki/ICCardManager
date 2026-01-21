using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.Services;

namespace ICCardManager.ViewModels
{
/// <summary>
    /// 職員管理画面のViewModel
    /// </summary>
    public partial class StaffManageViewModel : ViewModelBase
    {
        private readonly IStaffRepository _staffRepository;
        private readonly ICardReader _cardReader;
        private readonly IValidationService _validationService;

        [ObservableProperty]
        private ObservableCollection<StaffDto> _staffList = new();

        [ObservableProperty]
        private StaffDto? _selectedStaff;

        [ObservableProperty]
        private bool _isEditing;

        [ObservableProperty]
        private bool _isNewStaff;

        [ObservableProperty]
        private string _editStaffIdm = string.Empty;

        [ObservableProperty]
        private string _editName = string.Empty;

        [ObservableProperty]
        private string _editNumber = string.Empty;

        [ObservableProperty]
        private string _editNote = string.Empty;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        private bool _isStatusError;

        [ObservableProperty]
        private bool _isWaitingForCard;

        public StaffManageViewModel(
            IStaffRepository staffRepository,
            ICardReader cardReader,
            IValidationService validationService)
        {
            _staffRepository = staffRepository;
            _cardReader = cardReader;
            _validationService = validationService;

            // カード読み取りイベント
            _cardReader.CardRead += OnCardRead;
        }

        /// <summary>
        /// 初期化
        /// </summary>
        public async Task InitializeAsync()
        {
            await LoadStaffAsync();
        }

        /// <summary>
        /// 職員一覧を読み込み
        /// </summary>
        [RelayCommand]
        public async Task LoadStaffAsync()
        {
            using (BeginBusy("読み込み中..."))
            {
                var staffList = await _staffRepository.GetAllAsync();
                StaffList.Clear();
                foreach (var staff in staffList.OrderBy(s => s.Number).ThenBy(s => s.Name))
                {
                    StaffList.Add(staff.ToDto());
                }
            }
        }

        /// <summary>
        /// 新規登録モードを開始
        /// </summary>
        [RelayCommand]
        public void StartNewStaff()
        {
            SelectedStaff = null;
            IsEditing = true;
            IsNewStaff = true;
            EditStaffIdm = string.Empty;
            EditName = string.Empty;
            EditNumber = string.Empty;
            EditNote = string.Empty;
            StatusMessage = "職員証をタッチするとIDmを読み取ります";
            IsStatusError = false;
            IsWaitingForCard = true;

            // MainViewModelでの未登録カード処理を抑制
            App.IsStaffCardRegistrationActive = true;
        }

        /// <summary>
        /// IDmを指定して新規登録モードを開始（未登録カード検出時用）
        /// </summary>
        /// <param name="idm">職員証のIDm</param>
        public void StartNewStaffWithIdm(string idm)
        {
            SelectedStaff = null;
            IsEditing = true;
            IsNewStaff = true;
            EditStaffIdm = idm;
            EditName = string.Empty;
            EditNumber = string.Empty;
            EditNote = string.Empty;
            StatusMessage = "職員証を読み取りました。氏名を入力してください。";
            IsStatusError = false;
            IsWaitingForCard = false; // すでにIDmがあるので待機しない
        }

        /// <summary>
        /// 編集モードを開始
        /// </summary>
        [RelayCommand]
        public void StartEdit()
        {
            if (SelectedStaff == null) return;

            IsEditing = true;
            IsNewStaff = false;
            EditStaffIdm = SelectedStaff.StaffIdm;
            EditName = SelectedStaff.Name;
            EditNumber = SelectedStaff.Number ?? string.Empty;
            EditNote = SelectedStaff.Note ?? string.Empty;
            StatusMessage = string.Empty;
            IsStatusError = false;
            IsWaitingForCard = false;
        }

        /// <summary>
        /// 保存
        /// </summary>
        [RelayCommand]
        public async Task SaveAsync()
        {
            try
            {
                // 入力値をサニタイズ
                var sanitizedName = InputSanitizer.SanitizeName(EditName);
                var sanitizedNumber = InputSanitizer.SanitizeStaffNumber(EditNumber);
                var sanitizedNote = InputSanitizer.SanitizeNote(EditNote);

                // バリデーション
                var idmResult = _validationService.ValidateStaffIdm(EditStaffIdm);
                if (!idmResult)
                {
                    StatusMessage = idmResult.ErrorMessage!;
                    IsStatusError = true;
                    return;
                }

                var nameResult = _validationService.ValidateStaffName(sanitizedName);
                if (!nameResult)
                {
                    StatusMessage = nameResult.ErrorMessage!;
                    IsStatusError = true;
                    return;
                }

                using (BeginBusy("保存中..."))
                {
                    if (IsNewStaff)
                    {
                        // 重複チェック
                        var existing = await _staffRepository.GetByIdmAsync(EditStaffIdm, includeDeleted: true);
                        if (existing != null)
                        {
                            var identifier = string.IsNullOrWhiteSpace(existing.Number)
                                ? existing.Name
                                : $"{existing.Name}（{existing.Number}）";

                            if (existing.IsDeleted)
                            {
                                // 削除済み職員の場合は復元を提案
                                var result = MessageBox.Show(
                                    $"この職員証は以前 {identifier} として登録されていましたが、削除されています。\n\n復元しますか？",
                                    "削除済み職員",
                                    MessageBoxButton.YesNo,
                                    MessageBoxImage.Question);

                                if (result == MessageBoxResult.Yes)
                                {
                                    var restored = await _staffRepository.RestoreAsync(EditStaffIdm);
                                    if (restored)
                                    {
                                        StatusMessage = $"{identifier} を復元しました";
                                        IsStatusError = false;
                                        await LoadStaffAsync();
                                        CancelEdit();
                                    }
                                    else
                                    {
                                        StatusMessage = "復元に失敗しました";
                                        IsStatusError = true;
                                    }
                                }
                                else
                                {
                                    // Issue #314: 復元しない場合は案内メッセージを表示
                                    MessageBox.Show(
                                        $"この職員証は以前 {identifier} として登録されていたため、新規登録はできません。\n\n" +
                                        "異なる名前等で登録したい場合は、先に復元を行い、その後に編集してください。",
                                        "ご案内",
                                        MessageBoxButton.OK,
                                        MessageBoxImage.Information);
                                    CancelEdit();
                                }
                                return;
                            }
                            else
                            {
                                StatusMessage = $"この職員証は {identifier} として既に登録されています";
                                IsStatusError = true;
                                return;
                            }
                        }

                        var staff = new Staff
                        {
                            StaffIdm = EditStaffIdm,
                            Name = sanitizedName,
                            Number = string.IsNullOrWhiteSpace(sanitizedNumber) ? null : sanitizedNumber,
                            Note = string.IsNullOrWhiteSpace(sanitizedNote) ? null : sanitizedNote
                        };

                        var success = await _staffRepository.InsertAsync(staff);
                        if (success)
                        {
                            StatusMessage = "登録しました";
                            IsStatusError = false;
                            await LoadStaffAsync();
                            CancelEdit();
                        }
                        else
                        {
                            StatusMessage = "登録に失敗しました";
                            IsStatusError = true;
                        }
                    }
                    else
                    {
                        // 更新
                        var staff = new Staff
                        {
                            StaffIdm = EditStaffIdm,
                            Name = sanitizedName,
                            Number = string.IsNullOrWhiteSpace(sanitizedNumber) ? null : sanitizedNumber,
                            Note = string.IsNullOrWhiteSpace(sanitizedNote) ? null : sanitizedNote
                        };

                        var success = await _staffRepository.UpdateAsync(staff);
                        if (success)
                        {
                            StatusMessage = "更新しました";
                            IsStatusError = false;
                            await LoadStaffAsync();
                            CancelEdit();
                        }
                        else
                        {
                            StatusMessage = "更新に失敗しました";
                            IsStatusError = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"エラー: {ex.Message}";
                IsStatusError = true;
                System.Diagnostics.Debug.WriteLine($"[StaffManageViewModel] SaveAsync エラー: {ex}");
            }
        }

        /// <summary>
        /// 削除
        /// </summary>
        [RelayCommand]
        public async Task DeleteAsync()
        {
            if (SelectedStaff == null) return;

            try
            {
                using (BeginBusy("削除中..."))
                {
                    var success = await _staffRepository.DeleteAsync(SelectedStaff.StaffIdm);
                    if (success)
                    {
                        StatusMessage = "削除しました";
                        IsStatusError = false;
                        await LoadStaffAsync();
                        CancelEdit();
                    }
                    else
                    {
                        StatusMessage = "削除に失敗しました";
                        IsStatusError = true;
                    }
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"エラー: {ex.Message}";
                IsStatusError = true;
                System.Diagnostics.Debug.WriteLine($"[StaffManageViewModel] DeleteAsync エラー: {ex}");
            }
        }

        /// <summary>
        /// 編集をキャンセル
        /// </summary>
        [RelayCommand]
        public void CancelEdit()
        {
            IsEditing = false;
            IsNewStaff = false;
            IsWaitingForCard = false;
            EditStaffIdm = string.Empty;
            EditName = string.Empty;
            EditNumber = string.Empty;
            EditNote = string.Empty;
            StatusMessage = string.Empty;

            // 職員証登録モードを解除
            App.IsStaffCardRegistrationActive = false;
        }

        /// <summary>
        /// カード読み取りイベント
        /// </summary>
        private void OnCardRead(object sender, CardReadEventArgs e)
        {
            if (!IsWaitingForCard) return;

            // UIスレッドで非同期実行（登録済みチェックを即座に行うため）
            System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                EditStaffIdm = e.Idm;
                IsWaitingForCard = false;

                // 即座に登録済みチェックを実行（Issue #284）
                var existing = await _staffRepository.GetByIdmAsync(e.Idm, includeDeleted: true);
                if (existing != null)
                {
                    var identifier = string.IsNullOrWhiteSpace(existing.Number)
                        ? existing.Name
                        : $"{existing.Name}（{existing.Number}）";

                    if (existing.IsDeleted)
                    {
                        // 削除済み職員の場合は復元を提案
                        var result = MessageBox.Show(
                            $"この職員証は以前 {identifier} として登録されていましたが、削除されています。\n\n復元しますか？",
                            "削除済み職員",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                        if (result == MessageBoxResult.Yes)
                        {
                            var restored = await _staffRepository.RestoreAsync(e.Idm);
                            if (restored)
                            {
                                StatusMessage = $"{identifier} を復元しました";
                                IsStatusError = false;
                                await LoadStaffAsync();
                                CancelEdit();
                            }
                            else
                            {
                                StatusMessage = "復元に失敗しました";
                                IsStatusError = true;
                            }
                        }
                        else
                        {
                            // Issue #314: 復元しない場合は案内メッセージを表示
                            MessageBox.Show(
                                $"この職員証は以前 {identifier} として登録されていたため、新規登録はできません。\n\n" +
                                "異なる名前等で登録したい場合は、先に復元を行い、その後に編集してください。",
                                "ご案内",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                            CancelEdit();
                        }
                    }
                    else
                    {
                        // 既に登録済みの場合はメッセージを表示（赤色で目立たせる: Issue #286）
                        StatusMessage = $"この職員証は {identifier} として既に登録されています";
                        IsStatusError = true;
                        // フォームはそのままにして、ユーザーが確認できるようにする
                    }

                    // カード読み取り完了後、フラグを解除
                    App.IsStaffCardRegistrationActive = false;
                    return;
                }

                // 未登録職員証の場合は通常処理
                StatusMessage = "職員証を読み取りました";
                IsStatusError = false;

                // カード読み取り完了後、フラグを解除
                App.IsStaffCardRegistrationActive = false;
            });
        }

        /// <summary>
        /// 選択職員変更時の処理
        /// </summary>
        partial void OnSelectedStaffChanged(StaffDto? value)
        {
            // 新規登録モード中は選択変更を無視
            if (IsNewStaff) return;

            // 職員が選択された場合、編集中なら選択した職員の情報で更新
            if (value != null && IsEditing)
            {
                EditStaffIdm = value.StaffIdm;
                EditName = value.Name;
                EditNumber = value.Number ?? string.Empty;
                EditNote = value.Note ?? string.Empty;
                StatusMessage = string.Empty;
                IsStatusError = false;
            }
        }

        /// <summary>
        /// デバッグ用: カード読み取りをシミュレート
        /// </summary>
        [RelayCommand]
        public void SimulateCardRead()
        {
            if (!IsWaitingForCard) return;

            if (_cardReader is MockCardReader mockReader)
            {
                // 未使用のIDmを生成（職員証はFFFFで始まる）
                var newIdm = $"FFFF{Guid.NewGuid().ToString("N").Substring(0, 12).ToUpper()}";
                mockReader.SimulateCardRead(newIdm);
            }
        }

        /// <summary>
        /// クリーンアップ
        /// </summary>
        public void Cleanup()
        {
            _cardReader.CardRead -= OnCardRead;

            // ダイアログ終了時にフラグを解除
            App.IsStaffCardRegistrationActive = false;
        }
    }
}
