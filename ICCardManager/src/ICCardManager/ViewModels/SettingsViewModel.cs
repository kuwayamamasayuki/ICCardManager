#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Options;
using Microsoft.Win32;


namespace ICCardManager.ViewModels;

/// <summary>
/// 設定画面のViewModel
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly IValidationService _validationService;
    private readonly ISoundPlayer _soundPlayer;
    private readonly IDialogService _dialogService;

    /// <summary>
    /// 職員証の認証（Issue #2264: バックアップ先・データベース保存先の変更で求める）
    /// </summary>
    private readonly IStaffAuthService _staffAuthService;

    /// <summary>
    /// バックアップ先・データベース保存先の変更と操作ログ（Issue #2264）
    /// </summary>
    private readonly ISettingsChangeService _settingsChangeService;

    /// <summary>
    /// 摘要生成器（DI シングルトン、Issue #1975）
    /// </summary>
    /// <remarks>
    /// 部署種別は起動時に捕捉されたまま固定されるため、保存成功時に
    /// <see cref="SummaryGenerator.ApplyDepartmentType"/> で差し替えないと、
    /// 履歴統合・分割・返却・明細編集の摘要再生成が再起動まで旧設定で動く。
    /// 省略可能にすると DI の配線漏れが「設定した部署種別が静かに無視される」形で
    /// 潜在化するため必須引数にする（development-conventions.md #1820）。
    /// </remarks>
    private readonly SummaryGenerator _summaryGenerator;

    /// <summary>
    /// この画面が読み書きする設定ファイル（<c>database_config.txt</c> 等）の置き場所（Issue #2098）。
    /// </summary>
    /// <remarks>
    /// 本番では <see cref="AppDataPaths.RootDirectory"/>（<c>C:\ProgramData\ICCardManager</c>）。
    /// テストはテストごとの一時フォルダーを注入する。起動処理・帳票の出力先が読む既定の置き場所
    /// （<see cref="AppConfigFiles.DatabaseConfigPath"/> 等）は常に <see cref="AppDataPaths.RootDirectory"/> を見る。
    /// </remarks>
    private readonly string _configDirectory;

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
    /// Issue #1279: 最初にエラーが発生したフィールドのプロパティ名。
    /// Dialog のコードビハインドがこの値を参照し、該当コントロールへフォーカスを移動する。
    /// エラーなしの場合は null。
    /// </summary>
    [ObservableProperty]
    private string? _firstErrorField;

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
    /// 返却時に同行者数入力ダイアログを自動スキップするかどうか（Issue #1906）
    /// </summary>
    [ObservableProperty]
    private bool _skipCompanionCountInputOnReturn = true; // Issue #2178: モデル（AppSettings）・読み込みの既定とそろえる

    /// <summary>
    /// 返却時の同行者数入力を「外0名」として自動的に閉じるまでの秒数（Issue #2009）
    /// </summary>
    /// <remarks>
    /// 0 は「自動的に閉じない（必ず尋ねる）」。複数名での利用が既定の部署はこちらを選ぶ。
    /// </remarks>
    [ObservableProperty]
    private int _companionCountInputTimeoutSeconds;

    /// <summary>
    /// 長期未返却とみなす日数（Issue #2152）
    /// </summary>
    /// <remarks>
    /// 管理者ダッシュボードの初期値になる。ダッシュボード上の切り替えは一時的な絞り込みで、この値は変わらない。
    /// </remarks>
    [ObservableProperty]
    private int _longTermUnreturnedDays = AppConstants.LongTermUnreturnedDays;

    /// <summary>
    /// 返却時に利用履歴を自動表示して確認を促すかどうか（Issue #1907）
    /// </summary>
    [ObservableProperty]
    private bool _showHistoryOnReturn;

    /// <summary>
    /// データベースの保存先フォルダパス（UI表示用。ファイル名は内部で自動付与）
    /// </summary>
    [ObservableProperty]
    private string _databasePath = string.Empty;

    /// <summary>
    /// データベースパスが変更されたか（再起動が必要）
    /// </summary>
    [ObservableProperty]
    private bool _isDatabasePathChanged;

    private string _originalDatabasePath = string.Empty;

    /// <summary>
    /// 読み込んだ時点（または最後に変更した時点）のバックアップ先（Issue #2264）
    /// </summary>
    /// <remarks>
    /// 入力欄の値がこれと異なるときだけ「バックアップ先を変更する」とみなし、職員証の認証を求める。
    /// 正規化した値ではなく入力欄の生の値で比べる（触っていないバックアップ先が、正規化の差で変更扱いにならないように）。
    /// </remarks>
    private string _loadedBackupPath = string.Empty;

    public SettingsViewModel(
        ISettingsRepository settingsRepository,
        IValidationService validationService,
        ISoundPlayer soundPlayer,
        IOptions<DatabaseOptions> databaseOptions, // DI互換のため引数を維持（本体では未使用）
        IDialogService dialogService,
        SummaryGenerator summaryGenerator,
        IStaffAuthService staffAuthService,
        ISettingsChangeService settingsChangeService)
        : this(
            settingsRepository,
            validationService,
            soundPlayer,
            dialogService,
            summaryGenerator,
            staffAuthService,
            settingsChangeService,
            AppDataPaths.RootDirectory)
    {
    }

    /// <summary>
    /// 設定ファイルの置き場所を指定して生成する（Issue #2098）。
    /// </summary>
    /// <param name="settingsRepository">設定リポジトリ</param>
    /// <param name="validationService">入力検証サービス</param>
    /// <param name="soundPlayer">効果音・音声の再生</param>
    /// <param name="dialogService">確認ダイアログの表示</param>
    /// <param name="summaryGenerator">摘要生成器（部署種別の反映先。Issue #1975）</param>
    /// <param name="staffAuthService">職員証の認証（バックアップ先・データベース保存先の変更で求める。Issue #2264）</param>
    /// <param name="settingsChangeService">バックアップ先・データベース保存先の変更と操作ログ（Issue #2264）</param>
    /// <param name="configDirectory">
    /// <c>database_config.txt</c> / <c>department_config.txt</c> を読み書きするフォルダー。
    /// テストはテストごとの一時フォルダーを渡し、開発機の本物の設定ファイルや、
    /// 並列に走る他のテストの設定ファイルに触れないようにする。
    /// </param>
    /// <remarks>
    /// DI コンテナは public コンストラクタだけを候補にするため、本コンストラクタは選ばれない。
    /// </remarks>
    internal SettingsViewModel(
        ISettingsRepository settingsRepository,
        IValidationService validationService,
        ISoundPlayer soundPlayer,
        IDialogService dialogService,
        SummaryGenerator summaryGenerator,
        IStaffAuthService staffAuthService,
        ISettingsChangeService settingsChangeService,
        string configDirectory)
    {
        _settingsRepository = settingsRepository;
        _validationService = validationService;
        _soundPlayer = soundPlayer;
        _dialogService = dialogService;
        _summaryGenerator = summaryGenerator;
        // 既定値を持たない必須引数にする（省略可能にすると DI の配線漏れが「認証なしで保存できる」形で潜在化する。#1820）
        _staffAuthService = staffAuthService ?? throw new ArgumentNullException(nameof(staffAuthService));
        _settingsChangeService = settingsChangeService ?? throw new ArgumentNullException(nameof(settingsChangeService));
        _configDirectory = configDirectory;
        // database_config.txtから直接読む（設定ファイルが正。DI経由のDatabaseOptionsは
        // アプリ起動時に固定されるため、同一セッション中に設定変更しても反映されない）
        var fullPath = AppConfigFiles.Read(DatabaseConfigPath);
        _originalDatabasePath = ExtractDirectoryPath(fullPath);
        _databasePath = _originalDatabasePath;
    }

    /// <summary>
    /// ファイルパスからフォルダパスを抽出（空文字・フォルダパスはそのまま返す）
    /// </summary>
    private static string ExtractDirectoryPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        // 既にフォルダパス（ファイル名なし）の場合はそのまま
        if (!Path.HasExtension(path))
        {
            return path;
        }

        return Path.GetDirectoryName(path) ?? string.Empty;
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
            _loadedBackupPath = settings.BackupPath ?? string.Empty;
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
            SkipCompanionCountInputOnReturn = settings.SkipCompanionCountInputOnReturn;
            CompanionCountInputTimeoutSeconds = settings.CompanionCountInputTimeoutSeconds;
            LongTermUnreturnedDays = settings.LongTermUnreturnedDays;
            ShowHistoryOnReturn = settings.ShowHistoryOnReturn;

            // DBフォルダパス設定（appsettings.jsonから読み込み済み）
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
        // Issue #1279: 検証前にエラーフィールド情報をリセット
        FirstErrorField = null;

        // バリデーション
        var balanceResult = _validationService.ValidateWarningBalance(WarningBalance);
        if (!balanceResult)
        {
            SetStatus(balanceResult.ErrorMessage!, true);
            // Issue #1279: 検証失敗フィールドを通知してフォーカス移動を促す
            FirstErrorField = nameof(WarningBalance);
            return;
        }

        // 同行者数入力の自動クローズ秒数の検証（Issue #2009）
        var companionTimeoutResult = _validationService.ValidateCompanionCountInputTimeout(CompanionCountInputTimeoutSeconds);
        if (!companionTimeoutResult)
        {
            SetStatus(companionTimeoutResult.ErrorMessage!, true);
            FirstErrorField = nameof(CompanionCountInputTimeoutSeconds);
            return;
        }

        // 長期未返却のしきい値の検証（Issue #2152）
        var longTermResult = _validationService.ValidateLongTermUnreturnedDays(LongTermUnreturnedDays);
        if (!longTermResult)
        {
            SetStatus(longTermResult.ErrorMessage!, true);
            FirstErrorField = nameof(LongTermUnreturnedDays);
            return;
        }

        // Issue #2264: 職員証の認証を求めるのは、共有されるセキュリティ関連の設定（バックアップ先・データベース保存先）が
        // 変わるときだけ。文字サイズ等だけの保存では求めない。
        // 入力の検証・確認（未作成フォルダーの確認）・職員の登録状況の確認は認証より前に、短い処理中スコープの中で済ませる
        // （UNC の到達確認・Directory.Exists は待ち得るため、待っている間に保存を重ねて押させない）。
        // 認証ダイアログはそのスコープの外で出す（#2263 / #2265 と同じ）。
        SavePlan? plan;
        var requiresAuthentication = false;
        using (BeginBusy("確認中..."))
        {
            plan = await PrepareSavePlanAsync();
            if (plan == null)
            {
                // 検証で弾いた・確認で中止した（案内は設定済み）。何も保存しない
                return;
            }

            if (plan.ChangesSecuritySettings)
            {
                try
                {
                    // 職員が 0 人なら免除する（2 台目以降の PC は職員 0 人のローカルの既定 DB で起動し、ここで共有 DB を指す）。
                    // 判定は保存を押した時点の DB の状態で行う（#2263）
                    requiresAuthentication = await _settingsChangeService.RequiresOperatorAuthenticationAsync();
                }
                catch (Exception ex)
                {
                    // 現在の DB を開けない。職員を確かめられないので、認証なしで別の場所へ向ける変更は許さない
                    //（「デフォルトに戻す」も同じく断る。復旧は接続の回復か、IT 担当者による設定ファイルの削除）
                    ErrorDialogHelper.LogException(ex, "職員の登録状況の確認");
                    SetStatus(SecuritySettingsDatabaseUnavailableMessage, true);
                    return;
                }
            }
        }

        if (requiresAuthentication)
        {
            // 保存全体を認証した 1 操作のスコープ（#2260）の中で行い、操作ログの操作者を認証した職員にする
            using var authResult = (await _staffAuthService.RequestAuthenticationAsync("バックアップ先・データベース保存先の変更"))?.Enter();
            if (authResult == null)
            {
                // 何も保存していない。入力内容は残し（HasChanges は true のまま）、認証し直せばそのまま保存できるようにする
                SetStatus(SaveAuthenticationCancelledMessage, true);
                return;
            }

            await SaveCoreAsync(plan);
            return;
        }

        // 変更しない・職員が 0 人（免除。操作ログは「GUI操作」になる）
        await SaveCoreAsync(plan);
    }

    /// <summary>
    /// 入力を検証・正規化し、1 回の保存で何を変えるかを決める（Issue #2264）
    /// </summary>
    /// <returns>保存する内容。検証で弾いた・確認で中止したときは null（案内は設定済み）</returns>
    /// <remarks>
    /// 変更の有無は<b>正規化した後の値</b>で判定する。末尾の区切り文字だけが違う入力や、既定のままの保存先を
    /// 「変更」とみなすと、起きていない変更のために認証を求め、「既定 → 既定」のような記録を残す。
    /// </remarks>
    private async Task<SavePlan?> PrepareSavePlanAsync()
    {
        // バックアップ先（空は既定の場所。空白だけの入力は空とみなさず検証で弾く）
        string? newBackupPath = null;
        var backupInput = BackupPath ?? string.Empty;
        if (!string.Equals(backupInput, _loadedBackupPath, StringComparison.Ordinal))
        {
            var normalizedBackupPath = backupInput;
            if (backupInput.Length > 0)
            {
                // Issue #1269: UNC到達性チェックをUIブロックせず非同期実行
                var pathResult = await PathValidator.ValidateBackupPathAsync(backupInput);
                if (!pathResult.IsValid)
                {
                    SetStatus($"バックアップパスが無効です: {pathResult.ErrorMessage}", true);
                    // Issue #1279
                    FirstErrorField = nameof(BackupPath);
                    return null;
                }
                normalizedBackupPath = PathValidator.NormalizePath(backupInput) ?? backupInput;
            }

            // 空は既定の場所として比べる。未設定のとき欄には既定のパスが出るため、欄を空にしても既定の場所のまま
            //（空の値と既定のパスを比べて変更とみなすと、起きていない変更のために認証を求める）
            var comparableInput = normalizedBackupPath.Length > 0 ? normalizedBackupPath : PathValidator.GetDefaultBackupPath();
            var comparableLoaded = _loadedBackupPath.Length > 0
                ? PathValidator.NormalizePath(_loadedBackupPath) ?? _loadedBackupPath
                : PathValidator.GetDefaultBackupPath();
            // 末尾の区切り文字の有無は同じフォルダー（NormalizePath は末尾の区切り文字を残す）
            if (!string.Equals(
                    comparableInput.TrimEnd('\\', '/'),
                    comparableLoaded.TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase))
            {
                newBackupPath = normalizedBackupPath;
            }
        }

        // DBフォルダパスの検証（空の場合はデフォルトに戻す）。Issue #2264 で保存の前（認証の前）へ移した。
        // 以前は他の設定を保存した後で検証しており、ここで弾かれても他の設定だけが保存されていた。
        string? newDatabaseFolder = null;
        string newDatabaseFilePath = string.Empty; // database_config.txt に保存するファイルパス（空は既定）
        if (IsDatabasePathChanged)
        {
            var folder = DatabasePath ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                // Issue #1269: UNC到達性をUIブロックせず非同期検証
                var dbPathResult = await PathValidator.ValidateBackupPathAsync(folder);
                if (!dbPathResult.IsValid)
                {
                    SetStatus($"データベース保存先が無効です: {dbPathResult.ErrorMessage}", true);
                    // Issue #1279
                    FirstErrorField = nameof(DatabasePath);
                    return null;
                }

                // パスを正規化
                var normalizedDir = PathValidator.NormalizePath(folder);
                if (normalizedDir == null)
                {
                    SetStatus("データベース保存先のパスを正規化できません", true);
                    // Issue #1279
                    FirstErrorField = nameof(DatabasePath);
                    return null;
                }
                folder = normalizedDir;
                newDatabaseFilePath = Path.Combine(normalizedDir, Data.DbContext.DatabaseFileName);
            }

            // 既定へ（空）の変更は、設定ファイルが無い・中身が空白だけなら変更なし。AppConfigFiles.Read は読めない
            //（ロック・権限）ときも空文字を返すため、それで比べると共有 DB を指したままのファイルを「既定のまま」と
            // みなしてしまう。読めないファイルは変更ありとして進め、書き換えの段で失敗を案内する（「デフォルトに戻す」と同じ判定）
            var unchangedDatabasePath = newDatabaseFilePath.Length == 0
                ? !HasNonEmptyConfigFile(DatabaseConfigPath)
                : string.Equals(newDatabaseFilePath, AppConfigFiles.Read(DatabaseConfigPath), StringComparison.OrdinalIgnoreCase);
            if (unchangedDatabasePath)
            {
                // 正規化すると今と同じ保存先（末尾の区切り文字の違い・既に既定のまま等）。変更しない
                _originalDatabasePath = DatabasePath ?? string.Empty;
                IsDatabasePathChanged = false;
            }
            else
            {
                if (newDatabaseFilePath.Length > 0)
                {
                    // Issue #1924: 保存先フォルダーが未作成なら、続行前に確認する。
                    //
                    // バックアップ先の検証（ValidateBackupPathAsync）は「フォルダーが
                    // まだ無い」ことを許容する（作成は実行時に行う）。この検証は DB 保存先にも
                    // 使い回されているが、DB の場合は意味が違う ―― 未作成のフォルダーを
                    // 指定して再起動すると、SQLite が**新しい空の DB**をそこに作り、
                    // 既存の共有データベースからは切り離される（台帳分裂）。
                    // 入力誤り（共有名のタイポ）と初回セットアップ（意図的な新規作成）は
                    // パスだけでは区別できないため、機械的に弾かずに利用者へ判断させる
                    //（`.claude/rules/development-conventions.md`「曖昧な入力は『塞ぐ』より
                    // 『解決結果を見せる』」）。
                    //
                    // Directory.Exists は UNC でハングし得るため Task.Run へ逃がす（Issue #1746）。
                    var folderExists = await Task.Run(() => Directory.Exists(folder));
                    if (!ConfirmCreatingNewDatabaseFolderIfMissing(folder, folderExists))
                    {
                        SetStatus("データベース保存先の変更を中止しました。", false);
                        return null;
                    }
                }

                newDatabaseFolder = folder;
            }
        }

        return new SavePlan(newBackupPath, newDatabaseFolder, newDatabaseFilePath);
    }

    /// <summary>
    /// バックアップ先・データベース保存先を変えようとしたが、現在の DB を開けず職員を確かめられないときの案内（Issue #2264）
    /// </summary>
    internal const string SecuritySettingsDatabaseUnavailableMessage =
        "現在のデータベースに接続できないため、職員証の認証ができず、バックアップ先・データベース保存先を変更していません" +
        "（ほかの設定も保存していません）。共有フォルダーへの接続を回復してからもう一度「保存」を押すか、" +
        "接続を回復できないときは IT 担当者にこの PC の database_config.txt の削除を依頼してください。";

    /// <summary>
    /// 「デフォルトに戻す」を、現在の DB を開けず職員を確かめられないために断ったときの案内（Issue #2264）
    /// </summary>
    internal const string ResetDatabaseUnavailableMessage =
        "現在のデータベースに接続できないため、職員証の認証ができず、データベース保存先をデフォルトに戻していません。" +
        "共有フォルダーへの接続を回復してからもう一度「デフォルトに戻す」を押すか、" +
        "接続を回復できないときは IT 担当者にこの PC の database_config.txt の削除を依頼してください。";

    /// <summary>
    /// 設定の保存で職員証の認証が完了しなかったときの案内（Issue #2264）
    /// </summary>
    internal const string SaveAuthenticationCancelledMessage =
        "バックアップ先・データベース保存先の変更には職員証の認証が必要ですが、認証が完了しなかったため、" +
        "設定を保存していません（入力内容はそのまま残っています）。" +
        "保存するには、もう一度「保存」を押して職員証をタッチしてください。";

    /// <summary>
    /// 「デフォルトに戻す」で職員証の認証が完了しなかったときの案内（Issue #2264）
    /// </summary>
    internal const string ResetAuthenticationCancelledMessage =
        "データベース保存先をデフォルトに戻すには職員証の認証が必要ですが、認証が完了しなかったため、" +
        "保存先を変更していません。戻すには、もう一度「デフォルトに戻す」を押して職員証をタッチしてください。";

    /// <summary>
    /// 1 回の保存で何を変えるか（Issue #2264）
    /// </summary>
    internal sealed class SavePlan
    {
        public SavePlan(string? newBackupPath, string? newDatabaseFolder, string newDatabaseFilePath)
        {
            NewBackupPath = newBackupPath;
            NewDatabaseFolder = newDatabaseFolder;
            NewDatabaseFilePath = newDatabaseFilePath;
        }

        /// <summary>変更後のバックアップ先（正規化済み）。変更しないなら null</summary>
        public string? NewBackupPath { get; }

        /// <summary>変更後のデータベース保存先フォルダー（画面表示用）。変更しないなら null</summary>
        public string? NewDatabaseFolder { get; }

        /// <summary>database_config.txt に書くファイルパス（空は既定の場所）</summary>
        public string NewDatabaseFilePath { get; }

        /// <summary>共有されるセキュリティ関連の設定を変えるか（職員が 1 人以上いれば職員証の認証を求める）</summary>
        public bool ChangesSecuritySettings => NewBackupPath != null || NewDatabaseFolder != null;
    }

    /// <summary>
    /// 保存の本体（検証・確認・認証を済ませた後）
    /// </summary>
    /// <remarks>
    /// Issue #2264: セキュリティ関連の設定（バックアップ先 → データベース保存先）を先に変更し、そのあとで他の設定を保存する。
    /// バックアップ先の変更に失敗したら何も保存しない。
    /// </remarks>
    private async Task SaveCoreAsync(SavePlan plan)
    {
        using (BeginBusy("保存中..."))
        {
            // バックアップ先の変更（共有 DB。値・変更日時・PC 名・操作ログを 1 トランザクションで書く）
            if (plan.NewBackupPath is string newBackupPath)
            {
                try
                {
                    // DB の書き込みは UI スレッドから退避する（#2197 と同じ）
                    await Task.Run(() => _settingsChangeService.ChangeBackupPathAsync(newBackupPath));
                }
                catch (Exception ex)
                {
                    ErrorDialogHelper.LogException(ex, "バックアップ先の変更");
                    SetStatus(ExceptionMessageFormatter.ToUserMessage(ex, "バックアップ先の変更"), true);
                    return;
                }

                _loadedBackupPath = newBackupPath;
                // 正規化されたパスをUIに反映
                if (BackupPath != newBackupPath)
                {
                    BackupPath = newBackupPath;
                }
            }

            // データベース保存先の変更（この PC の database_config.txt。操作ログは現在の DB に残す）
            if (plan.NewDatabaseFolder is string newDatabaseFolder)
            {
                try
                {
                    await Task.Run(() => _settingsChangeService.ChangeDatabaseConfigAsync(DatabaseConfigPath, plan.NewDatabaseFilePath));
                    DatabasePath = newDatabaseFolder;
                    _originalDatabasePath = newDatabaseFolder;
                    IsDatabasePathChanged = false;
                }
                catch (Exception ex)
                {
                    ErrorDialogHelper.LogException(ex, "データベース保存先の設定ファイルの保存");
                    SetStatus(
                        (plan.NewBackupPath != null ? "バックアップ先の変更は保存しました。" : string.Empty) +
                        ExceptionMessageFormatter.ToUserMessage(ex, "データベース保存先の設定ファイルの保存"),
                        true);
                    return;
                }
            }

            var settings = new AppSettings
            {
                WarningBalance = WarningBalance,
                FontSize = SelectedFontSizeItem?.Value ?? FontSizeOption.Medium,
                SoundMode = SelectedSoundModeItem?.Value ?? SoundMode.Beep,
                ToastPosition = SelectedToastPositionItem?.Value ?? ToastPosition.TopRight,
                DepartmentType = SelectedDepartmentTypeItem?.Value ?? DepartmentType.MayorOffice,
                SkipBusStopInputOnReturn = SkipBusStopInputOnReturn,
                SkipCompanionCountInputOnReturn = SkipCompanionCountInputOnReturn,
                CompanionCountInputTimeoutSeconds = CompanionCountInputTimeoutSeconds,
                LongTermUnreturnedDays = LongTermUnreturnedDays,
                ShowHistoryOnReturn = ShowHistoryOnReturn
            };

            // Issue #2197: DB への書き込みは Task.Run で UI スレッドから退避する（接続診断・バックアップと同じ方針）。
            // SaveAppSettingsAsync は名前こそ非同期だが、BeginTransactionAsync の最初の await（セマフォの取得）が
            // 同期的に完了するため、BEGIN IMMEDIATE と各 INSERT は呼び出し元のスレッドで実行される。
            // ほかの接続（共有モードの他 PC 等）が書き込み中だと、その待ち（busy_timeout と ADO 層の再試行）の間
            // UI スレッドが止まり、アプリ全体が固まって処理中オーバーレイも描画されなかった。
            // Issue #2202 で DbContext の入口が UI スレッドから移すようになったため、この Task.Run は二重の備えになる
            // （外しても固まらない）。Task.Run の中からの呼び出しは UI 起点のゲートを取らないが、書き込みは
            // トランザクション（セマフォ）でほかの書き込みと直列化される（#2197 から変わらない）。
            // バックアップ先（backup_path）は一括保存に載らない（Issue #2264。上の ChangeBackupPathAsync だけが書く）。
            var success = await Task.Run(() => _settingsRepository.SaveAppSettingsAsync(settings));

            if (success)
            {
                SetStatus("設定を保存しました", false);
                HasChanges = false;

                // 部署種別の変更を摘要生成器へ反映（Issue #1975）
                // 保存成功時だけ反映する（順序を逆にすると「保存できませんでした」と
                // 案内しながら摘要生成だけ新しい部署種別で動く。#1905 と同じ判断）。
                // これを省くと、履歴統合・履歴分割・返却時の台帳生成・明細編集の
                // 摘要再生成がアプリ再起動まで旧設定でチャージ摘要を作り、
                // 「役務費によりチャージ」が 6 年保存の台帳へ入る。
                // UI 装飾の反映（文字サイズ・トースト位置）より前に置くのは、
                // それらが失敗しても台帳へ効く反映だけは確定させるため
                // （development-conventions.md「コミット確定後の後処理を、成否の判定に巻き込まない」）
                _summaryGenerator.ApplyDepartmentType(settings.DepartmentType);

                // 文字サイズの変更を反映
                ApplyFontSize(settings.FontSize);

                // 音声モードの変更を反映
                _soundPlayer.SoundMode = settings.SoundMode;

                // トースト位置の変更を反映
                App.ApplyToastPosition(settings.ToastPosition);

                // 部署種別を設定ファイルに保存（インストーラーがアップグレード時に読み込む）
                try
                {
                    AppConfigFiles.Write(
                        DepartmentConfigPath,
                        SettingsRepository.DepartmentTypeToString(settings.DepartmentType));
                }
                catch
                {
                    // 非致命的: DBが正なので設定ファイルの書き込み失敗は無視
                }

                if (plan.NewDatabaseFolder != null)
                {
                    // DBパス変更時はダイアログを閉じず、再起動を案内
                    SetStatus("データベース保存先を変更しました。変更を反映するにはアプリケーションを再起動してください。", false);
                    return;
                }

                // 保存完了フラグを立てる（ダイアログを閉じるトリガー）
                IsSaved = true;
            }
            else
            {
                SetStatus(
                    plan.ChangesSecuritySettings ? BuildPartialSaveFailureMessage(plan) : "設定の保存に失敗しました",
                    true);
            }
        }
    }

    /// <summary>
    /// セキュリティ関連の設定は変更できたが、ほかの設定の一括保存に失敗したときの案内（Issue #2264）
    /// </summary>
    /// <remarks>
    /// 実際に変えた方だけを名指しする。データベース保存先を書き換えた後なら、成功時と同じく再起動の案内を付ける
    /// （設定ファイルは書き換え済みで、次の起動から新しい保存先を使う）。
    /// </remarks>
    internal static string BuildPartialSaveFailureMessage(SavePlan plan)
    {
        var changed = plan.NewBackupPath != null && plan.NewDatabaseFolder != null
            ? "バックアップ先とデータベース保存先"
            : plan.NewBackupPath != null ? "バックアップ先" : "データベース保存先";
        var restart = plan.NewDatabaseFolder != null
            ? "データベース保存先の変更は、アプリケーションを再起動すると反映されます。"
            : string.Empty;
        return $"{changed}は変更しましたが、ほかの設定（文字サイズ・残額警告など）はデータベースへ書き込めなかったため保存していません。" +
               restart +
               "ほかの設定を保存するには、もう一度「保存」を押してください。";
    }

    /// <summary>
    /// データベース保存先フォルダーが未作成のとき、新規データベースが作られることを確認する（Issue #1924）
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2264 で保存の前（<c>BeginBusy</c> スコープの外、職員証の認証の前）へ移したが、
    /// 呼び出し元が処理中スコープの内側へ戻っても背後でオーバーレイが回らないよう <c>SuspendBusy()</c> で囲んだままにする
    /// （処理中でないときは何もしない。Issue #1793 / `03_画面設計書.md` §3.10）。
    /// </para>
    /// <para>
    /// フォルダーの存在確認（I/O）は呼び出し元が行い、本メソッドは<b>判断と提示</b>だけを担う。
    /// <c>Directory.Exists</c> は UNC でハングし得るため呼び出し元が <c>Task.Run</c> へ逃がしており
    /// （Issue #1746）、判断を分離することで実ファイルシステムに触れずに単体テストできる
    /// （Issue #1794「判断を純関数へ切り出す」と同じ形）。
    /// </para>
    /// </remarks>
    /// <param name="folderPath">正規化済みのデータベース保存先フォルダー</param>
    /// <param name="folderExists">そのフォルダーが実在するか</param>
    /// <returns>続行してよい場合 true</returns>
    internal bool ConfirmCreatingNewDatabaseFolderIfMissing(string folderPath, bool folderExists)
    {
        if (folderExists)
        {
            return true;
        }

        const string Title = "データベース保存先の確認";
        var message =
            "指定されたフォルダーは存在しません。\n\n" +
            $"　{folderPath}\n\n" +
            "続行すると、このフォルダーを作成して新しい空のデータベースを作成します。\n" +
            "既存の共有データベースへ接続するつもりなら、パスの入力誤りがないか確認してください。\n\n" +
            "このまま続行しますか？";

        using (SuspendBusy())
        {
            return _dialogService.ShowConfirmation(message, Title);
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
                ? GetDefaultDatabaseFolder()
                : DatabasePath,
            ShowNewFolderButton = true
        })
        {
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                DatabasePath = dialog.SelectedPath;
                HasChanges = true;
            }
        }
    }

    /// <summary>
    /// デフォルトのデータベース保存先フォルダパスを取得（Issue #1559）。
    /// C:\ProgramData\ICCardManager\ を返す。
    /// </summary>
    internal static string GetDefaultDatabaseFolder()
    {
        return AppDataPaths.RootDirectory;
    }

    /// <summary>
    /// データベース保存先をデフォルト（ローカル）に戻す（Issue #1559）。
    /// database_config.txt を削除し、UI上のパスをデフォルトフォルダパスに更新する。反映には再起動が必要。
    /// </summary>
    /// <remarks>
    /// Issue #2264: この PC の以後の記録を別の DB へ切り替える操作のため、保存先の変更と同じく職員証の認証を求め、
    /// 操作ログ（変更前後の保存先）を記録する。設定ファイルが無い（既にデフォルト）ときは何も変わらないので認証を求めない。
    /// </remarks>
    [RelayCommand]
    public async Task ResetDatabasePathToDefaultAsync()
    {
        var defaultFolder = GetDefaultDatabaseFolder();
        const string Title = "データベース保存先をデフォルトに戻す";
        var message =
            $"データベース保存先をローカルのデフォルト（{defaultFolder}\\iccard.db）に戻します。\n" +
            "現在の設定ファイル（database_config.txt）は削除されます。\n\n" +
            "変更を反映するにはアプリケーションの再起動が必要です。続行しますか？";

        if (!_dialogService.ShowConfirmation(message, Title))
        {
            return;
        }

        var configPath = DatabaseConfigPath;
        // 設定ファイルが無い・中身が空なら既に既定の保存先。変化が無いので認証も記録もしない
        //（空のファイルは残しても既定の保存先を指すので、そのまま置いておく）。
        // 空かどうかは AppConfigFiles.Read ではなくファイルの大きさで見る。Read は読めない（権限・ロック）ときも
        // 空文字を返すため、それで判定すると読めないファイルを残したまま「デフォルトに戻しました」と案内してしまう。
        // 読めないファイルは「変更あり」として進め、削除・記録の段で失敗を案内する
        if (HasNonEmptyConfigFile(configPath))
        {
            var requiresAuthentication = false;
            Exception? databaseUnavailable = null;
            using (BeginBusy("確認中..."))
            {
                try
                {
                    // 職員が 0 人なら免除する（誤った共有フォルダーを指して空の DB ができた PC を、#1924 の状態から戻せるように）
                    requiresAuthentication = await _settingsChangeService.RequiresOperatorAuthenticationAsync();
                }
                catch (Exception ex)
                {
                    databaseUnavailable = ex;
                }
            }

            if (databaseUnavailable != null)
            {
                // 現在の DB を開けない（共有フォルダーに接続できない・壊れている）。職員を確かめられず認証できないので断る。
                // 認証なしで戻せると、ネットワークを切って戻す → 再起動で職員 0 人のローカルの既定 DB → 免除で認証なしに
                // 保存先を任意の場所へ、という迂回路になる（レビューで検出）。起動時に DB を開けなければアプリは起動しないため、
                // その場合の復旧は IT 担当者が database_config.txt を削除する
                ErrorDialogHelper.LogException(databaseUnavailable, "データベース保存先をデフォルトに戻す前の職員の確認");
                SetStatus(ResetDatabaseUnavailableMessage, true);
                return;
            }
            else if (requiresAuthentication)
            {
                using var authResult = (await _staffAuthService.RequestAuthenticationAsync("データベース保存先をデフォルトに戻す"))?.Enter();
                if (authResult == null)
                {
                    // 設定ファイルは削除していない
                    SetStatus(ResetAuthenticationCancelledMessage, true);
                    return;
                }

                if (!await ResetWithAuditAsync(configPath))
                {
                    return;
                }
            }
            else if (!await ResetWithAuditAsync(configPath))
            {
                // 職員が 0 人（免除。操作ログは「GUI操作」になる）
                return;
            }
        }

        // デフォルトフォルダを UI に表示。config 削除は既に完了しているため、
        // 「変更なし状態」として IsDatabasePathChanged=false にして再保存処理を不要にする
        DatabasePath = defaultFolder;
        _originalDatabasePath = defaultFolder;
        IsDatabasePathChanged = false;
        SetStatus(
            "データベース保存先をデフォルトに戻しました。変更を反映するにはアプリケーションを再起動してください。",
            false);
    }

    /// <summary>
    /// 設定ファイルが存在し、空でないか（読めるかどうかは問わない。Issue #2264）
    /// </summary>
    /// <remarks>
    /// 空白・改行だけのファイルは既定の保存先を指す（起動時の読み取りも前後の空白を除く）ので「空」とみなす。
    /// 中身を確かめるのは小さいファイルに限る（パス 1 行の設定ファイルより大きいものは中身ありとみなす）。
    /// 読めない（ロック・権限）ときは既定とは言えないので「中身あり」とする。
    /// </remarks>
    private static bool HasNonEmptyConfigFile(string configPath)
    {
        const long MaxInspectedBytes = 4096;
        try
        {
            var info = new FileInfo(configPath);
            if (!info.Exists || info.Length == 0)
            {
                return false;
            }

            if (info.Length > MaxInspectedBytes)
            {
                return true;
            }

            // 読めることを先に確かめる（AppConfigFiles.Read は読めないときも空文字を返すため）
            _ = File.ReadAllBytes(configPath);
            return AppConfigFiles.Read(configPath).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 大きさも中身も分からない: 既定とは言えないので変更ありとして進める
            return true;
        }
    }

    /// <summary>
    /// 設定ファイルを削除し、操作ログに記録する（「デフォルトに戻す」。Issue #2264）
    /// </summary>
    /// <returns>削除できたら true。失敗したら案内を設定して false</returns>
    private async Task<bool> ResetWithAuditAsync(string configPath)
    {
        try
        {
            using (BeginBusy("保存中..."))
            {
                await Task.Run(() => _settingsChangeService.ChangeDatabaseConfigAsync(configPath, databaseFilePath: null));
            }
            return true;
        }
        catch (Exception ex)
        {
            ErrorDialogHelper.LogException(ex, "データベース保存先の設定ファイルの削除");
            SetStatus(ExceptionMessageFormatter.ToUserMessage(ex, "データベース保存先の設定ファイルの削除"), true);
            return false;
        }
    }

    // =========================================================================
    // 設定ファイル（C:\ProgramData\ICCardManager\*_config.txt）
    // =========================================================================
    // 置き場所の既定値・文字コード判定・アトミック書き込みは Common/AppConfigFiles に集約した（Issue #2266）。
    // この画面は注入された置き場所（_configDirectory）の配下を読み書きする（Issue #2098）。
    // appsettings.jsonはProgram Files内にあり一般ユーザーには書き込めないため、
    // データベースの保存先は database_config.txt に保存する。

    /// <summary>この画面が読み書きする database_config.txt（Issue #2098）</summary>
    private string DatabaseConfigPath => Path.Combine(_configDirectory, AppConfigFiles.DatabaseConfigFileName);

    /// <summary>この画面が書き込む department_config.txt（Issue #2098）</summary>
    private string DepartmentConfigPath => Path.Combine(_configDirectory, AppConfigFiles.DepartmentConfigFileName);

    /// <summary>
    /// 文字サイズをアプリケーションに適用
    /// </summary>
    private static void ApplyFontSize(FontSizeOption fontSize)
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

    partial void OnSkipCompanionCountInputOnReturnChanged(bool value)
    {
        HasChanges = true;
    }

    partial void OnCompanionCountInputTimeoutSecondsChanged(int value)
    {
        HasChanges = true;
    }

    partial void OnLongTermUnreturnedDaysChanged(int value)
    {
        HasChanges = true;
    }

    partial void OnShowHistoryOnReturnChanged(bool value)
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
