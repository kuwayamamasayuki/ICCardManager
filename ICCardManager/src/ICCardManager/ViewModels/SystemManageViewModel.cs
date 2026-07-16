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
    private readonly OperationLogger _operationLogger;
    private readonly ISafeFileLauncher _safeFileLauncher;
    private readonly IDatabaseInfo _databaseInfo;

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

    /// <summary>
    /// 現在使用中のデータベースファイルのパス（Issue #1686、常設表示用）
    /// </summary>
    public string DatabasePathDisplay => _databaseInfo.DatabasePath;

    /// <summary>
    /// データベースの動作モード表示（Issue #1686）。
    /// 共有フォルダモードか、このPC内のローカルモードかを常時表示する
    /// </summary>
    public string DatabaseModeText => _databaseInfo.IsSharedMode
        ? "共有モード（複数のPCでデータベースを共有しています）"
        : "ローカルモード（このPCの中に保存されています）";

    /// <summary>
    /// データベースの動作モードアイコン（Issue #1686）。
    /// ステータスバーの共有モードインジケーター（🔗）と同じ図像を使用し、色以外の手段でもモードを伝達する
    /// </summary>
    public string DatabaseModeIcon => _databaseInfo.IsSharedMode ? "🔗" : "💻";

    public SystemManageViewModel(
        BackupService backupService,
        ISettingsRepository settingsRepository,
        INavigationService navigationService,
        OperationLogger operationLogger,
        ISafeFileLauncher safeFileLauncher,
        IDatabaseInfo databaseInfo)
    {
        _backupService = backupService;
        _settingsRepository = settingsRepository;
        _navigationService = navigationService;
        _operationLogger = operationLogger;
        _safeFileLauncher = safeFileLauncher;
        _databaseInfo = databaseInfo;
    }

    /// <summary>
    /// データベースへの接続テストを実行する（Issue #1686）。
    /// 到達性（読み取り）と書込可否を順に確認し、結果をステータスメッセージへ表示する
    /// </summary>
    [RelayCommand]
    public async Task TestDatabaseConnectionAsync()
    {
        using (BeginBusy("データベース接続をテスト中..."))
        {
            // CheckConnection / CheckWritable は同期I/O（ネットワーク越しだと busy_timeout 最大15秒待つ）
            // のため、UIスレッドを塞がないよう Task.Run で退避する
            var canRead = await Task.Run(() => _databaseInfo.CheckConnection());
            if (!canRead)
            {
                SetStatus(
                    $"データベース（{DatabasePathDisplay}）に接続できません。" +
                    "ネットワークが切断されているか、共有フォルダにアクセスできない状態です。" +
                    "ネットワーク接続と共有フォルダの状態を確認してください。",
                    true);
                return;
            }

            var canWrite = await Task.Run(() => _databaseInfo.CheckWritable());
            if (!canWrite)
            {
                SetStatus(
                    $"データベース（{DatabasePathDisplay}）に接続できましたが、書き込みができません。" +
                    "ファイルまたは共有フォルダのアクセス権が読み取り専用になっている可能性があります。" +
                    "フォルダの「変更」権限があるか確認してください。",
                    true);
                return;
            }

            SetStatus("接続テストに成功しました。データベースへの読み取り・書き込みの両方が可能です。", false);
        }
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
    public Task LoadBackupsAsync() => LoadBackupsInternalAsync(announceCount: true);

    // Issue #1417: 件数を StatusMessage に書き戻すかを呼び出し側で制御できるよう分離。
    // バックアップ作成直後の呼び出しでは announceCount=false を指定し、
    // 直前に設定した完了メッセージ「バックアップを作成しました: ...」を上書きしないようにする。
    internal async Task LoadBackupsInternalAsync(bool announceCount)
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

                if (announceCount)
                {
                    if (BackupFiles.Count == 0)
                    {
                        SetStatus("バックアップファイルが見つかりません", false);
                    }
                    else
                    {
                        SetStatus($"{BackupFiles.Count}件のバックアップが見つかりました", false);
                    }
                }
            }
            catch (Exception ex)
            {
                // 技術的詳細はログへ。UI には 3 要素のユーザー向け文言を表示（Issue #1614）。
                ErrorDialogHelper.LogException(ex, "バックアップ一覧の取得");
                SetStatus(ExceptionMessageFormatter.ToUserMessage(ex, "バックアップ一覧の取得"), true);
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

        await CreateBackupCoreAsync(dialog.FileName);
    }

    // Issue #1417: SaveFileDialog はテスト不能 (UI スレッド要求) のため、
    // バックアップ本体処理を internal メソッドに抽出してテスト可能化する。
    internal async Task CreateBackupCoreAsync(string backupFilePath)
    {
        using (BeginBusy("バックアップを作成中..."))
        {
            try
            {
                // Issue #1361: UI スレッドから sync 呼び出しは LeaseConnection の UI スレッドガード (#1281) に抵触するため、
                // Task.Run で委譲する CreateBackupAsync を使用する
                var success = await _backupService.CreateBackupAsync(backupFilePath);
                if (success)
                {
                    LastBackupFile = backupFilePath;
                    SetStatus($"バックアップを作成しました: {Path.GetFileName(backupFilePath)}", false);

                    // Issue #1302: 監査ログ記録
                    await _operationLogger.LogBackupAsync(backupFilePath);

                    // Issue #1417: バックアップ一覧を更新するが、件数表示で完了メッセージを上書きしない
                    await LoadBackupsInternalAsync(announceCount: false);
                }
                else
                {
                    SetStatus("バックアップの作成に失敗しました。保存先の空き容量や書き込み権限を確認してから再度実行してください。", true);
                }
            }
            catch (Exception ex)
            {
                // 技術的詳細はログへ。UI には 3 要素のユーザー向け文言を表示（Issue #1614）。
                ErrorDialogHelper.LogException(ex, "バックアップの作成");
                SetStatus(ExceptionMessageFormatter.ToUserMessage(ex, "バックアップの作成"), true);
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
                // Issue #1361: UI スレッドから sync 呼び出しは LeaseConnection の UI スレッドガード (#1281) に抵触するため、
                // Task.Run で委譲する CreateBackupAsync を使用する
                var backupSuccess = await _backupService.CreateBackupAsync(preRestoreBackupPath);
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

                    // Issue #1302: 監査ログ記録 (リストア後の新DB上に痕跡を残す)
                    await _operationLogger.LogRestoreAsync(SelectedBackup.FilePath);
                }
                else
                {
                    // Issue #1108: 共有モード時は他PC接続が原因の可能性を示唆
                    var errorMessage = _backupService.IsSharedMode
                        ? "リストアに失敗しました。他のPCでアプリケーションが起動中の可能性があります。" +
                          "すべてのPCでアプリケーションを終了してから再度お試しください。"
                        : "リストアに失敗しました。バックアップファイルが破損しているか、データベースが使用中の可能性があります。" +
                          "別のバックアップファイルを選ぶか、アプリケーションを再起動してから再度お試しください。";
                    SetStatus(errorMessage, true);
                }
            }
            catch (Exception ex)
            {
                // 技術的詳細はログへ。UI には 3 要素のユーザー向け文言を表示（Issue #1614）。
                ErrorDialogHelper.LogException(ex, "リストア");
                SetStatus(ExceptionMessageFormatter.ToUserMessage(ex, "リストア"), true);
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
        if (BackupFiles.Count == 0)
        {
            SetStatus(
                "バックアップが 1 件もないため、フォルダを特定できません。" +
                "「バックアップを作成」を実行してからお試しください。",
                true);
            return;
        }

        // Issue #1465: ISafeFileLauncher 経由で explorer.exe を直接起動
        var folder = Path.GetDirectoryName(BackupFiles[0].FilePath);
        var result = _safeFileLauncher.LaunchFolder(folder ?? string.Empty);
        if (!result.Success)
        {
            SetStatus(result.ErrorMessage, true);
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
                // Issue #1361: UI スレッドから sync 呼び出しは LeaseConnection の UI スレッドガード (#1281) に抵触するため、
                // Task.Run で委譲する CreateBackupAsync を使用する
                var backupSuccess = await _backupService.CreateBackupAsync(preRestoreBackupPath);
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

                    // Issue #1302: 監査ログ記録 (リストア後の新DB上に痕跡を残す)
                    await _operationLogger.LogRestoreAsync(dialog.FileName);
                }
                else
                {
                    // Issue #1108: 共有モード時は他PC接続が原因の可能性を示唆
                    var errorMessage2 = _backupService.IsSharedMode
                        ? "リストアに失敗しました。他のPCでアプリケーションが起動中の可能性があります。" +
                          "すべてのPCでアプリケーションを終了してから再度お試しください。"
                        : "リストアに失敗しました。バックアップファイルが破損しているか、データベースが使用中の可能性があります。" +
                          "別のバックアップファイルを選ぶか、アプリケーションを再起動してから再度お試しください。";
                    SetStatus(errorMessage2, true);
                }
            }
            catch (Exception ex)
            {
                // 技術的詳細はログへ。UI には 3 要素のユーザー向け文言を表示（Issue #1614）。
                ErrorDialogHelper.LogException(ex, "リストア");
                SetStatus(ExceptionMessageFormatter.ToUserMessage(ex, "リストア"), true);
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
