using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Services;
using Microsoft.Win32;
using System.Threading.Tasks;

namespace ICCardManager.ViewModels;

/// <summary>
/// システム管理ViewModel（バックアップ/リストア/操作ログ）
/// </summary>
public partial class SystemManageViewModel : ViewModelBase
{
    private readonly BackupService _backupService;
    private readonly ISettingsRepository _settingsRepository;
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    private ObservableCollection<BackupFileInfo> _backupFiles = new();

    [ObservableProperty]
    private BackupFileInfo? _selectedBackup;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private string _lastBackupFile = string.Empty;

    /// <summary>
    /// 選択されたバックアップがあるか
    /// </summary>
    public bool HasSelectedBackup => SelectedBackup != null;

    public SystemManageViewModel(BackupService backupService, ISettingsRepository settingsRepository, INavigationService navigationService)
    {
        _backupService = backupService;
        _settingsRepository = settingsRepository;
        _navigationService = navigationService;
    }

    partial void OnSelectedBackupChanged(BackupFileInfo? value)
    {
        OnPropertyChanged(nameof(HasSelectedBackup));
        RestoreCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// バックアップ一覧を読み込む
    /// </summary>
    [RelayCommand]
    public async Task LoadBackupsAsync()
    {
        using (BeginBusy("バックアップ一覧を読み込み中..."))
        {
            try
            {
                var files = await _backupService.GetBackupFilesAsync();
                BackupFiles.Clear();
                foreach (var file in files)
                {
                    BackupFiles.Add(file);
                }

                if (BackupFiles.Count == 0)
                {
                    SetStatus("バックアップファイルが見つかりません", false);
                }
                else
                {
                    SetStatus($"{BackupFiles.Count}件のバックアップが見つかりました", false);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"バックアップ一覧の取得に失敗しました: {ex.Message}", true);
            }
        }
    }

    /// <summary>
    /// 手動バックアップを作成
    /// </summary>
    [RelayCommand]
    public async Task CreateBackupAsync()
    {
        // 自動バックアップと同じフォルダをデフォルトに設定
        var settings = await _settingsRepository.GetAppSettingsAsync();
        var defaultBackupFolder = !string.IsNullOrEmpty(settings.BackupPath)
            ? settings.BackupPath
            : PathValidator.GetDefaultBackupPath();

        var dialog = new SaveFileDialog
        {
            Filter = "データベースファイル (*.db)|*.db",
            DefaultExt = ".db",
            FileName = $"backup_manual_{DateTime.Now:yyyyMMdd_HHmmss}.db",
            Title = "バックアップファイルの保存先を選択",
            InitialDirectory = Directory.Exists(defaultBackupFolder) ? defaultBackupFolder : null
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        using (BeginBusy("バックアップを作成中..."))
        {
            try
            {
                var success = _backupService.CreateBackup(dialog.FileName);
                if (success)
                {
                    LastBackupFile = dialog.FileName;
                    SetStatus($"バックアップを作成しました: {Path.GetFileName(dialog.FileName)}", false);
                    // バックアップ一覧を更新
                    await LoadBackupsAsync();
                }
                else
                {
                    SetStatus("バックアップの作成に失敗しました", true);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"バックアップの作成に失敗しました: {ex.Message}", true);
            }
        }
    }

    /// <summary>
    /// 選択したバックアップからリストア
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    public async Task RestoreAsync()
    {
        if (SelectedBackup == null)
        {
            SetStatus("リストアするバックアップを選択してください", true);
            return;
        }

        // 確認ダイアログ
        // Issue #1108: 共有モード時は他PCの終了を促す警告を追加
        var sharedModeWarning = _backupService.IsSharedMode
            ? "【重要】共有モードで使用中のため、リストア前にすべてのPCでアプリケーションを終了してください。\n" +
              "他のPCが接続中の場合、リストアは実行できません。\n\n"
            : "";

        var result = MessageBox.Show(
            $"以下のバックアップからデータを復元します。\n\n" +
            $"ファイル: {SelectedBackup.FileName}\n" +
            $"作成日時: {DisplayFormatters.FormatTimestamp(SelectedBackup.CreatedAt)}\n\n" +
            sharedModeWarning +
            $"現在のデータは上書きされます。\n" +
            $"（復元前に現在のデータは自動バックアップされます）\n\n" +
            $"続行しますか？",
            "リストアの確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        bool restoreSuccess = false;

        using (BeginBusy("リストア中..."))
        {
            try
            {
                // リストア前バックアップの保存先を設定から取得
                var preRestoreBackupPath = await GetPreRestoreBackupPathAsync();

                // リストア前に現在のDBをバックアップ
                var backupSuccess = _backupService.CreateBackup(preRestoreBackupPath);
                if (!backupSuccess)
                {
                    // バックアップ失敗時はユーザーに確認
                    var continueResult = MessageBox.Show(
                        "現在のデータのバックアップに失敗しました。\n" +
                        "バックアップなしでリストアを続行しますか？",
                        "警告",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (continueResult != MessageBoxResult.Yes)
                    {
                        SetStatus("リストアをキャンセルしました", false);
                        return;
                    }
                }

                // リストア実行
                restoreSuccess = _backupService.RestoreFromBackup(SelectedBackup.FilePath);
                if (restoreSuccess)
                {
                    SetStatus("リストアが完了しました。アプリケーションを再起動してください。", false);
                }
                else
                {
                    // Issue #1108: 共有モード時は他PC接続が原因の可能性を示唆
                    var errorMessage = _backupService.IsSharedMode
                        ? "リストアに失敗しました。他のPCでアプリケーションが起動中の可能性があります。" +
                          "すべてのPCでアプリケーションを終了してから再度お試しください。"
                        : "リストアに失敗しました";
                    SetStatus(errorMessage, true);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"リストアに失敗しました: {ex.Message}", true);
            }
        }

        // プログレスバーを非表示にしてから再起動を促すダイアログを表示
        if (restoreSuccess)
        {
            MessageBox.Show(
                "リストアが完了しました。\n\n" +
                "変更を反映するには、アプリケーションを再起動してください。",
                "リストア完了",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private bool CanRestore() => SelectedBackup != null;

    /// <summary>
    /// バックアップフォルダを開く
    /// </summary>
    [RelayCommand]
    public void OpenBackupFolder()
    {
        if (BackupFiles.Count > 0)
        {
            var folder = Path.GetDirectoryName(BackupFiles[0].FilePath);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
        }
        else
        {
            SetStatus("バックアップフォルダが見つかりません", true);
        }
    }

    /// <summary>
    /// 外部バックアップファイルからリストア
    /// </summary>
    [RelayCommand]
    public async Task RestoreFromFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "データベースファイル (*.db)|*.db|すべてのファイル (*.*)|*.*",
            DefaultExt = ".db",
            Title = "リストアするバックアップファイルを選択"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // 確認ダイアログ
        // Issue #1108: 共有モード時は他PCの終了を促す警告を追加
        var sharedModeWarning2 = _backupService.IsSharedMode
            ? "【重要】共有モードで使用中のため、リストア前にすべてのPCでアプリケーションを終了してください。\n" +
              "他のPCが接続中の場合、リストアは実行できません。\n\n"
            : "";

        var result = MessageBox.Show(
            $"以下のファイルからデータを復元します。\n\n" +
            $"ファイル: {Path.GetFileName(dialog.FileName)}\n\n" +
            sharedModeWarning2 +
            $"現在のデータは上書きされます。\n" +
            $"（復元前に現在のデータは自動バックアップされます）\n\n" +
            $"続行しますか？",
            "リストアの確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        bool restoreFromFileSuccess = false;

        using (BeginBusy("リストア中..."))
        {
            try
            {
                // リストア前バックアップの保存先を設定から取得
                var preRestoreBackupPath = await GetPreRestoreBackupPathAsync();

                // リストア前に現在のDBをバックアップ
                var backupSuccess = _backupService.CreateBackup(preRestoreBackupPath);
                if (!backupSuccess)
                {
                    // バックアップ失敗時はユーザーに確認
                    var continueResult = MessageBox.Show(
                        "現在のデータのバックアップに失敗しました。\n" +
                        "バックアップなしでリストアを続行しますか？",
                        "警告",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (continueResult != MessageBoxResult.Yes)
                    {
                        SetStatus("リストアをキャンセルしました", false);
                        return;
                    }
                }

                // リストア実行
                restoreFromFileSuccess = _backupService.RestoreFromBackup(dialog.FileName);
                if (restoreFromFileSuccess)
                {
                    SetStatus("リストアが完了しました。アプリケーションを再起動してください。", false);
                }
                else
                {
                    // Issue #1108: 共有モード時は他PC接続が原因の可能性を示唆
                    var errorMessage2 = _backupService.IsSharedMode
                        ? "リストアに失敗しました。他のPCでアプリケーションが起動中の可能性があります。" +
                          "すべてのPCでアプリケーションを終了してから再度お試しください。"
                        : "リストアに失敗しました";
                    SetStatus(errorMessage2, true);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"リストアに失敗しました: {ex.Message}", true);
            }
        }

        // プログレスバーを非表示にしてから再起動を促すダイアログを表示
        if (restoreFromFileSuccess)
        {
            MessageBox.Show(
                "リストアが完了しました。\n\n" +
                "変更を反映するには、アプリケーションを再起動してください。",
                "リストア完了",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            // バックアップ一覧を更新
            await LoadBackupsAsync();
        }
    }

    /// <summary>
    /// リストア前バックアップの保存パスを取得
    /// 設定で指定されたバックアップフォルダを使用し、未設定の場合はデフォルトパスを使用
    /// </summary>
    private async Task<string> GetPreRestoreBackupPathAsync()
    {
        var settings = await _settingsRepository.GetAppSettingsAsync();
        var backupFolder = !string.IsNullOrEmpty(settings.BackupPath)
            ? settings.BackupPath
            : PathValidator.GetDefaultBackupPath();

        // バックアップフォルダが存在しない場合は作成
        if (!Directory.Exists(backupFolder))
        {
            Directory.CreateDirectory(backupFolder);
        }

        return Path.Combine(
            backupFolder,
            $"backup_pre_restore_{DateTime.Now:yyyyMMdd_HHmmss}.db");
    }

    /// <summary>
    /// ステータスメッセージを設定
    /// </summary>
    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }

    /// <summary>
    /// 操作ログダイアログを開く
    /// </summary>
    [RelayCommand]
    public void OpenOperationLog()
    {
        _navigationService.ShowDialog<Views.Dialogs.OperationLogDialog>();
    }
}
