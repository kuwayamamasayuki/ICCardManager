using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Services;
using Microsoft.Win32;
using System.Threading.Tasks;

namespace ICCardManager.ViewModels;

/// <summary>
/// ファイルサイズを人間が読みやすい形式に変換するコンバーター
/// </summary>
public class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// システム管理ViewModel（バックアップ/リストア）
/// </summary>
public partial class SystemManageViewModel : ViewModelBase
{
    private readonly BackupService _backupService;

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

    public SystemManageViewModel(BackupService backupService)
    {
        _backupService = backupService;
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
        var dialog = new SaveFileDialog
        {
            Filter = "データベースファイル (*.db)|*.db",
            DefaultExt = ".db",
            FileName = $"backup_manual_{DateTime.Now:yyyyMMdd_HHmmss}.db",
            Title = "バックアップファイルの保存先を選択"
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
        var result = MessageBox.Show(
            $"以下のバックアップからデータを復元します。\n\n" +
            $"ファイル: {SelectedBackup.FileName}\n" +
            $"作成日時: {SelectedBackup.CreatedAt:yyyy/MM/dd HH:mm:ss}\n\n" +
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

        using (BeginBusy("リストア中..."))
        {
            try
            {
                // リストア前に現在のDBをバックアップ
                var preRestoreBackupPath = Path.Combine(
                    Path.GetDirectoryName(SelectedBackup.FilePath) ?? "",
                    $"backup_pre_restore_{DateTime.Now:yyyyMMdd_HHmmss}.db");

                _backupService.CreateBackup(preRestoreBackupPath);

                // リストア実行
                var success = _backupService.RestoreFromBackup(SelectedBackup.FilePath);
                if (success)
                {
                    SetStatus("リストアが完了しました。アプリケーションを再起動してください。", false);

                    // 再起動を促すダイアログ
                    MessageBox.Show(
                        "リストアが完了しました。\n\n" +
                        "変更を反映するには、アプリケーションを再起動してください。",
                        "リストア完了",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    SetStatus("リストアに失敗しました", true);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"リストアに失敗しました: {ex.Message}", true);
            }
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
        var result = MessageBox.Show(
            $"以下のファイルからデータを復元します。\n\n" +
            $"ファイル: {Path.GetFileName(dialog.FileName)}\n\n" +
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

        using (BeginBusy("リストア中..."))
        {
            try
            {
                // リストア前に現在のDBをバックアップ
                var folder = Path.GetDirectoryName(dialog.FileName) ?? "";
                var preRestoreBackupPath = Path.Combine(
                    folder,
                    $"backup_pre_restore_{DateTime.Now:yyyyMMdd_HHmmss}.db");

                _backupService.CreateBackup(preRestoreBackupPath);

                // リストア実行
                var success = _backupService.RestoreFromBackup(dialog.FileName);
                if (success)
                {
                    SetStatus("リストアが完了しました。アプリケーションを再起動してください。", false);

                    // 再起動を促すダイアログ
                    MessageBox.Show(
                        "リストアが完了しました。\n\n" +
                        "変更を反映するには、アプリケーションを再起動してください。",
                        "リストア完了",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    // バックアップ一覧を更新
                    await LoadBackupsAsync();
                }
                else
                {
                    SetStatus("リストアに失敗しました", true);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"リストアに失敗しました: {ex.Message}", true);
            }
        }
    }

    /// <summary>
    /// ステータスメッセージを設定
    /// </summary>
    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }
}
