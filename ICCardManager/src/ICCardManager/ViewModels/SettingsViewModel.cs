using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Win32;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;


namespace ICCardManager.ViewModels;

/// <summary>
/// 設定画面のViewModel
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsRepository _settingsRepository;
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
    private bool _isStatusError;

    [ObservableProperty]
    private bool _hasChanges;

    /// <summary>
    /// 保存が完了したかどうか
    /// </summary>
    [ObservableProperty]
    private bool _isSaved;

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

    /// <summary>
    /// トースト位置の選択肢
    /// </summary>
    public ObservableCollection<ToastPositionItem> ToastPositionOptions { get; } = new()
    {
        new ToastPositionItem { Value = ToastPosition.TopRight, DisplayName = "右上（デフォルト）" },
        new ToastPositionItem { Value = ToastPosition.TopLeft, DisplayName = "左上" },
        new ToastPositionItem { Value = ToastPosition.BottomRight, DisplayName = "右下" },
        new ToastPositionItem { Value = ToastPosition.BottomLeft, DisplayName = "左下" }
    };

    [ObservableProperty]
    private ToastPositionItem? _selectedToastPositionItem;

    /// <summary>
    /// 部署種別の選択肢
    /// </summary>
    public ObservableCollection<DepartmentTypeItem> DepartmentTypeOptions { get; } = new()
    {
        new DepartmentTypeItem { Value = DepartmentType.MayorOffice, DisplayName = "市長事務部局：役務費" },
        new DepartmentTypeItem { Value = DepartmentType.EnterpriseAccount, DisplayName = "企業会計部局（水道局、交通局等）：旅費" }
    };

    [ObservableProperty]
    private DepartmentTypeItem? _selectedDepartmentTypeItem;

    /// <summary>
    /// 返却時にバス停名入力ダイアログを自動スキップするかどうか
    /// </summary>
    [ObservableProperty]
    private bool _skipBusStopInputOnReturn;

    /// <summary>
    /// データベースの保存先パス
    /// </summary>
    [ObservableProperty]
    private string _databasePath = string.Empty;

    /// <summary>
    /// データベースパスが変更されたか（再起動が必要）
    /// </summary>
    [ObservableProperty]
    private bool _isDatabasePathChanged;

    private string _originalDatabasePath = string.Empty;

    public SettingsViewModel(
        ISettingsRepository settingsRepository,
        IValidationService validationService,
        ISoundPlayer soundPlayer,
        IOptions<DatabaseOptions> databaseOptions)
    {
        _settingsRepository = settingsRepository;
        _validationService = validationService;
        _soundPlayer = soundPlayer;
        _originalDatabasePath = databaseOptions.Value.Path ?? string.Empty;
        _databasePath = _originalDatabasePath;
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

            // ToastPositionItemを選択
            SelectedToastPositionItem = ToastPositionOptions.FirstOrDefault(x => x.Value == settings.ToastPosition)
                                        ?? ToastPositionOptions[0]; // デフォルトは「右上」

            // DepartmentTypeItemを選択
            SelectedDepartmentTypeItem = DepartmentTypeOptions.FirstOrDefault(x => x.Value == settings.DepartmentType)
                                          ?? DepartmentTypeOptions[0]; // デフォルトは「市長事務部局」

            // バス停入力スキップ設定
            SkipBusStopInputOnReturn = settings.SkipBusStopInputOnReturn;

            // DBパス設定（appsettings.jsonから読み込み済み）
            DatabasePath = _originalDatabasePath;
            IsDatabasePathChanged = false;

            HasChanges = false;
            SetStatus(string.Empty, false);
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
            SetStatus(balanceResult.ErrorMessage!, true);
            return;
        }

        // バックアップパスの検証（空の場合はデフォルトを使用するのでスキップ）
        var validatedBackupPath = BackupPath;
        if (!string.IsNullOrWhiteSpace(BackupPath))
        {
            var pathResult = PathValidator.ValidateBackupPath(BackupPath);
            if (!pathResult.IsValid)
            {
                SetStatus($"バックアップパスが無効です: {pathResult.ErrorMessage}", true);
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
                ToastPosition = SelectedToastPositionItem?.Value ?? ToastPosition.TopRight,
                DepartmentType = SelectedDepartmentTypeItem?.Value ?? DepartmentType.MayorOffice,
                SkipBusStopInputOnReturn = SkipBusStopInputOnReturn
            };

            var success = await _settingsRepository.SaveAppSettingsAsync(settings);

            if (success)
            {
                SetStatus("設定を保存しました", false);
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

                // トースト位置の変更を反映
                App.ApplyToastPosition(settings.ToastPosition);

                // DBパスが変更された場合、appsettings.jsonに保存
                if (IsDatabasePathChanged)
                {
                    // DBパスの検証（空の場合はデフォルトに戻すためスキップ）
                    var validatedDatabasePath = DatabasePath;
                    if (!string.IsNullOrWhiteSpace(DatabasePath))
                    {
                        var dbDir = Path.GetDirectoryName(DatabasePath);
                        if (string.IsNullOrEmpty(dbDir))
                        {
                            SetStatus("データベース保存先のパスが無効です", true);
                            return;
                        }

                        var dbPathResult = PathValidator.ValidateBackupPath(dbDir);
                        if (!dbPathResult.IsValid)
                        {
                            SetStatus($"データベース保存先が無効です: {dbPathResult.ErrorMessage}", true);
                            return;
                        }

                        // パスを正規化
                        var normalizedDir = PathValidator.NormalizePath(dbDir);
                        if (normalizedDir == null)
                        {
                            SetStatus("データベース保存先のパスを正規化できません", true);
                            return;
                        }
                        validatedDatabasePath = Path.Combine(normalizedDir, Data.DbContext.DatabaseFileName);
                    }

                    try
                    {
                        SaveDatabasePathToAppSettings(validatedDatabasePath);
                        DatabasePath = validatedDatabasePath;
                        SetStatus("設定を保存しました。データベース保存先の変更を反映するにはアプリケーションを再起動してください。", false);
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"データベース保存先の設定ファイル（appsettings.json）の保存に失敗しました: {ex.Message}", true);
                        return;
                    }
                }

                // 保存完了フラグを立てる（ダイアログを閉じるトリガー）
                IsSaved = true;
            }
            else
            {
                SetStatus("設定の保存に失敗しました", true);
            }
        }
    }

    /// <summary>
    /// バックアップフォルダを選択
    /// </summary>
    [RelayCommand]
    public void BrowseBackupPath()
    {
        // .NET Framework 4.8ではOpenFolderDialogがないためFolderBrowserDialogを使用
        using (var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "バックアップ先フォルダを選択",
            SelectedPath = string.IsNullOrEmpty(BackupPath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : BackupPath,
            ShowNewFolderButton = true
        })
        {
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                BackupPath = dialog.SelectedPath;
                HasChanges = true;
            }
        }
    }

    /// <summary>
    /// データベース保存先フォルダを選択
    /// </summary>
    [RelayCommand]
    public void BrowseDatabasePath()
    {
        using (var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "データベース保存先フォルダを選択（共有フォルダのUNCパスも使用可能）",
            SelectedPath = string.IsNullOrEmpty(DatabasePath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                : Path.GetDirectoryName(DatabasePath) ?? string.Empty,
            ShowNewFolderButton = true
        })
        {
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                DatabasePath = Path.Combine(dialog.SelectedPath, Data.DbContext.DatabaseFileName);
                HasChanges = true;
            }
        }
    }

    /// <summary>
    /// データベースパスをappsettings.jsonに保存
    /// </summary>
    private static void SaveDatabasePathToAppSettings(string databasePath)
    {
        var appSettingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

        var json = File.Exists(appSettingsPath)
            ? File.ReadAllText(appSettingsPath)
            : "{}";

        using var doc = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name == "DatabaseOptions")
                {
                    writer.WriteStartObject("DatabaseOptions");
                    writer.WriteString("Path", databasePath ?? string.Empty);
                    writer.WriteEndObject();
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            // DatabaseOptionsが存在しなかった場合
            if (!doc.RootElement.TryGetProperty("DatabaseOptions", out _))
            {
                writer.WriteStartObject("DatabaseOptions");
                writer.WriteString("Path", databasePath ?? string.Empty);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        // アトミック書き込み: 一時ファイルに書き出してからリネーム（破損防止）
        var tempPath = appSettingsPath + ".tmp";
        File.WriteAllBytes(tempPath, stream.ToArray());
        // .NET Framework 4.8ではFile.Moveにoverwrite引数がないため手動で置き換え
        if (File.Exists(appSettingsPath))
        {
            File.Delete(appSettingsPath);
        }
        File.Move(tempPath, appSettingsPath);
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

    partial void OnSelectedSoundModeItemChanged(SoundModeItem? value)
    {
        HasChanges = true;
    }

    partial void OnSelectedToastPositionItemChanged(ToastPositionItem? value)
    {
        HasChanges = true;
    }

    partial void OnSelectedDepartmentTypeItemChanged(DepartmentTypeItem? value)
    {
        HasChanges = true;
    }

    partial void OnSkipBusStopInputOnReturnChanged(bool value)
    {
        HasChanges = true;
    }

    partial void OnDatabasePathChanged(string value)
    {
        HasChanges = true;
        IsDatabasePathChanged = value != _originalDatabasePath;
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
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
/// 音声モード選択アイテム
/// </summary>
public class SoundModeItem
{
    public SoundMode Value { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// トースト位置選択アイテム
/// </summary>
public class ToastPositionItem
{
    public ToastPosition Value { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// 部署種別選択アイテム
/// </summary>
public class DepartmentTypeItem
{
    public DepartmentType Value { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}
