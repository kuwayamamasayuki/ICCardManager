using System.IO;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;

namespace ICCardManager.Services;

/// <summary>
/// バックアップサービス
/// </summary>
public class BackupService
{
    private readonly DbContext _dbContext;
    private readonly ISettingsRepository _settingsRepository;

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

    public BackupService(DbContext dbContext, ISettingsRepository settingsRepository)
    {
        _dbContext = dbContext;
        _settingsRepository = settingsRepository;
    }

    /// <summary>
    /// 自動バックアップを実行
    /// </summary>
    /// <returns>作成されたバックアップファイルのパス（失敗時はnull）</returns>
    public async Task<string?> ExecuteAutoBackupAsync()
    {
        try
        {
            // バックアップ先フォルダを取得
            var settings = await _settingsRepository.GetAppSettingsAsync();
            var backupPath = settings.BackupPath;

            if (string.IsNullOrWhiteSpace(backupPath))
            {
                backupPath = GetDefaultBackupPath();
            }

            // バックアップフォルダを作成
            Directory.CreateDirectory(backupPath);

            // バックアップファイル名を生成
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupFileName = $"{BackupFilePrefix}{timestamp}{BackupFileExtension}";
            var backupFilePath = Path.Combine(backupPath, backupFileName);

            // DBファイルをコピー
            var sourcePath = _dbContext.DatabasePath;
            File.Copy(sourcePath, backupFilePath, overwrite: true);

            // 古いバックアップを削除
            await CleanupOldBackupsAsync(backupPath);

            return backupFilePath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 指定したパスにバックアップを作成
    /// </summary>
    /// <param name="backupFilePath">バックアップファイルのパス</param>
    public bool CreateBackup(string backupFilePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(backupFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var sourcePath = _dbContext.DatabasePath;
            File.Copy(sourcePath, backupFilePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// バックアップからリストア
    /// </summary>
    /// <param name="backupFilePath">リストアするバックアップファイルのパス</param>
    public bool RestoreFromBackup(string backupFilePath)
    {
        try
        {
            if (!File.Exists(backupFilePath))
            {
                return false;
            }

            var targetPath = _dbContext.DatabasePath;

            // 現在のDBを退避
            var tempPath = targetPath + ".temp";
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
                return true;
            }
            catch
            {
                // 失敗したら退避ファイルを戻す
                if (File.Exists(tempPath))
                {
                    File.Move(tempPath, targetPath, overwrite: true);
                }
                throw;
            }
        }
        catch
        {
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
            backupPath = GetDefaultBackupPath();
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
                    }
                    catch
                    {
                        // 削除に失敗しても続行
                    }
                }
            }
        });
    }

    /// <summary>
    /// デフォルトのバックアップパスを取得
    /// </summary>
    private static string GetDefaultBackupPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ICCardManager",
            "backup");
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
