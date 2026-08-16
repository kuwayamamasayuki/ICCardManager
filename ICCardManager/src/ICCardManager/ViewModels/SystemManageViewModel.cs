using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
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
    private readonly IStaffAuthService _staffAuthService;
    private readonly IBackupHealthService _backupHealthService;

    /// <summary>
    /// ダイアログ表示（Issue #1793 で <c>MessageBox.Show</c> 直呼びから移行）
    /// </summary>
    /// <remarks>
    /// 移行したのは <c>BeginBusy</c> スコープの内側から出す 2 か所（リストア前バックアップ失敗時の
    /// 続行確認）のみ。直呼びのままでは呼び出し時点の <c>IsBusy</c> をテストで捕捉できず、
    /// Issue #1793 の回帰を挙動テストで固定できないため。スコープ外の 4 か所は本 Issue の
    /// 対象ではないので触れていない（移行するなら別 Issue）。
    /// </remarks>
    private readonly IDialogService _dialogService;

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

    // --- バックアップ健全性（Issue #1689） ---
    // 「バックアップが正しく動き続けているか」を管理者がこの画面だけで判断できるようにする。
    // 表示文字列を ViewModel 側で組み立てるのは、XAML の StringFormat では
    // 「記録なし」「本日／昨日／N日前」のような条件分岐を表現できないため。

    [ObservableProperty]
    private BackupHealthInfo? _backupHealth;

    /// <summary>
    /// 最終バックアップ成功日時の表示テキスト
    /// </summary>
    public string LastBackupSuccessText
    {
        get
        {
            if (BackupHealth?.LastSuccessAt == null)
                return "最終成功: 記録なし（次回の自動バックアップ成功後に表示されます）";

            var elapsed = BackupHealth.GetDaysSinceLastSuccess(DateTime.Now) ?? 0;
            var elapsedText = elapsed == 0 ? "本日" : elapsed == 1 ? "昨日" : $"{elapsed}日前";
            return $"最終成功: {DisplayFormatters.FormatDateTime(BackupHealth.LastSuccessAt)}（{elapsedText}）";
        }
    }

    /// <summary>
    /// バックアップが長期間成功していない状態か（警告色・警告アイコンの切り替えに使用）
    /// </summary>
    public bool IsBackupStale
    {
        get
        {
            var elapsed = BackupHealth?.GetDaysSinceLastSuccess(DateTime.Now);
            return elapsed != null && elapsed > AppConstants.BackupStaleWarningDays;
        }
    }

    /// <summary>
    /// バックアップ健全性アイコン（色に依存せず状態を伝えるため、UI/UX原則に従いアイコンでも表現する）
    /// </summary>
    public string BackupHealthIcon => IsBackupStale ? "⚠" : "✔";

    /// <summary>
    /// 保持世代数の表示テキスト（例: 「保持世代: 12 / 30」）
    /// </summary>
    public string BackupGenerationText =>
        $"保持世代: {BackupHealth?.GenerationCount ?? 0} / {BackupHealth?.MaxGenerations ?? AppConstants.MaxBackupGenerations}";

    /// <summary>
    /// 保存先の空き容量の表示テキスト
    /// </summary>
    public string BackupFreeSpaceText =>
        $"保存先の空き容量: {DiskSpaceHelper.FormatBytes(BackupHealth?.FreeSpaceBytes)}";

    /// <summary>
    /// バックアップ保存先フォルダの表示テキスト
    /// </summary>
    public string BackupFolderText => $"保存先: {BackupHealth?.BackupFolderPath ?? "-"}";

    /// <summary>
    /// 共有モードでのみ表示する「最終実施PC」の表示テキスト（Issue #1689）。
    /// ローカルモードでは自PCしか実施し得ないため表示しない
    /// </summary>
    public string LastBackupMachineText =>
        $"最終実施PC: {(string.IsNullOrWhiteSpace(BackupHealth?.LastSuccessMachineName) ? "-" : BackupHealth!.LastSuccessMachineName)}";

    /// <summary>
    /// 共有モードでのみ表示する「最終VACUUM」の表示テキスト（Issue #1689）
    /// </summary>
    public string LastVacuumText
    {
        get
        {
            var date = BackupHealth?.LastVacuumDate;
            var machine = BackupHealth?.LastVacuumMachineName;
            if (date == null)
                return "最終最適化(VACUUM): 未実行";

            var machineText = string.IsNullOrWhiteSpace(machine) ? string.Empty : $"（実施PC: {machine}）";
            return $"最終最適化(VACUUM): {DisplayFormatters.FormatDate(date)}{machineText}";
        }
    }

    /// <summary>
    /// 共有モードかどうか（PC名関連の表示切り替えに使用）
    /// </summary>
    public bool IsSharedMode => _databaseInfo.IsSharedMode;

    partial void OnBackupHealthChanged(BackupHealthInfo? value)
    {
        OnPropertyChanged(nameof(LastBackupSuccessText));
        OnPropertyChanged(nameof(IsBackupStale));
        OnPropertyChanged(nameof(BackupHealthIcon));
        OnPropertyChanged(nameof(BackupGenerationText));
        OnPropertyChanged(nameof(BackupFreeSpaceText));
        OnPropertyChanged(nameof(BackupFolderText));
        OnPropertyChanged(nameof(LastBackupMachineText));
        OnPropertyChanged(nameof(LastVacuumText));
    }

    /// <summary>
    /// バックアップ健全性情報を読み込む（Issue #1689）
    /// </summary>
    /// <remarks>
    /// フォルダ走査と空き容量取得は同期I/Oで、共有モードでは SMB 越しになるため
    /// Task.Run でUIスレッドから退避する（接続テストと同じ方針）。
    /// </remarks>
    [RelayCommand]
    public async Task LoadBackupHealthAsync()
    {
        BackupHealth = await Task.Run(() => _backupHealthService.GetHealthAsync());
    }

    public SystemManageViewModel(
        BackupService backupService,
        ISettingsRepository settingsRepository,
        INavigationService navigationService,
        OperationLogger operationLogger,
        ISafeFileLauncher safeFileLauncher,
        IDatabaseInfo databaseInfo,
        IStaffAuthService staffAuthService,
        IBackupHealthService backupHealthService,
        IDialogService dialogService)
    {
        _backupService = backupService;
        _settingsRepository = settingsRepository;
        _navigationService = navigationService;
        _operationLogger = operationLogger;
        _safeFileLauncher = safeFileLauncher;
        _databaseInfo = databaseInfo;
        _staffAuthService = staffAuthService;
        _backupHealthService = backupHealthService;
        _dialogService = dialogService;
    }

    /// <summary>
    /// 接続診断ダイアログを開く（Issue #1690）
    /// </summary>
    /// <remarks>
    /// Issue #1686 の「接続をテスト」（DB の到達性・書込可否のみ）を置き換える。
    /// 到達性と書込権限は接続診断の項目 1・2 に内包されるため機能は後退せず、
    /// ICカードリーダー・バックアップ保存先・空き容量まで一度に確認できる。
    /// 似た2つのボタンが並ぶ混乱を避けるため、片方だけを残している。
    /// </remarks>
    [RelayCommand]
    public void OpenConnectionDiagnostics()
    {
        _navigationService.ShowDialog<Views.Dialogs.ConnectionDiagnosticsDialog>();
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

                // Issue #1689: 一覧と健全性表示（世代数・空き容量）は同じフォルダの状態を映すため、
                // 一覧を読み直すタイミングで必ず健全性も更新する（手動バックアップ直後もここを通る）。
                await LoadBackupHealthAsync();

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

        // Issue #1761: 一覧の選択（SelectedItem="{Binding SelectedBackup}" は TwoWay）を
        // 「操作対象の識別子」として await をまたいで参照しない。リストアするのは
        // **ボタンを押した時点で選択されていたファイル**であり、その後の選択状態には依存しない。
        // 処理中オーバーレイはマウス入力しか塞がないため、リストア前バックアップの作成中
        // （共有フォルダー上では数十秒かかり得る）にキーボード操作で選択が動くと、
        // 確認ダイアログで名指ししたファイルとは別のバックアップで DB を上書きし得た。
        // 選択が外れた場合は SelectedBackup.FilePath が NullReferenceException になる。
        var targetBackupPath = SelectedBackup.FilePath;
        var targetBackupFileName = SelectedBackup.FileName;
        var targetBackupCreatedAt = SelectedBackup.CreatedAt;

        // Issue #1705: DB リストアは全レコード（台帳・残高・職員・カード）を置換する破壊的操作のため、
        // 単一台帳行の削除（#635）と同様に職員認証を必須とする。認可の非対称性
        // （1 行削除には認証を課すのに DB 全体置換は無認証）を解消する。
        var authResult = await _staffAuthService.RequestAuthenticationAsync("データベースのリストア");
        if (authResult == null)
        {
            SetStatus("リストアには職員認証が必要です。認証がキャンセルされたため、リストアを中止しました。", false);
            return;
        }

        // 確認ダイアログ
        // Issue #1108: 共有モード時は他PCの終了を促す警告を追加
        var sharedModeWarning = _backupService.IsSharedMode
            ? "【重要】共有モードで使用中のため、リストア前にすべてのPCでアプリケーションを終了してください。\n" +
              "他のPCが接続中の場合、リストアは実行できません。\n\n"
            : "";

        // Issue #1793: リストア経路の入口の確認も IDialogService 経由にする。
        // MessageBox 直呼びのままだと、この確認で単体テストが実モーダルに入って止まり、
        // **スコープ内側の続行確認（本 Issue の対象）へ到達するテストが書けない**。
        var result = _dialogService.ShowWarningConfirmation(
            $"以下のバックアップからデータを復元します。\n\n" +
            $"ファイル: {targetBackupFileName}\n" +
            $"作成日時: {DisplayFormatters.FormatTimestamp(targetBackupCreatedAt)}\n\n" +
            sharedModeWarning +
            $"現在のデータは上書きされます。\n" +
            $"（復元前に現在のデータは自動バックアップされます）\n\n" +
            $"続行しますか？",
            "リストアの確認");

        if (!result)
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
                    //
                    // Issue #1793: この確認は BeginBusy("リストア中...") スコープの内側にある。
                    // スコープの前へ移すことはできない（直前の CreateBackupAsync の結果を見て
                    // 初めて必要性が決まる）ため、SuspendBusy で一時中断してから表示する。
                    // 中断しないと全面オーバーレイと「リストア中...」のプログレスバーが
                    // ダイアログの背後で回り続け、職員は 6 年保存の台帳 DB を上書きするか否かの
                    // 決定を「処理が続いているのか分からない」状態で迫られる。
                    bool continueWithoutBackup;
                    using (SuspendBusy())
                    {
                        continueWithoutBackup = _dialogService.ShowWarningConfirmation(
                            "現在のデータのバックアップに失敗しました。\n" +
                            "バックアップなしでリストアを続行しますか？",
                            "警告");
                    }
                    if (!continueWithoutBackup)
                    {
                        SetStatus("リストアをキャンセルしました", false);
                        return;
                    }
                }

                // リストア実行
                restoreSuccess = _backupService.RestoreFromBackup(targetBackupPath);
                if (restoreSuccess)
                {
                    SetStatus("リストアが完了しました。アプリケーションを再起動してください。", false);

                    // Issue #1302: 監査ログ記録 (リストア後の新DB上に痕跡を残す)
                    await _operationLogger.LogRestoreAsync(targetBackupPath);
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

        // Issue #1705: 外部ファイルからのリストアも DB 全体を置換する破壊的操作のため、
        // 選択バックアップからのリストアと同様に職員認証を必須とする。
        var authResult = await _staffAuthService.RequestAuthenticationAsync("データベースのリストア");
        if (authResult == null)
        {
            SetStatus("リストアには職員認証が必要です。認証がキャンセルされたため、リストアを中止しました。", false);
            return;
        }

        // 確認ダイアログ
        // Issue #1108: 共有モード時は他PCの終了を促す警告を追加
        var sharedModeWarning2 = _backupService.IsSharedMode
            ? "【重要】共有モードで使用中のため、リストア前にすべてのPCでアプリケーションを終了してください。\n" +
              "他のPCが接続中の場合、リストアは実行できません。\n\n"
            : "";

        // Issue #1793: リストア経路の入口の確認も IDialogService 経由にする。
        // MessageBox 直呼びのままだと、この確認で単体テストが実モーダルに入って止まり、
        // **スコープ内側の続行確認（本 Issue の対象）へ到達するテストが書けない**。
        var result = _dialogService.ShowWarningConfirmation(
            $"以下のファイルからデータを復元します。\n\n" +
            $"ファイル: {Path.GetFileName(dialog.FileName)}\n\n" +
            sharedModeWarning2 +
            $"現在のデータは上書きされます。\n" +
            $"（復元前に現在のデータは自動バックアップされます）\n\n" +
            $"続行しますか？",
            "リストアの確認");

        if (!result)
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
                    //
                    // Issue #1793: この確認は BeginBusy("リストア中...") スコープの内側にある。
                    // スコープの前へ移すことはできない（直前の CreateBackupAsync の結果を見て
                    // 初めて必要性が決まる）ため、SuspendBusy で一時中断してから表示する。
                    // 中断しないと全面オーバーレイと「リストア中...」のプログレスバーが
                    // ダイアログの背後で回り続け、職員は 6 年保存の台帳 DB を上書きするか否かの
                    // 決定を「処理が続いているのか分からない」状態で迫られる。
                    bool continueWithoutBackup;
                    using (SuspendBusy())
                    {
                        continueWithoutBackup = _dialogService.ShowWarningConfirmation(
                            "現在のデータのバックアップに失敗しました。\n" +
                            "バックアップなしでリストアを続行しますか？",
                            "警告");
                    }
                    if (!continueWithoutBackup)
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
