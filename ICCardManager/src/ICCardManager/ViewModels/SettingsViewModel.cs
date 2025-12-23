using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Win32;

namespace ICCardManager.ViewModels;

/// <summary>
/// 設定画面のViewModel
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly IStaffRepository _staffRepository;
    private readonly IValidationService _validationService;
    private readonly ISoundPlayer _soundPlayer;

    [ObservableProperty]
    private int _warningBalance;

    [ObservableProperty]
    private string _backupPath = string.Empty;

    [ObservableProperty]
    private FontSizeOption _selectedFontSize;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasChanges;

    /// <summary>
    /// 保存が完了したかどうか
    /// </summary>
    [ObservableProperty]
    private bool _isSaved;

    /// <summary>
    /// 職員証タッチをスキップするかどうか
    /// </summary>
    [ObservableProperty]
    private bool _skipStaffTouch;

    /// <summary>
    /// 職員一覧（デフォルト職員選択用）
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<StaffItem> _staffList = new();

    /// <summary>
    /// 選択されたデフォルト職員
    /// </summary>
    [ObservableProperty]
    private StaffItem? _selectedDefaultStaff;

    /// <summary>
    /// 文字サイズの選択肢
    /// </summary>
    public ObservableCollection<FontSizeItem> FontSizeOptions { get; } = new()
    {
        new FontSizeItem { Value = FontSizeOption.Small, DisplayName = "小", BaseFontSize = 12 },
        new FontSizeItem { Value = FontSizeOption.Medium, DisplayName = "中（標準）", BaseFontSize = 14 },
        new FontSizeItem { Value = FontSizeOption.Large, DisplayName = "大", BaseFontSize = 16 },
        new FontSizeItem { Value = FontSizeOption.ExtraLarge, DisplayName = "特大", BaseFontSize = 20 }
    };

    [ObservableProperty]
    private FontSizeItem? _selectedFontSizeItem;

    /// <summary>
    /// 音声モードの選択肢
    /// </summary>
    public ObservableCollection<SoundModeItem> SoundModeOptions { get; } = new()
    {
        new SoundModeItem { Value = SoundMode.Beep, DisplayName = "効果音（ピッ/ピピッ）" },
        new SoundModeItem { Value = SoundMode.VoiceMale, DisplayName = "音声（男性）" },
        new SoundModeItem { Value = SoundMode.VoiceFemale, DisplayName = "音声（女性）" },
        new SoundModeItem { Value = SoundMode.None, DisplayName = "無し" }
    };

    [ObservableProperty]
    private SoundModeItem? _selectedSoundModeItem;

    public SettingsViewModel(
        ISettingsRepository settingsRepository,
        IStaffRepository staffRepository,
        IValidationService validationService,
        ISoundPlayer soundPlayer)
    {
        _settingsRepository = settingsRepository;
        _staffRepository = staffRepository;
        _validationService = validationService;
        _soundPlayer = soundPlayer;
    }

    /// <summary>
    /// 初期化
    /// </summary>
    public async Task InitializeAsync()
    {
        await LoadSettingsAsync();
    }

    /// <summary>
    /// 設定を読み込み
    /// </summary>
    [RelayCommand]
    public async Task LoadSettingsAsync()
    {
        using (BeginBusy("読み込み中..."))
        {
            var settings = await _settingsRepository.GetAppSettingsAsync();

            WarningBalance = settings.WarningBalance;
            BackupPath = settings.BackupPath;
            SelectedFontSize = settings.FontSize;

            // FontSizeItemを選択
            SelectedFontSizeItem = FontSizeOptions.FirstOrDefault(x => x.Value == settings.FontSize)
                                   ?? FontSizeOptions[1]; // デフォルトは「中」

            // SoundModeItemを選択
            SelectedSoundModeItem = SoundModeOptions.FirstOrDefault(x => x.Value == settings.SoundMode)
                                    ?? SoundModeOptions[0]; // デフォルトは「効果音」

            // 職員一覧を読み込み
            await LoadStaffListAsync();

            // 職員証スキップ設定を読み込み
            SkipStaffTouch = settings.SkipStaffTouch;
            if (!string.IsNullOrEmpty(settings.DefaultStaffIdm))
            {
                SelectedDefaultStaff = StaffList.FirstOrDefault(s => s.StaffIdm == settings.DefaultStaffIdm);
            }

            HasChanges = false;
            StatusMessage = string.Empty;
        }
    }

    /// <summary>
    /// 職員一覧を読み込み
    /// </summary>
    private async Task LoadStaffListAsync()
    {
        var staffMembers = await _staffRepository.GetAllAsync();
        StaffList.Clear();
        foreach (var staff in staffMembers.OrderBy(s => s.Name))
        {
            StaffList.Add(new StaffItem
            {
                StaffIdm = staff.StaffIdm,
                Name = staff.Name
            });
        }
    }

    /// <summary>
    /// 設定を保存
    /// </summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        // バリデーション
        var balanceResult = _validationService.ValidateWarningBalance(WarningBalance);
        if (!balanceResult)
        {
            StatusMessage = balanceResult.ErrorMessage!;
            return;
        }

        // 職員証スキップ時はデフォルト職員が必須
        if (SkipStaffTouch && SelectedDefaultStaff == null)
        {
            StatusMessage = "職員証スキップを有効にするには、デフォルト職員を選択してください";
            return;
        }

        // バックアップパスの検証（空の場合はデフォルトを使用するのでスキップ）
        var validatedBackupPath = BackupPath;
        if (!string.IsNullOrWhiteSpace(BackupPath))
        {
            var pathResult = PathValidator.ValidateBackupPath(BackupPath);
            if (!pathResult.IsValid)
            {
                StatusMessage = $"バックアップパスが無効です: {pathResult.ErrorMessage}";
                return;
            }
            // パスを正規化
            validatedBackupPath = PathValidator.NormalizePath(BackupPath) ?? BackupPath;
        }

        using (BeginBusy("保存中..."))
        {
            var settings = new AppSettings
            {
                WarningBalance = WarningBalance,
                BackupPath = validatedBackupPath,
                FontSize = SelectedFontSizeItem?.Value ?? FontSizeOption.Medium,
                SoundMode = SelectedSoundModeItem?.Value ?? SoundMode.Beep,
                SkipStaffTouch = SkipStaffTouch,
                DefaultStaffIdm = SelectedDefaultStaff?.StaffIdm
            };

            var success = await _settingsRepository.SaveAppSettingsAsync(settings);

            if (success)
            {
                StatusMessage = "設定を保存しました";
                HasChanges = false;

                // 正規化されたパスをUIに反映
                if (BackupPath != validatedBackupPath)
                {
                    BackupPath = validatedBackupPath;
                }

                // 文字サイズの変更を反映
                ApplyFontSize(settings.FontSize);

                // 音声モードの変更を反映
                _soundPlayer.SoundMode = settings.SoundMode;

                // 保存完了フラグを立てる（ダイアログを閉じるトリガー）
                IsSaved = true;
            }
            else
            {
                StatusMessage = "設定の保存に失敗しました";
            }
        }
    }

    /// <summary>
    /// バックアップフォルダを選択
    /// </summary>
    [RelayCommand]
    public void BrowseBackupPath()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "バックアップ先フォルダを選択",
            InitialDirectory = string.IsNullOrEmpty(BackupPath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : BackupPath
        };

        if (dialog.ShowDialog() == true)
        {
            BackupPath = dialog.FolderName;
            HasChanges = true;
        }
    }

    /// <summary>
    /// 文字サイズをアプリケーションに適用
    /// </summary>
    private void ApplyFontSize(FontSizeOption fontSize)
    {
        // App.ApplyFontSizeを呼び出して、関連するすべてのフォントサイズを更新
        App.ApplyFontSize(fontSize);
    }

    partial void OnWarningBalanceChanged(int value)
    {
        HasChanges = true;
    }

    partial void OnBackupPathChanged(string value)
    {
        HasChanges = true;
    }

    partial void OnSelectedFontSizeItemChanged(FontSizeItem? value)
    {
        if (value != null)
        {
            SelectedFontSize = value.Value;
            HasChanges = true;
        }
    }

    partial void OnSkipStaffTouchChanged(bool value)
    {
        HasChanges = true;
    }

    partial void OnSelectedDefaultStaffChanged(StaffItem? value)
    {
        HasChanges = true;
    }

    partial void OnSelectedSoundModeItemChanged(SoundModeItem? value)
    {
        HasChanges = true;
    }
}

/// <summary>
/// 文字サイズ選択アイテム
/// </summary>
public class FontSizeItem
{
    public FontSizeOption Value { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public double BaseFontSize { get; set; }
}

/// <summary>
/// 職員選択アイテム
/// </summary>
public class StaffItem
{
    public string StaffIdm { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// 音声モード選択アイテム
/// </summary>
public class SoundModeItem
{
    public SoundMode Value { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}
