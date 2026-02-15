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
        private readonly OperationLogger _operationLogger;
        private readonly IDialogService _dialogService;
        private readonly IStaffAuthService _staffAuthService;

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

        /// <summary>
        /// 新規登録・更新・復元後にハイライト表示する職員のIDm
        /// </summary>
        [ObservableProperty]
        private string? _newlyRegisteredIdm;

        public StaffManageViewModel(
            IStaffRepository staffRepository,
            ICardReader cardReader,
            IValidationService validationService,
            OperationLogger operationLogger,
            IDialogService dialogService,
            IStaffAuthService staffAuthService)
        {
            _staffRepository = staffRepository;
            _cardReader = cardReader;
            _validationService = validationService;
            _operationLogger = operationLogger;
            _dialogService = dialogService;
            _staffAuthService = staffAuthService;

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
        /// <returns>処理が完了したかどうか（削除済み職員の復元で完了した場合はtrue）</returns>
        public async Task<bool> StartNewStaffWithIdmAsync(string idm)
        {
            // Issue #284対応: タッチ時点で削除済み職員チェックを行う
            var existing = await _staffRepository.GetByIdmAsync(idm, includeDeleted: true);
            if (existing != null)
            {
                // 識別子を決定（名前優先、なければ職員番号）
                var identifier = !string.IsNullOrEmpty(existing.Name) ? existing.Name : existing.Number;

                if (existing.IsDeleted)
                {
                    // 削除済み職員の場合は復元を提案
                    var confirmed = _dialogService.ShowConfirmation(
                        $"この職員証は以前 {identifier} として登録されていましたが、削除されています。\n\n復元しますか？",
                        "削除済み職員");

                    if (confirmed)
                    {
                        var restored = await _staffRepository.RestoreAsync(idm);
                        if (restored)
                        {
                            // 操作ログを記録（復元後のデータを取得）
                            var restoredStaff = await _staffRepository.GetByIdmAsync(idm);
                            if (restoredStaff != null)
                            {
                                await _operationLogger.LogStaffRestoreAsync(null, restoredStaff);
                            }

                            _dialogService.ShowInformation(
                                $"{identifier} を復元しました",
                                "復元完了");
                            return true; // ダイアログを閉じる
                        }
                        else
                        {
                            _dialogService.ShowError(
                                "復元に失敗しました",
                                "エラー");
                            return true; // ダイアログを閉じる
                        }
                    }
                    else
                    {
                        // Issue #314: 復元しない場合は案内メッセージを表示
                        _dialogService.ShowInformation(
                            $"この職員証は以前 {identifier} として登録されていたため、新規登録はできません。\n\n" +
                            "異なる情報で登録したい場合は、先に復元を行い、その後に編集してください。",
                            "ご案内");
                        return true; // ダイアログを閉じる
                    }
                }
                else
                {
                    // 既に登録済みの場合はメッセージを表示して終了
                    _dialogService.ShowInformation(
                        $"この職員証は {identifier} として既に登録されています",
                        "登録済み職員証");
                    return true; // ダイアログを閉じる
                }
            }

            // 未登録職員証の場合は通常処理
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

            return false; // ダイアログは開いたまま
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
                                var confirmed = _dialogService.ShowConfirmation(
                                    $"この職員証は以前 {identifier} として登録されていましたが、削除されています。\n\n復元しますか？",
                                    "削除済み職員");

                                if (confirmed)
                                {
                                    var restored = await _staffRepository.RestoreAsync(EditStaffIdm);
                                    if (restored)
                                    {
                                        // 操作ログを記録（復元後のデータを取得）
                                        var restoredStaff = await _staffRepository.GetByIdmAsync(EditStaffIdm);
                                        if (restoredStaff != null)
                                        {
                                            await _operationLogger.LogStaffRestoreAsync(null, restoredStaff);
                                        }

                                        var restoredIdm = EditStaffIdm;
                                        StatusMessage = $"{identifier} を復元しました";
                                        IsStatusError = false;
                                        await LoadStaffAsync();
                                        CancelEdit();
                                        SelectAndHighlight(restoredIdm);
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
                                    _dialogService.ShowInformation(
                                        $"この職員証は以前 {identifier} として登録されていたため、新規登録はできません。\n\n" +
                                        "異なる名前等で登録したい場合は、先に復元を行い、その後に編集してください。",
                                        "ご案内");
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
                            // 操作ログを記録
                            await _operationLogger.LogStaffInsertAsync(null, staff);

                            var savedIdm = EditStaffIdm;
                            StatusMessage = "登録しました";
                            IsStatusError = false;
                            await LoadStaffAsync();
                            CancelEdit();
                            SelectAndHighlight(savedIdm);
                        }
                        else
                        {
                            StatusMessage = "登録に失敗しました";
                            IsStatusError = true;
                        }
                    }
                    else
                    {
                        // 更新前のデータを取得（操作ログ用）
                        var beforeStaff = await _staffRepository.GetByIdmAsync(EditStaffIdm);

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
                            // 操作ログを記録
                            if (beforeStaff != null)
                            {
                                await _operationLogger.LogStaffUpdateAsync(null, beforeStaff, staff);
                            }

                            var updatedIdm = EditStaffIdm;
                            StatusMessage = "更新しました";
                            IsStatusError = false;
                            await LoadStaffAsync();
                            CancelEdit();
                            SelectAndHighlight(updatedIdm);
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
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[StaffManageViewModel] SaveAsync エラー: {ex}");
#endif
            }
        }

        /// <summary>
        /// 削除
        /// </summary>
        [RelayCommand]
        public async Task DeleteAsync()
        {
            if (SelectedStaff == null) return;

            // Issue #429: 職員の削除は認証が必要
            var authResult = await _staffAuthService.RequestAuthenticationAsync("職員の削除");
            if (authResult == null)
            {
                // 認証キャンセルまたはタイムアウト
                return;
            }

            try
            {
                using (BeginBusy("削除中..."))
                {
                    // 削除前のデータを取得（操作ログ用）
                    var staff = await _staffRepository.GetByIdmAsync(SelectedStaff.StaffIdm);

                    var success = await _staffRepository.DeleteAsync(SelectedStaff.StaffIdm);
                    if (success)
                    {
                        // 操作ログを記録（Issue #429: 認証済み職員のIDmを使用）
                        if (staff != null)
                        {
                            await _operationLogger.LogStaffDeleteAsync(authResult.Idm, staff);
                        }

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
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[StaffManageViewModel] DeleteAsync エラー: {ex}");
#endif
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
                        var confirmed = _dialogService.ShowConfirmation(
                            $"この職員証は以前 {identifier} として登録されていましたが、削除されています。\n\n復元しますか？",
                            "削除済み職員");

                        if (confirmed)
                        {
                            var restored = await _staffRepository.RestoreAsync(e.Idm);
                            if (restored)
                            {
                                // 操作ログを記録（復元後のデータを取得）
                                var restoredStaff = await _staffRepository.GetByIdmAsync(e.Idm);
                                if (restoredStaff != null)
                                {
                                    await _operationLogger.LogStaffRestoreAsync(null, restoredStaff);
                                }

                                var restoredIdm = e.Idm;
                                StatusMessage = $"{identifier} を復元しました";
                                IsStatusError = false;
                                await LoadStaffAsync();
                                CancelEdit();
                                SelectAndHighlight(restoredIdm);
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
                            _dialogService.ShowInformation(
                                $"この職員証は以前 {identifier} として登録されていたため、新規登録はできません。\n\n" +
                                "異なる名前等で登録したい場合は、先に復元を行い、その後に編集してください。",
                                "ご案内");
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

#if DEBUG
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
#endif

        /// <summary>
        /// 保存・更新・復元後にハイライト対象のIDmを設定する
        /// </summary>
        /// <remarks>
        /// View層がNewlyRegisteredIdmの変更を監視し、該当行のスクロール＋ハイライトを行う。
        /// 選択行の背景色と競合しないよう、SelectedStaffは設定しない（View層で選択解除する）。
        /// </remarks>
        /// <param name="idm">ハイライト対象の職員IDm</param>
        private void SelectAndHighlight(string idm)
        {
            // 同じIDmの連続操作でもPropertyChangedが発火するようリセット
            NewlyRegisteredIdm = null;
            NewlyRegisteredIdm = idm;
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
