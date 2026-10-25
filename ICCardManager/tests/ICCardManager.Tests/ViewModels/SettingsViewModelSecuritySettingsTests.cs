using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.Tests.Infrastructure.Timing;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2264: 設定画面（F5）でバックアップ先・データベース保存先を変更するときに職員証の認証を求め、
/// 認証した職員の操作として操作ログ（変更前後の値）に記録することを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 修正前は F5 を開けば誰でも、認証なしで共有 DB のバックアップ先（全 PC が起動のたびに DB 全体をコピーする先）と
/// その PC のデータベース保存先を変更でき、変更は操作ログにも残らなかった。
/// </para>
/// <para>
/// 認証の代役は本物の <see cref="StaffAuthService"/> と同じ結果（<c>Enter()</c> で操作者セッションを開始する認証結果）を
/// 返し、変更は本物の <see cref="SettingsChangeService"/> がインメモリ DB と一時フォルダーへ書く。
/// 操作者の帰属は書き込まれた操作ログの行で表明する（Issue #1729）。
/// 一括保存（他の設定）はリポジトリの代役で受け、保存後の WPF 依存の反映（文字サイズ等）を避けるため false を返す。
/// </para>
/// </remarks>
public sealed class SettingsViewModelSecuritySettingsTests : IDisposable
{
    private const string AuthenticatedStaffIdm = "FFFF0000000000AA";
    private const string AuthenticatedStaffName = "認証 太郎";
    private const string OldBackupPath = @"C:\OldBackup";

    private readonly DbContext _dbContext;
    private readonly SettingsRepository _realSettingsRepository;
    private readonly OperationLogRepository _operationLogRepository;
    private readonly CurrentOperatorContext _operatorContext =
        new(new FixedSystemClock(new DateTime(2026, 10, 13, 9, 0, 0)));
    private readonly Mock<ISettingsRepository> _bulkSettingsRepositoryMock = new();
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();
    /// <summary>職員の登録状況（既定: 登録済みの職員がいる＝認証を求める）</summary>
    private readonly Mock<IStaffRepository> _staffRepositoryMock = new();
    private readonly string _workDirectory;
    private readonly string _configDirectory;
    private readonly string _newBackupFolder;
    private readonly string _newDatabaseFolder;
    private readonly Mock<IValidationService> _validation = new();
    private readonly SettingsChangeService _settingsChangeService;
    private readonly SettingsViewModel _viewModel;

    public SettingsViewModelSecuritySettingsTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        _realSettingsRepository = new SettingsRepository(
            _dbContext, new Mock<ICacheService>().Object, Options.Create(new CacheOptions()));
        _operationLogRepository = new OperationLogRepository(_dbContext);

        _workDirectory = Path.Combine(Path.GetTempPath(), "ICCardManagerTest_" + Path.GetRandomFileName());
        _configDirectory = Path.Combine(_workDirectory, "config");
        _newBackupFolder = Path.Combine(_workDirectory, "backup");
        _newDatabaseFolder = Path.Combine(_workDirectory, "db");
        Directory.CreateDirectory(_configDirectory);
        Directory.CreateDirectory(_newBackupFolder);
        Directory.CreateDirectory(_newDatabaseFolder);

        _bulkSettingsRepositoryMock
            .Setup(r => r.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { WarningBalance = 10000, BackupPath = OldBackupPath });
        _bulkSettingsRepositoryMock
            .Setup(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()))
            .ReturnsAsync(false);

        _validation.Setup(v => v.ValidateWarningBalance(It.IsAny<int>())).Returns(ValidationResult.Success());
        _validation.Setup(v => v.ValidateCompanionCountInputTimeout(It.IsAny<int>())).Returns(ValidationResult.Success());
        _validation.Setup(v => v.ValidateLongTermUnreturnedDays(It.IsAny<int>())).Returns(ValidationResult.Success());
        _dialogServiceMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _staffRepositoryMock.Setup(r => r.HasActiveStaffAsync()).ReturnsAsync(true);

        _settingsChangeService = new SettingsChangeService(
            _dbContext,
            _realSettingsRepository,
            new OperationLogger(_operationLogRepository, _operatorContext),
            new StaffManagementService(
                _dbContext, _staffRepositoryMock.Object,
                new OperationLogger(_operationLogRepository, _operatorContext),
                NullLogger<StaffManagementService>.Instance),
            new FixedSystemClock(new DateTime(2026, 10, 13, 14, 5, 0)),
            NullLogger<SettingsChangeService>.Instance);

        _viewModel = CreateViewModel();
    }

    /// <summary>
    /// 画面を作る（コンストラクタが database_config.txt を読むため、設定ファイルを置いた後で作り直す用途にも使う）
    /// </summary>
    private SettingsViewModel CreateViewModel() => new SettingsViewModel(
        _bulkSettingsRepositoryMock.Object,
        _validation.Object,
        new Mock<ISoundPlayer>().Object,
        _dialogServiceMock.Object,
        new SummaryGenerator(DepartmentType.MayorOffice),
        _staffAuthServiceMock.Object,
        _settingsChangeService,
        _configDirectory);

    public void Dispose()
    {
        _dbContext.Dispose();
        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch
        {
            // 後片付けの失敗はテスト結果に影響させない（一時フォルダー配下）
        }
        GC.SuppressFinalize(this);
    }

    private string ConfigPath => Path.Combine(_configDirectory, AppConfigFiles.DatabaseConfigFileName);

    /// <summary>
    /// 画面を開いた状態にする（共有 DB のバックアップ先は旧い値）
    /// </summary>
    private async Task OpenAsync()
    {
        await _realSettingsRepository.SetAsync(SettingsRepository.KeyBackupPath, OldBackupPath);
        await _viewModel.LoadSettingsAsync();
    }

    /// <summary>
    /// 認証の代役を、本物と同じ（<c>Enter()</c> で操作者セッションを開始する）認証結果で成功させる。
    /// 応答は非同期に完了させる（セッションを認証サービスの内側で開始する実装だと画面の流れに残らず赤になる）。
    /// </summary>
    private void ArrangeAuthenticationSucceeds()
    {
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .Returns(async () =>
            {
                await Task.Yield();
                return StaffAuthService.CreateAuthenticatedResult(
                    _operatorContext, AuthenticatedStaffIdm, AuthenticatedStaffName);
            });
    }

    /// <summary>認証のキャンセル・タイムアウト（認証サービスは null を返す）。</summary>
    private void ArrangeAuthenticationCancelled()
    {
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync((StaffAuthResult?)null);
    }

    #region バックアップ先

    [Fact]
    public async Task バックアップ先を変更して認証が完了しなければ_何も保存せず入力を残すこと()
    {
        await OpenAsync();
        ArrangeAuthenticationCancelled();
        _viewModel.BackupPath = _newBackupFolder;

        await _viewModel.SaveAsync();

        (await _realSettingsRepository.GetAsync(SettingsRepository.KeyBackupPath)).Should().Be(OldBackupPath);
        (await GetSettingLogsAsync()).Should().BeEmpty();
        _bulkSettingsRepositoryMock.Verify(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()), Times.Never,
            "認証が完了しなければ他の設定も保存しない");
        _viewModel.StatusMessage.Should().Be(SettingsViewModel.SaveAuthenticationCancelledMessage);
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.BackupPath.Should().Be(_newBackupFolder, "入力内容は残し、認証し直せばそのまま保存できる");
        _viewModel.HasChanges.Should().BeTrue();
    }

    [Fact]
    public async Task バックアップ先を変更して認証すると_認証した職員の操作として変更前後が記録されること()
    {
        await OpenAsync();
        ArrangeAuthenticationSucceeds();
        _viewModel.BackupPath = _newBackupFolder;

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("バックアップ先・データベース保存先の変更"), Times.Once);
        var expected = PathValidator.NormalizePath(_newBackupFolder);
        (await _realSettingsRepository.GetAsync(SettingsRepository.KeyBackupPath)).Should().Be(expected);
        (await _realSettingsRepository.GetAsync(SettingsRepository.KeyBackupPathChangedAt)).Should().Be("2026-10-13 14:05:00");

        var log = (await GetSettingLogsAsync()).Single();
        log.TargetId.Should().Be(OperationLogger.SettingNames.BackupPath);
        log.OperatorIdm.Should().Be(AuthenticatedStaffIdm);
        log.OperatorName.Should().Be(AuthenticatedStaffName);
        ReadValue(log.BeforeData).Should().Be(OldBackupPath);
        ReadValue(log.AfterData).Should().Be(expected);
        _operatorContext.TryGetOperator(out _, out _).Should().BeFalse("保存が終わったら操作者セッションは閉じる（#2260）");
    }

    [Fact]
    public async Task バックアップ先を変えずに他の設定だけを保存するときは_認証を求めないこと()
    {
        await OpenAsync();
        _viewModel.WarningBalance = 3000;

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
        _bulkSettingsRepositoryMock.Verify(
            r => r.SaveAppSettingsAsync(It.Is<AppSettings>(s => s.WarningBalance == 3000)), Times.Once);
        (await GetSettingLogsAsync()).Should().BeEmpty();
        (await _realSettingsRepository.GetAsync(SettingsRepository.KeyBackupPath)).Should().Be(OldBackupPath);
    }

    #endregion

    #region データベース保存先

    [Fact]
    public async Task データベース保存先を変更して認証が完了しなければ_設定ファイルを書かないこと()
    {
        File.WriteAllText(ConfigPath, @"C:\old\iccard.db");
        await OpenAsync();
        ArrangeAuthenticationCancelled();
        _viewModel.DatabasePath = _newDatabaseFolder;

        await _viewModel.SaveAsync();

        File.ReadAllText(ConfigPath).Should().Be(@"C:\old\iccard.db");
        (await GetSettingLogsAsync()).Should().BeEmpty();
        _viewModel.StatusMessage.Should().Be(SettingsViewModel.SaveAuthenticationCancelledMessage);
        _viewModel.IsDatabasePathChanged.Should().BeTrue("入力内容は残す");
    }

    [Fact]
    public async Task データベース保存先を変更して認証すると_認証した職員の操作として記録されること()
    {
        File.WriteAllText(ConfigPath, @"C:\old\iccard.db");
        await OpenAsync();
        ArrangeAuthenticationSucceeds();
        _viewModel.DatabasePath = _newDatabaseFolder;

        await _viewModel.SaveAsync();

        var expected = Path.Combine(PathValidator.NormalizePath(_newDatabaseFolder)!, DbContext.DatabaseFileName);
        AppConfigFiles.Read(ConfigPath).Should().Be(expected);
        var log = (await GetSettingLogsAsync()).Single();
        log.TargetId.Should().Be(OperationLogger.SettingNames.DatabasePath);
        log.OperatorIdm.Should().Be(AuthenticatedStaffIdm);
        ReadValue(log.BeforeData).Should().Be(@"C:\old\iccard.db");
        ReadValue(log.AfterData).Should().Be(expected);
    }

    [Fact]
    public async Task デフォルトに戻すで認証が完了しなければ_設定ファイルを削除しないこと()
    {
        File.WriteAllText(ConfigPath, @"\\fileserver\iccard\iccard.db");
        ArrangeAuthenticationCancelled();

        await _viewModel.ResetDatabasePathToDefaultAsync();

        File.Exists(ConfigPath).Should().BeTrue();
        (await GetSettingLogsAsync()).Should().BeEmpty();
        _viewModel.StatusMessage.Should().Be(SettingsViewModel.ResetAuthenticationCancelledMessage);
        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("データベース保存先をデフォルトに戻す"), Times.Once);
    }

    [Fact]
    public async Task デフォルトに戻すで認証すると_設定ファイルを削除し認証した職員の操作として記録されること()
    {
        File.WriteAllText(ConfigPath, @"\\fileserver\iccard\iccard.db");
        ArrangeAuthenticationSucceeds();

        await _viewModel.ResetDatabasePathToDefaultAsync();

        File.Exists(ConfigPath).Should().BeFalse();
        var log = (await GetSettingLogsAsync()).Single();
        log.OperatorIdm.Should().Be(AuthenticatedStaffIdm);
        ReadValue(log.BeforeData).Should().Be(@"\\fileserver\iccard\iccard.db");
    }

    #endregion

    #region 職員が 0 人のときの免除・DB を開けないとき（レビュー指摘 must）

    [Fact]
    public async Task 職員が0人なら_認証を求めずにバックアップ先を変更しGUI操作として記録すること()
    {
        // 2 台目以降の PC は職員 0 人のローカルの既定 DB で起動し、F5 で共有 DB を指す
        _staffRepositoryMock.Setup(r => r.HasActiveStaffAsync()).ReturnsAsync(false);
        await OpenAsync();
        _viewModel.BackupPath = _newBackupFolder;

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
        (await _realSettingsRepository.GetAsync(SettingsRepository.KeyBackupPath))
            .Should().Be(PathValidator.NormalizePath(_newBackupFolder));
        var log = (await GetSettingLogsAsync()).Single();
        log.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
        log.OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
    }

    [Fact]
    public async Task 職員が0人なら_認証を求めずにデータベース保存先を共有DBへ向けられること()
    {
        _staffRepositoryMock.Setup(r => r.HasActiveStaffAsync()).ReturnsAsync(false);
        await OpenAsync();
        _viewModel.DatabasePath = _newDatabaseFolder;

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
        AppConfigFiles.Read(ConfigPath).Should().Be(
            Path.Combine(PathValidator.NormalizePath(_newDatabaseFolder)!, DbContext.DatabaseFileName));
        (await GetSettingLogsAsync()).Single().OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
    }

    [Fact]
    public async Task 職員が0人なら_認証を求めずにデフォルトに戻せること()
    {
        // 誤った共有フォルダーを指して空の DB ができた PC（#1924）を戻す
        _staffRepositoryMock.Setup(r => r.HasActiveStaffAsync()).ReturnsAsync(false);
        File.WriteAllText(ConfigPath, @"\\typo\share\iccard.db");

        await _viewModel.ResetDatabasePathToDefaultAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
        File.Exists(ConfigPath).Should().BeFalse();
        (await GetSettingLogsAsync()).Single().OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
    }

    /// <summary>
    /// レビュー 2 回目: DB を開けないときに「デフォルトに戻す」を認証なしで許すと、ネットワークを切って戻す →
    /// 再起動で職員 0 人のローカルの既定 DB → 免除で認証なしに保存先を任意の場所へ、という迂回路になる。断ること。
    /// </summary>
    [Fact]
    public async Task 現在のDBを開けないときは_デフォルトに戻すも断ること()
    {
        _staffRepositoryMock.Setup(r => r.HasActiveStaffAsync())
            .ThrowsAsync(new InvalidOperationException("共有フォルダーに接続できない（テスト）"));
        File.WriteAllText(ConfigPath, @"\\fileserver\share\iccard.db");

        await _viewModel.ResetDatabasePathToDefaultAsync();

        File.ReadAllText(ConfigPath).Should().Be(@"\\fileserver\share\iccard.db",
            "認証なしで職員 0 人の既定の DB へ戻せる経路を作らない");
        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
        _viewModel.StatusMessage.Should().Be(SettingsViewModel.ResetDatabaseUnavailableMessage);
        _viewModel.IsStatusError.Should().BeTrue();
    }

    /// <summary>
    /// 迂回の対: DB を開けて職員がいれば、同じ「デフォルトに戻す」は認証したうえで行える（断るのは DB を開けないときだけ）。
    /// </summary>
    [Fact]
    public async Task DBを開けて職員がいれば_デフォルトに戻すは認証したうえで行えること()
    {
        ArrangeAuthenticationSucceeds();
        File.WriteAllText(ConfigPath, @"\\fileserver\share\iccard.db");

        await _viewModel.ResetDatabasePathToDefaultAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("データベース保存先をデフォルトに戻す"), Times.Once);
        File.Exists(ConfigPath).Should().BeFalse();
        (await GetSettingLogsAsync()).Single().OperatorIdm.Should().Be(AuthenticatedStaffIdm);
    }

    [Fact]
    public async Task 現在のDBを開けないときは_別の場所への変更は認証なしで許さないこと()
    {
        await OpenAsync();
        _staffRepositoryMock.Setup(r => r.HasActiveStaffAsync())
            .ThrowsAsync(new InvalidOperationException("共有フォルダーに接続できない（テスト）"));
        File.WriteAllText(ConfigPath, @"C:\old\iccard.db");
        _viewModel.DatabasePath = _newDatabaseFolder;

        await _viewModel.SaveAsync();

        File.ReadAllText(ConfigPath).Should().Be(@"C:\old\iccard.db");
        _bulkSettingsRepositoryMock.Verify(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()), Times.Never);
        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
        _viewModel.StatusMessage.Should().Be(SettingsViewModel.SecuritySettingsDatabaseUnavailableMessage);
    }

    #endregion

    #region 検証・確認の順序と部分的な失敗（レビュー指摘 should）

    [Fact]
    public async Task 不正なデータベース保存先なら_他の設定も保存しないこと()
    {
        await OpenAsync();
        _viewModel.WarningBalance = 3000;
        _viewModel.DatabasePath = "relative\\path";

        await _viewModel.SaveAsync();

        _bulkSettingsRepositoryMock.Verify(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()), Times.Never,
            "検証は保存の前に行う（以前は他の設定を保存した後で検証していた）");
        _viewModel.FirstErrorField.Should().Be(nameof(SettingsViewModel.DatabasePath));
        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task 未作成フォルダーの確認でいいえなら_認証を求めず何も保存しないこと()
    {
        await OpenAsync();
        ArrangeAuthenticationSucceeds();
        _dialogServiceMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
        _viewModel.BackupPath = _newBackupFolder;
        _viewModel.DatabasePath = Path.Combine(_workDirectory, "not-yet-created");

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never,
            "確認は認証の前（中止するなら認証させない）");
        (await _realSettingsRepository.GetAsync(SettingsRepository.KeyBackupPath)).Should().Be(OldBackupPath);
        _bulkSettingsRepositoryMock.Verify(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()), Times.Never);
        File.Exists(ConfigPath).Should().BeFalse();
    }

    [Fact]
    public async Task バックアップ先の変更後にデータベース保存先の書き込みが失敗したら_バックアップ先は変更済みと案内すること()
    {
        await OpenAsync();
        ArrangeAuthenticationSucceeds();
        // 設定ファイルの場所に同名のフォルダーを置き、書き込みを失敗させる
        Directory.CreateDirectory(ConfigPath);
        _viewModel.BackupPath = _newBackupFolder;
        _viewModel.DatabasePath = _newDatabaseFolder;

        await _viewModel.SaveAsync();

        _viewModel.StatusMessage.Should().StartWith("バックアップ先の変更は保存しました。");
        _viewModel.StatusMessage.Should().Contain("ほかの設定（文字サイズ・残額警告など）は保存していません。");
        _viewModel.StatusMessage.Should().EndWith("もう一度「保存」を押してください。");
        _viewModel.IsStatusError.Should().BeTrue();
        (await _realSettingsRepository.GetAsync(SettingsRepository.KeyBackupPath))
            .Should().Be(PathValidator.NormalizePath(_newBackupFolder));
        _bulkSettingsRepositoryMock.Verify(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()), Times.Never);
    }

    [Fact]
    public async Task ほかの設定の保存に失敗したら_変えた方だけを名指しし再起動を案内して行動指示で終えること()
    {
        await OpenAsync();
        ArrangeAuthenticationSucceeds();
        _viewModel.DatabasePath = _newDatabaseFolder;

        await _viewModel.SaveAsync();

        var message = _viewModel.StatusMessage;
        message.Should().StartWith("データベース保存先は変更しましたが、");
        message.Should().NotContain("バックアップ先", "変更していない方を名指ししない");
        message.Should().Contain("再起動");
        message.Should().EndWith("もう一度「保存」を押してください。");
    }

    [Fact]
    public async Task バックアップ先だけを変えて他の設定の保存に失敗したら_再起動を案内しないこと()
    {
        await OpenAsync();
        ArrangeAuthenticationSucceeds();
        _viewModel.BackupPath = _newBackupFolder;

        await _viewModel.SaveAsync();

        _viewModel.StatusMessage.Should().StartWith("バックアップ先は変更しましたが、");
        _viewModel.StatusMessage.Should().NotContain("再起動");
        _viewModel.StatusMessage.Should().NotContain("データベース保存先");
    }

    #endregion

    #region 正規化すると変化しない入力（レビュー指摘 nit）

    [Fact]
    public async Task 正規化すると同じバックアップ先なら_認証も記録もしないこと()
    {
        await _realSettingsRepository.SetAsync(SettingsRepository.KeyBackupPath, _newBackupFolder);
        _bulkSettingsRepositoryMock
            .Setup(r => r.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { WarningBalance = 10000, BackupPath = _newBackupFolder });
        await _viewModel.LoadSettingsAsync();
        _viewModel.BackupPath = _newBackupFolder + "\\";

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
        (await GetSettingLogsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task 空白だけのバックアップ先は検証で弾くこと()
    {
        await OpenAsync();
        _viewModel.BackupPath = "   ";

        await _viewModel.SaveAsync();

        _viewModel.FirstErrorField.Should().Be(nameof(SettingsViewModel.BackupPath));
        _bulkSettingsRepositoryMock.Verify(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()), Times.Never);
        (await _realSettingsRepository.GetAsync(SettingsRepository.KeyBackupPath)).Should().Be(OldBackupPath);
    }

    /// <summary>
    /// レビュー 3 回目: AppConfigFiles.Read は読めないファイルにも空文字を返す。存在するが読めない設定ファイルを
    /// 「既に既定」とみなすと、削除しないまま「デフォルトに戻しました」と案内してしまう。
    /// </summary>
    [Fact]
    public async Task 読めない設定ファイルでデフォルトに戻すは_既定とみなさず失敗を案内すること()
    {
        ArrangeAuthenticationSucceeds();
        File.WriteAllText(ConfigPath, @"\\fileserver\share\iccard.db");

        using (new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await _viewModel.ResetDatabasePathToDefaultAsync();
        }

        File.Exists(ConfigPath).Should().BeTrue();
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().NotContain("デフォルトに戻しました");
        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("データベース保存先をデフォルトに戻す"), Times.Once,
            "既定とはみなさず、変更ありとして進める");
    }

    /// <summary>
    /// レビュー 4 回目（should）: 読めない設定ファイルを空文字として読むと、DB 保存先の欄を空にした保存が
    /// 「"" == ""（変更なし）」になり、ファイルは共有 DB を指したまま「設定を保存しました」で閉じていた。
    /// 「読めない設定ファイルでデフォルトに戻すは…」の対（保存の経路）。
    /// </summary>
    [Fact]
    public async Task 読めない設定ファイルで保存先を空にして保存すると_変更なしとみなさず失敗を案内すること()
    {
        ArrangeAuthenticationSucceeds();
        File.WriteAllText(ConfigPath, @"\\fileserver\share\iccard.db");
        var viewModel = CreateViewModel();
        viewModel.DatabasePath = string.Empty;

        using (new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await viewModel.SaveAsync();
        }

        File.ReadAllText(ConfigPath).Should().Be(@"\\fileserver\share\iccard.db");
        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Once,
            "既定とはみなさず、変更ありとして進める");
        viewModel.IsStatusError.Should().BeTrue();
        viewModel.IsSaved.Should().BeFalse("ファイルを書き換えられていないのに保存済みとして閉じない");
        _bulkSettingsRepositoryMock.Verify(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()), Times.Never);
    }

    /// <summary>
    /// レビュー 4 回目（nit 3）: 空白・改行だけの設定ファイルは既定の保存先を指す。「デフォルトに戻す」で既定 → 既定を記録しない。
    /// </summary>
    [Fact]
    public async Task 空白だけの設定ファイルでデフォルトに戻すは_既定のままなので認証も記録もしないこと()
    {
        File.WriteAllText(ConfigPath, "  \r\n");

        await _viewModel.ResetDatabasePathToDefaultAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
        (await GetSettingLogsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// レビュー 4 回目（nit 1）: バックアップ先が未設定のとき欄には既定のパスが出る。欄を空にしても既定の場所のままなので、
    /// 変更とみなして認証を求めない（サービス側も "" と "" で何もしない）。
    /// </summary>
    [Fact]
    public async Task バックアップ先が未設定のとき欄を空にして保存しても_認証も記録もしないこと()
    {
        _bulkSettingsRepositoryMock
            .Setup(r => r.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { WarningBalance = 10000, BackupPath = PathValidator.GetDefaultBackupPath() });
        await _viewModel.LoadSettingsAsync();
        _viewModel.BackupPath = string.Empty;

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
        (await GetSettingLogsAsync()).Should().BeEmpty();
        _bulkSettingsRepositoryMock.Verify(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()), Times.Once);
    }

    [Fact]
    public async Task 空の設定ファイルでデフォルトに戻すは_既定のままなので認証も記録もしないこと()
    {
        File.WriteAllText(ConfigPath, string.Empty);

        await _viewModel.ResetDatabasePathToDefaultAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
        (await GetSettingLogsAsync()).Should().BeEmpty("「既定 → 既定」を記録しない");
    }

    #endregion

    #region 文言

    [Fact]
    public void 認証が完了しなかったときの案内は3要素を含み行動指示で終わること()
    {
        foreach (var message in new[]
                 {
                     SettingsViewModel.SaveAuthenticationCancelledMessage,
                     SettingsViewModel.ResetAuthenticationCancelledMessage,
                     SettingsViewModel.SecuritySettingsDatabaseUnavailableMessage,
                     SettingsViewModel.ResetDatabaseUnavailableMessage
                 })
        {
            message.Should().Contain("職員証の認証");
            message.Should().Contain("していません", "何が起きなかったか（何も変更していない）を述べる");
            message.Should().EndWith("してください。");
            message.Length.Should().BeGreaterThan(20);
        }
    }

    #endregion

    private async Task<List<OperationLog>> GetSettingLogsAsync()
    {
        var backup = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.Settings, OperationLogger.SettingNames.BackupPath);
        var database = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.Settings, OperationLogger.SettingNames.DatabasePath);
        return backup.Concat(database).ToList();
    }

    private static string? ReadValue(string? json)
    {
        json.Should().NotBeNull();
        using var doc = JsonDocument.Parse(json!);
        return doc.RootElement.GetProperty("Value").GetString();
    }
}
