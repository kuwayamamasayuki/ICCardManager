using System.IO;
using System.Security;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services;

/// <summary>
/// バックアップサービス
/// </summary>
public class BackupService
{
    private readonly DbContext _dbContext;
    private readonly ISettingsRepository _settingsRepository;
    private readonly ILogger<BackupService> _logger;

    /// <summary>
    /// バックアップファイル保持世代数
    /// </summary>
    private const int MaxBackupGenerations = 30;

    /// <summary>
    /// バックアップファイル名のプレフィックス
    /// </summary>
    private const string BackupFilePrefix = "backup_";

    /// <summary>
    /// バックアップファイルの拡張子
    /// </summary>
    private const string BackupFileExtension = ".db";

    public BackupService(
        DbContext dbContext,
        ISettingsRepository settingsRepository,
        ILogger<BackupService> logger)
    {
        _dbContext = dbContext;
        _settingsRepository = settingsRepository;
        _logger = logger;
    }

    /// <summary>
    /// 自動バックアップを実行
    /// </summary>
    /// <returns>作成されたバックアップファイルのパス（失敗時はnull）</returns>
    public async Task<string?> ExecuteAutoBackupAsync()
    {
        string? backupPath = null;

        try
        {
            // バックアップ先フォルダを取得
            var settings = await _settingsRepository.GetAppSettingsAsync();
            backupPath = settings.BackupPath;

            if (string.IsNullOrWhiteSpace(backupPath))
            {
                backupPath = PathValidator.GetDefaultBackupPath();
                _logger.LogDebug("バックアップパス未設定のためデフォルトを使用: {Path}", backupPath);
            }
            else
            {
                // パスを検証
                var validationResult = PathValidator.ValidateBackupPath(backupPath);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning(
                        "バックアップパスが無効です: {Path} - {Error}。デフォルトパスを使用します",
                        backupPath,
                        validationResult.ErrorMessage);
                    backupPath = PathValidator.GetDefaultBackupPath();
                }
            }

            // パスを正規化
            backupPath = PathValidator.NormalizePath(backupPath) ?? PathValidator.GetDefaultBackupPath();

            // バックアップフォルダを作成
            Directory.CreateDirectory(backupPath);

            // バックアップファイル名を生成
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupFileName = $"{BackupFilePrefix}{timestamp}{BackupFileExtension}";
            var backupFilePath = Path.Combine(backupPath, backupFileName);

            // DBファイルをコピー
            var sourcePath = _dbContext.DatabasePath;
            File.Copy(sourcePath, backupFilePath, overwrite: true);

            _logger.LogInformation("バックアップを作成しました: {Path}", backupFilePath);

            // 古いバックアップを削除
            await CleanupOldBackupsAsync(backupPath);

            return backupFilePath;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "自動バックアップに失敗しました（アクセス権限エラー）: {Path}", backupPath);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "自動バックアップに失敗しました（I/Oエラー）: {Path}", backupPath);
            return null;
        }
        catch (SecurityException ex)
        {
            _logger.LogError(ex, "自動バックアップに失敗しました（セキュリティエラー）: {Path}", backupPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "自動バックアップに失敗しました（予期しないエラー）");
            return null;
        }
    }

    /// <summary>
    /// 指定したパスにバックアップを作成
    /// </summary>
    /// <param name="backupFilePath">バックアップファイルのパス</param>
    /// <returns>成功時はtrue、失敗時はfalse</returns>
    public bool CreateBackup(string backupFilePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(backupFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                // ディレクトリパスを検証
                var validationResult = PathValidator.ValidateBackupPath(directory);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning(
                        "バックアップ先ディレクトリが無効です: {Path} - {Error}",
                        directory,
                        validationResult.ErrorMessage);
                    return false;
                }

                Directory.CreateDirectory(directory);
            }

            var sourcePath = _dbContext.DatabasePath;
            File.Copy(sourcePath, backupFilePath, overwrite: true);

            _logger.LogInformation("バックアップを作成しました: {Path}", backupFilePath);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex,
                "バックアップ作成に失敗しました（アクセス権限エラー）: {Path}, Source={Source}",
                backupFilePath,
                _dbContext.DatabasePath);
            return false;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex,
                "バックアップ作成に失敗しました（I/Oエラー）: {Path}, Source={Source}",
                backupFilePath,
                _dbContext.DatabasePath);
            return false;
        }
        catch (SecurityException ex)
        {
            _logger.LogError(ex,
                "バックアップ作成に失敗しました（セキュリティエラー）: {Path}",
                backupFilePath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "バックアップ作成に失敗しました（予期しないエラー）: {Path}",
                backupFilePath);
            return false;
        }
    }

    /// <summary>
    /// バックアップからリストア
    /// </summary>
    /// <param name="backupFilePath">リストアするバックアップファイルのパス</param>
    public bool RestoreFromBackup(string backupFilePath)
    {
        var targetPath = _dbContext.DatabasePath;
        var tempPath = targetPath + ".temp";

        try
        {
            if (!File.Exists(backupFilePath))
            {
                _logger.LogWarning(
                    "リストア対象のバックアップファイルが存在しません: {Path}",
                    backupFilePath);
                return false;
            }

            // 現在のDBを退避
            if (File.Exists(targetPath))
            {
                File.Move(targetPath, tempPath, overwrite: true);
            }

            try
            {
                File.Copy(backupFilePath, targetPath, overwrite: true);
                // 成功したら退避ファイルを削除
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
                _logger.LogInformation(
                    "バックアップからリストアしました: {BackupPath} -> {TargetPath}",
                    backupFilePath,
                    targetPath);
                return true;
            }
            catch (Exception ex)
            {
                // 失敗したら退避ファイルを戻す
                _logger.LogWarning(ex,
                    "リストアに失敗したため、元のデータベースを復元します: {TempPath} -> {TargetPath}",
                    tempPath,
                    targetPath);
                if (File.Exists(tempPath))
                {
                    File.Move(tempPath, targetPath, overwrite: true);
                }
                throw;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex,
                "リストアに失敗しました（アクセス権限エラー）: {BackupPath} -> {TargetPath}",
                backupFilePath,
                targetPath);
            return false;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex,
                "リストアに失敗しました（I/Oエラー）: {BackupPath} -> {TargetPath}",
                backupFilePath,
                targetPath);
            return false;
        }
        catch (SecurityException ex)
        {
            _logger.LogError(ex,
                "リストアに失敗しました（セキュリティエラー）: {BackupPath}",
                backupFilePath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "リストアに失敗しました（予期しないエラー）: {BackupPath}",
                backupFilePath);
            return false;
        }
    }

    /// <summary>
    /// バックアップファイル一覧を取得
    /// </summary>
    public async Task<IEnumerable<BackupFileInfo>> GetBackupFilesAsync()
    {
        var settings = await _settingsRepository.GetAppSettingsAsync();
        var backupPath = settings.BackupPath;

        if (string.IsNullOrWhiteSpace(backupPath))
        {
            backupPath = PathValidator.GetDefaultBackupPath();
        }
        else
        {
            // パスを検証
            var validationResult = PathValidator.ValidateBackupPath(backupPath);
            if (!validationResult.IsValid)
            {
                _logger.LogWarning(
                    "バックアップパスが無効です: {Path} - {Error}。デフォルトパスを使用します",
                    backupPath,
                    validationResult.ErrorMessage);
                backupPath = PathValidator.GetDefaultBackupPath();
            }
        }

        if (!Directory.Exists(backupPath))
        {
            return Enumerable.Empty<BackupFileInfo>();
        }

        return Directory.GetFiles(backupPath, $"{BackupFilePrefix}*{BackupFileExtension}")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTime)
            .Select(f => new BackupFileInfo
            {
                FilePath = f.FullName,
                FileName = f.Name,
                CreatedAt = f.CreationTime,
                FileSize = f.Length
            });
    }

    /// <summary>
    /// 古いバックアップを削除
    /// </summary>
    private Task CleanupOldBackupsAsync(string backupPath)
    {
        return Task.Run(() =>
        {
            var backupFiles = Directory.GetFiles(backupPath, $"{BackupFilePrefix}*{BackupFileExtension}")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .ToList();

            // 保持世代数を超えるファイルを削除
            if (backupFiles.Count > MaxBackupGenerations)
            {
                var filesToDelete = backupFiles.Skip(MaxBackupGenerations);
                foreach (var file in filesToDelete)
                {
                    try
                    {
                        file.Delete();
                        _logger.LogDebug("古いバックアップファイルを削除しました: {Path}", file.FullName);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        // 削除に失敗しても続行（クリーンアップは最善努力）
                        _logger.LogWarning(ex,
                            "古いバックアップファイルの削除に失敗しました（アクセス権限エラー）: {Path}",
                            file.FullName);
                    }
                    catch (IOException ex)
                    {
                        // 削除に失敗しても続行（ファイルが使用中など）
                        _logger.LogWarning(ex,
                            "古いバックアップファイルの削除に失敗しました（I/Oエラー）: {Path}",
                            file.FullName);
                    }
                    catch (Exception ex)
                    {
                        // 予期しないエラーでも続行
                        _logger.LogWarning(ex,
                            "古いバックアップファイルの削除に失敗しました: {Path}",
                            file.FullName);
                    }
                }
            }
        });
    }

}

/// <summary>
/// バックアップファイル情報
/// </summary>
public class BackupFileInfo
{
    /// <summary>
    /// ファイルパス
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// ファイル名
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// 作成日時
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// ファイルサイズ（バイト）
    /// </summary>
    public long FileSize { get; set; }
}
