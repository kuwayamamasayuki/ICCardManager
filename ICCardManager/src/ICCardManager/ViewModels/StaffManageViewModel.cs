using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.Services;

namespace ICCardManager.ViewModels;

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
        IsWaitingForCard = true;

        // MainViewModelでの未登録カード処理を抑制
        App.IsStaffCardRegistrationActive = true;
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
                return;
            }

            var nameResult = _validationService.ValidateStaffName(sanitizedName);
            if (!nameResult)
            {
                StatusMessage = nameResult.ErrorMessage!;
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
                        StatusMessage = "この職員証は既に登録されています";
                        return;
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
                        await LoadStaffAsync();
                        CancelEdit();
                    }
                    else
                    {
                        StatusMessage = "登録に失敗しました";
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
                        await LoadStaffAsync();
                        CancelEdit();
                    }
                    else
                    {
                        StatusMessage = "更新に失敗しました";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"エラー: {ex.Message}";
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
                    await LoadStaffAsync();
                    CancelEdit();
                }
                else
                {
                    StatusMessage = "削除に失敗しました";
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"エラー: {ex.Message}";
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
    private void OnCardRead(object? sender, CardReadEventArgs e)
    {
        if (!IsWaitingForCard) return;

        // UIスレッドで実行
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            EditStaffIdm = e.Idm;
            StatusMessage = "職員証を読み取りました";
            IsWaitingForCard = false;

            // カード読み取り完了後、フラグを解除
            App.IsStaffCardRegistrationActive = false;
        });
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
            var newIdm = $"FFFF{Guid.NewGuid().ToString("N")[..12].ToUpper()}";
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
