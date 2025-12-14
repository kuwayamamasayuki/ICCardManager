using System.IO;
using System.Reflection;

namespace ICCardManager.Services;

/// <summary>
/// Excelテンプレートファイルのパス解決を行うヘルパークラス
/// </summary>
/// <remarks>
/// Single-file publish環境でも正しくテンプレートを取得できるよう、
/// 複数のパス解決方法をフォールバックで試行します。
/// </remarks>
public static class TemplateResolver
{
    /// <summary>
    /// 物品出納簿テンプレートの相対パス
    /// </summary>
    private const string TemplateRelativePath = "Resources/Templates/物品出納簿テンプレート.xlsx";

    /// <summary>
    /// 埋め込みリソース名
    /// </summary>
    private const string EmbeddedResourceName = "ICCardManager.Resources.Templates.物品出納簿テンプレート.xlsx";

    /// <summary>
    /// 一時ファイルのプレフィックス
    /// </summary>
    private const string TempFilePrefix = "ICCardManager_Template_";

    /// <summary>
    /// 物品出納簿テンプレートのパスを解決
    /// </summary>
    /// <returns>テンプレートファイルのパス</returns>
    /// <exception cref="TemplateNotFoundException">テンプレートが見つからない場合</exception>
    public static string ResolveTemplatePath()
    {
        var searchedPaths = new List<string>();

        // 1. AppContext.BaseDirectory からの相対パス（推奨）
        var baseDirPath = Path.Combine(AppContext.BaseDirectory, TemplateRelativePath);
        searchedPaths.Add(baseDirPath);
        if (File.Exists(baseDirPath))
        {
            return baseDirPath;
        }

        // 2. AppDomain.CurrentDomain.BaseDirectory からの相対パス
        var domainBasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TemplateRelativePath);
        if (domainBasePath != baseDirPath)
        {
            searchedPaths.Add(domainBasePath);
            if (File.Exists(domainBasePath))
            {
                return domainBasePath;
            }
        }

        // 3. 実行アセンブリの場所からの相対パス
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        if (!string.IsNullOrEmpty(assemblyLocation))
        {
            var assemblyDir = Path.GetDirectoryName(assemblyLocation);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                var assemblyPath = Path.Combine(assemblyDir, TemplateRelativePath);
                if (assemblyPath != baseDirPath && assemblyPath != domainBasePath)
                {
                    searchedPaths.Add(assemblyPath);
                    if (File.Exists(assemblyPath))
                    {
                        return assemblyPath;
                    }
                }
            }
        }

        // 4. 実行ファイルの場所からの相対パス（Single-file publish対応）
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var processDir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(processDir))
            {
                var processBasePath = Path.Combine(processDir, TemplateRelativePath);
                if (!searchedPaths.Contains(processBasePath))
                {
                    searchedPaths.Add(processBasePath);
                    if (File.Exists(processBasePath))
                    {
                        return processBasePath;
                    }
                }
            }
        }

        // 5. 埋め込みリソースから一時ファイルに展開
        var tempPath = ExtractEmbeddedTemplate();
        if (tempPath != null)
        {
            return tempPath;
        }

        // どの方法でも見つからない場合は例外をスロー
        throw new TemplateNotFoundException(
            "物品出納簿テンプレート",
            searchedPaths,
            "テンプレートファイルが見つかりません。アプリケーションを再インストールしてください。");
    }

    /// <summary>
    /// 埋め込みリソースからテンプレートを一時ファイルに展開
    /// </summary>
    /// <returns>一時ファイルのパス。展開に失敗した場合はnull</returns>
    private static string? ExtractEmbeddedTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (stream == null)
        {
            return null;
        }

        // 一時ディレクトリに展開
        var tempDir = Path.Combine(Path.GetTempPath(), "ICCardManager");
        Directory.CreateDirectory(tempDir);

        var tempPath = Path.Combine(tempDir, $"{TempFilePrefix}{Guid.NewGuid():N}.xlsx");

        using var fileStream = File.Create(tempPath);
        stream.CopyTo(fileStream);

        return tempPath;
    }

    /// <summary>
    /// テンプレートファイルが存在するかチェック
    /// </summary>
    /// <returns>存在する場合true</returns>
    public static bool TemplateExists()
    {
        try
        {
            ResolveTemplatePath();
            return true;
        }
        catch (TemplateNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 一時展開されたテンプレートファイルをクリーンアップ
    /// </summary>
    public static void CleanupTempFiles()
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "ICCardManager");
            if (Directory.Exists(tempDir))
            {
                var files = Directory.GetFiles(tempDir, $"{TempFilePrefix}*.xlsx");
                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // 削除失敗は無視（使用中の可能性あり）
                    }
                }
            }
        }
        catch
        {
            // クリーンアップ失敗は無視
        }
    }
}

/// <summary>
/// テンプレートファイルが見つからない場合の例外
/// </summary>
public class TemplateNotFoundException : Exception
{
    /// <summary>
    /// テンプレート名
    /// </summary>
    public string TemplateName { get; }

    /// <summary>
    /// 検索したパスの一覧
    /// </summary>
    public IReadOnlyList<string> SearchedPaths { get; }

    public TemplateNotFoundException(string templateName, IEnumerable<string> searchedPaths, string message)
        : base(message)
    {
        TemplateName = templateName;
        SearchedPaths = searchedPaths.ToList().AsReadOnly();
    }

    public TemplateNotFoundException(string templateName, IEnumerable<string> searchedPaths, string message, Exception innerException)
        : base(message, innerException)
    {
        TemplateName = templateName;
        SearchedPaths = searchedPaths.ToList().AsReadOnly();
    }

    /// <summary>
    /// 検索したパスを含む詳細メッセージを取得
    /// </summary>
    public string GetDetailedMessage()
    {
        var paths = string.Join("\n  - ", SearchedPaths);
        return $"{Message}\n\n検索したパス:\n  - {paths}";
    }
}
