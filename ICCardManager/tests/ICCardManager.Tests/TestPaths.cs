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

    /// <summary>
    /// git の管理情報の名前（リポジトリでは<b>ディレクトリ</b>、git worktree では<b>ファイル</b>）。
    /// </summary>
    /// <remarks>
    /// テストコードでこの名前を直書きしてよいのは本クラスだけ（<see cref="TestRootResolutionConventionTests"/>、
    /// Issue #2101）。<c>Directory.Exists(".git")</c> で基点を探す実装は worktree で本体の作業ツリーを
    /// 黙って検査するため、判定を 1 か所へ寄せる。走査から除外する名前などの用途もこの定数を参照する。
    /// </remarks>
    public const string GitMarkerName = ".git";

    /// <summary>
    /// <paramref name="startDirectory"/> から祖先方向へたどり、最初に git の管理情報
    /// （ディレクトリまたは worktree のファイル）がある階層を返す。見つからなければ <c>null</c>。
    /// </summary>
    /// <remarks>
    /// ソリューションルート（<see cref="GetSolutionRoot"/>）の 1 階層上にあるリポジトリ直下の設定
    /// （<c>.editorconfig</c> 等）を読む検査のためにある。ディレクトリだけを探す形
    /// （<c>Directory.Exists</c>）にすると、リポジトリの中にある worktree（<c>.claude/worktrees/…</c>）では
    /// worktree 直下の <c>.git</c> ファイルを素通りして本体の作業ツリーへ届くため、ファイルも認める。
    /// </remarks>
    public static string? FindRepositoryRoot(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir != null; dir = dir.Parent)
        {
            var marker = Path.Combine(dir.FullName, GitMarkerName);
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}
