using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Data.Repositories;
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
    private readonly IValidationService _validationService;

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

    public SettingsViewModel(
        ISettingsRepository settingsRepository,
        IValidationService validationService)
    {
        _settingsRepository = settingsRepository;
        _validationService = validationService;
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

            HasChanges = false;
            StatusMessage = string.Empty;
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

        using (BeginBusy("保存中..."))
        {
            var settings = new AppSettings
            {
                WarningBalance = WarningBalance,
                BackupPath = BackupPath,
                FontSize = SelectedFontSizeItem?.Value ?? FontSizeOption.Medium
            };

            var success = await _settingsRepository.SaveAppSettingsAsync(settings);

            if (success)
            {
                StatusMessage = "設定を保存しました";
                HasChanges = false;

                // 文字サイズの変更を反映
                ApplyFontSize(settings.FontSize);
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
