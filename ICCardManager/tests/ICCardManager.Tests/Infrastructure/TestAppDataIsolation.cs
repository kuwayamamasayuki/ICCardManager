using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using ICCardManager.Common;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// 単体テストのプロセスが、開発機の本物の <c>C:\ProgramData\ICCardManager</c> に触れないようにする（Issue #2098）。
/// </summary>
/// <remarks>
/// <para>
/// 本体はアプリケーションデータ（既定の DB・バックアップの既定の保存先・<c>database_config.txt</c>・
/// エラーログ・ファイルログ）の置き場所を <see cref="AppDataPaths.RootDirectory"/> 1 か所で解決する。
/// ここでモジュール初期化子から一時フォルダーへ差し替えると、<b>どのテストよりも先に 1 回だけ</b>
/// 全経路が一時フォルダーへ向く。テストごとに退避・復元する形にしないのは、並列に走るテストの間で
/// 退避が入れ子になり、元の内容を上書きし得るため（Issue #2062「退避は起動手順の入口で 1 回」）。
/// </para>
/// <para>
/// 以前はテストを実行するだけで、既定の保存先に空の DB のバックアップが作られて本物の自動バックアップが
/// 間引かれ、<c>database_config.txt</c>（共有モードの DB パス）が削除され、エラーログへ架空の失敗が
/// 追記されていた。本クラスが呼ばれていることは <c>AppDataPathsConventionTests</c> が固定する。
/// </para>
/// </remarks>
internal static class TestAppDataIsolation
{
    /// <summary>このテストプロセスが使うアプリケーションデータの置き場所。</summary>
    internal static string RootDirectory { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void Initialize()
    {
        RootDirectory = Path.Combine(
            Path.GetTempPath(),
            "ICCardManager.Tests",
            $"appdata_{Process.GetCurrentProcess().Id}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootDirectory);

        AppDataPaths.RedirectRootDirectory(RootDirectory);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(RootDirectory);
        AppDomain.CurrentDomain.DomainUnload += (_, _) => TryDelete(RootDirectory);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // 後片付けの失敗（SQLite のファイルが解放前など）はテスト結果に影響させない。
            // 残るのは %TEMP% 配下であり、開発機の実データではない。
        }
    }
}
