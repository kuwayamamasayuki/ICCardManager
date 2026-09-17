using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Reflection;
using ICCardManager.Models;

namespace ICCardManager.Services
{
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
        /// 部署種別に応じた物品出納簿テンプレートの相対パスを取得
        /// </summary>
        private static string GetTemplateRelativePath(DepartmentType departmentType)
        {
            var fileName = departmentType == DepartmentType.EnterpriseAccount
                ? "物品出納簿テンプレート（企業会計部局）.xlsx"
                : "物品出納簿テンプレート（市長事務部局）.xlsx";
            return $"Resources/Templates/{fileName}";
        }

        /// <summary>
        /// 部署種別に応じた埋め込みリソース名を取得
        /// </summary>
        private static string GetEmbeddedResourceName(DepartmentType departmentType)
        {
            var fileName = departmentType == DepartmentType.EnterpriseAccount
                ? "物品出納簿テンプレート（企業会計部局）.xlsx"
                : "物品出納簿テンプレート（市長事務部局）.xlsx";
            return $"ICCardManager.Resources.Templates.{fileName}";
        }

        /// <summary>
        /// 一時ファイルのプレフィックス
        /// </summary>
        private const string TempFilePrefix = "ICCardManager_Template_";

        /// <summary>
        /// 物品出納簿テンプレートのパスを解決（市長事務部局デフォルト）
        /// </summary>
        /// <returns>テンプレートファイルのパス</returns>
        /// <exception cref="TemplateNotFoundException">テンプレートが見つからない場合</exception>
        public static string ResolveTemplatePath()
        {
            return ResolveTemplatePath(DepartmentType.MayorOffice);
        }

        /// <summary>
        /// 部署種別に応じた物品出納簿テンプレートのパスを解決
        /// </summary>
        /// <param name="departmentType">部署種別</param>
        /// <returns>テンプレートファイルのパス</returns>
        /// <exception cref="TemplateNotFoundException">テンプレートが見つからない場合</exception>
        public static string ResolveTemplatePath(DepartmentType departmentType)
        {
            var templateRelativePath = GetTemplateRelativePath(departmentType);
            var searchedPaths = new List<string>();

            // 1. AppContext.BaseDirectory からの相対パス（推奨）
            var baseDirPath = Path.Combine(AppContext.BaseDirectory, templateRelativePath);
            searchedPaths.Add(baseDirPath);
            if (File.Exists(baseDirPath))
            {
                return baseDirPath;
            }

            // 2. AppDomain.CurrentDomain.BaseDirectory からの相対パス
            var domainBasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, templateRelativePath);
            if (domainBasePath != baseDirPath)
            {
                searchedPaths.Add(domainBasePath);
                if (File.Exists(domainBasePath))
                {
                    return domainBasePath;
                }
            }

            // 3. 実行アセンブリの場所からの相対パス
            // Single-file publish時はAssembly.Locationは空文字を返すが、
            // 既にnull/empty チェックを行っており、他のフォールバックも用意されているため安全
    #pragma warning disable IL3000 // Single-file publish時にAssembly.Locationは空を返す（想定済み）
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
    #pragma warning restore IL3000
            if (!string.IsNullOrEmpty(assemblyLocation))
            {
                var assemblyDir = Path.GetDirectoryName(assemblyLocation);
                if (!string.IsNullOrEmpty(assemblyDir))
                {
                    var assemblyPath = Path.Combine(assemblyDir, templateRelativePath);
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
            // .NET Framework 4.8ではEnvironment.ProcessPathがないためAssembly.GetEntryAssemblyを使用
            var processPath = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(processPath))
            {
                var processDir = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrEmpty(processDir))
                {
                    var processBasePath = Path.Combine(processDir, templateRelativePath);
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
            var tempPath = ExtractEmbeddedTemplate(departmentType);
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
        /// 展開済みの一時テンプレート（部署種別ごと）。<see cref="ExtractLock"/> の内側でのみ読み書きする。
        /// </summary>
        private static readonly Dictionary<DepartmentType, string> ExtractedTemplatePaths =
            new Dictionary<DepartmentType, string>();

        private static readonly object ExtractLock = new object();

        /// <summary>
        /// 埋め込みリソースからテンプレートを一時ファイルに展開
        /// </summary>
        /// <param name="departmentType">部署種別</param>
        /// <returns>一時ファイルのパス。展開に失敗した場合はnull</returns>
        /// <remarks>
        /// Issue #2050: 展開はプロセス内で部署種別ごとに 1 回だけ行い、以後は同じ一時ファイルを返す。
        /// 本メソッドに到達するのは、テンプレートがアプリケーションの配置先（Resources/Templates）に無い
        /// ときだけ（通常のインストールでは配置先のファイルが見つかる）。その場合、旧実装は呼ぶたびに新しい一時 .xlsx を作っていたため、帳票の一括作成ではカードの枚数ぶん
        /// （一括作成の事前確認 <see cref="TemplateExists(DepartmentType)"/> の分も含めて）
        /// 同じ内容のファイルが %TEMP% に積み上がり、削除は次回起動時まで行われなかった。
        /// <para>
        /// 再利用してよいのは「ファイルが残っていて、長さが埋め込みリソースと一致する」ときだけ。
        /// 一時フォルダーの掃除（<see cref="CleanupTempFiles"/> や OS・利用者による削除）で消えた、
        /// あるいは書き込みが途中で切れたファイルを返すと、帳票作成がテンプレートを開けずに失敗する。
        /// 呼び出し元は一時テンプレートを読むだけ（帳票は別の一時ファイルへ保存する）なので、
        /// 共有しても内容は書き換わらない。
        /// </para>
        /// </remarks>
        internal static string ExtractEmbeddedTemplate(DepartmentType departmentType = DepartmentType.MayorOffice)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var embeddedResourceName = GetEmbeddedResourceName(departmentType);

            using var stream = assembly.GetManifestResourceStream(embeddedResourceName);
            if (stream == null)
            {
                return null;
            }

            lock (ExtractLock)
            {
                if (ExtractedTemplatePaths.TryGetValue(departmentType, out var cachedPath)
                    && IsReusableExtraction(cachedPath, stream.Length))
                {
                    return cachedPath;
                }

                // 一時ディレクトリに展開
                var tempDir = Path.Combine(Path.GetTempPath(), "ICCardManager");
                Directory.CreateDirectory(tempDir);

                var tempPath = Path.Combine(tempDir, $"{TempFilePrefix}{Guid.NewGuid():N}.xlsx");

                try
                {
                    using (var fileStream = File.Create(tempPath))
                    {
                        stream.CopyTo(fileStream);
                    }
                }
                catch
                {
                    // 書き込みが途中で切れたファイルを残さない（残れば次回起動時の掃除まで積み上がる）
                    TryDeleteFile(tempPath);
                    throw;
                }

                // 書き込みが完了してから登録する（途中で失敗したファイルを再利用の候補にしない）
                ExtractedTemplatePaths[departmentType] = tempPath;
                return tempPath;
            }
        }

        /// <summary>
        /// 展開済みの一時テンプレートを再利用してよいか
        /// </summary>
        private static bool IsReusableExtraction(string path, long expectedLength)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length == expectedLength;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 状態を確かめられないファイルは使わず、展開し直す
                return false;
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 削除できなくても次回起動時の CleanupTempFiles が回収する
            }
        }

        /// <summary>
        /// テンプレートファイルが存在するかチェック（市長事務部局デフォルト）
        /// </summary>
        /// <returns>存在する場合true</returns>
        public static bool TemplateExists()
        {
            return TemplateExists(DepartmentType.MayorOffice);
        }

        /// <summary>
        /// 部署種別に応じたテンプレートファイルが存在するかチェック
        /// </summary>
        /// <param name="departmentType">部署種別</param>
        /// <returns>存在する場合true</returns>
        public static bool TemplateExists(DepartmentType departmentType)
        {
            try
            {
                ResolveTemplatePath(departmentType);
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
}
