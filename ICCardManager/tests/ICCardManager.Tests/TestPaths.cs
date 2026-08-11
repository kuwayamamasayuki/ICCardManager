using System;
using System.IO;

namespace ICCardManager.Tests;

/// <summary>
/// ソースツリー上のパスをテスト実行ディレクトリから解決するヘルパー。
/// </summary>
/// <remarks>
/// 静的検査を行う規約テスト（<see cref="UserFacingTextConventionTests"/>、
/// <see cref="DomainBoundaryConventionTests"/> 等）は、いずれも
/// テスト実行ディレクトリから親方向に <c>ICCardManager.sln</c> を探索する同じ処理を持つ。
/// 新規の規約テストはこのヘルパーを使い、探索ロジックの複製をこれ以上増やさないこと
/// （既存クラスの各コピーの集約は別途行う）。
/// </remarks>
internal static class TestPaths
{
    /// <summary>
    /// <c>ICCardManager.sln</c> が置かれたディレクトリ（＝ソリューションルート）を返す。
    /// </summary>
    /// <remarks>
    /// git リポジトリのルート（この 1 階層上）ではないことに注意。
    /// csproj / Directory.Build.props はこの階層を基点とした相対パスで解決できる。
    /// </remarks>
    /// <exception cref="InvalidOperationException">ソリューションファイルが見つからないとき。</exception>
    public static string GetSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ICCardManager.sln")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            throw new InvalidOperationException(
                $"ICCardManager.sln が AppContext.BaseDirectory ({AppContext.BaseDirectory}) から見つからない。" +
                "テスト実行ディレクトリの構造を確認してください。");
        }

        return dir.FullName;
    }

    /// <summary>
    /// 本番ソースのルート（<c>src/ICCardManager</c>）を返す。
    /// </summary>
    public static string GetProductionSourceRoot()
        => Path.Combine(GetSolutionRoot(), "src", "ICCardManager");
}
