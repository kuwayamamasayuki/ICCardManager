using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// Issue #1615: すべてのダイアログが Esc キーで閉じられること（キーボード操作の一貫性）を
/// 保証する XAML 規約の回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// WPF では <c>Button.IsCancel="True"</c> を宣言したボタンが Esc キー押下で Click される。
/// これがダイアログを Esc で閉じる慣用パターンであり、本リポジトリの大半のダイアログが
/// 採用している。一部のダイアログ（CardManageDialog / StaffManageDialog）はかつて
/// 独自の <c>Window_KeyDown</c> Esc ハンドラ（Issue #445）で代替していたが、Esc 処理の
/// 実装が二系統に分裂し一貫性を欠いていたため、Issue #1615 で IsCancel へ統一した。
/// </para>
/// <para>
/// 実際の Esc 押下によるクローズ挙動は WPF Window のインスタンス化と STA スレッドを要し
/// UI 自動化の領域になるため、PR テストプランで手動検証する。本テストは「閉じる手段を
/// 持つボタンが IsCancel を宣言しているか」を markup レベルで静的検証し、IsCancel 未設定の
/// ダイアログが将来再混入するのを防ぐセーフティネット（Issue #1468 で確立した静的解析方式を踏襲）。
/// </para>
/// <para>
/// DEBUG 専用の <c>VirtualCardDialog</c> も同じ規約に従う（Issue #1615 で IsCancel を付与）。
/// Release ビルドでは csproj により Compile 対象から除外されるが、ソースファイルはツリーに
/// 存在するため本テストの対象に含まれ、例外リストは不要。
/// </para>
/// </remarks>
public class DialogEscapeCloseConventionTests
{
    private static readonly string ViewsDirectory = ResolveViewsDirectory();

    private static string DialogsDirectory => Path.Combine(ViewsDirectory, "Dialogs");

    /// <summary>
    /// Views/Dialogs 配下の各ダイアログ XAML は、Esc キーで閉じられるよう
    /// <c>IsCancel="True"</c> を宣言したボタンを少なくとも 1 つ持たなければならない。
    /// </summary>
    [Fact]
    public void 全ダイアログがIsCancelボタンを宣言していること()
    {
        var dialogs = Directory.GetFiles(DialogsDirectory, "*.xaml", SearchOption.TopDirectoryOnly);
        dialogs.Should().NotBeEmpty("Views/Dialogs 配下にダイアログ XAML が存在するはず");

        var violations = new List<string>();

        foreach (var path in dialogs)
        {
            var xaml = File.ReadAllText(path);
            if (!Regex.IsMatch(xaml, @"IsCancel\s*=\s*""True"""))
            {
                violations.Add(Path.GetFileName(path));
            }
        }

        violations.Should().BeEmpty(
            "すべてのダイアログは Esc キーで閉じられるよう IsCancel=\"True\" ボタンを持つこと（Issue #1615）。" +
            "独自の Window_KeyDown Esc ハンドラではなく WPF 慣用の IsCancel を用いてキーボード操作の一貫性を保つ。" +
            "違反ダイアログ: " + string.Join(", ", violations));
    }

    /// <summary>
    /// Issue #1615 で名指しされた 3 ダイアログ（CardManage / StaffManage / VirtualCard）が
    /// 確実に IsCancel を持つことを個別に固定する。汎用テストの母集合が将来変わっても、
    /// 本 Issue が是正した対象の回帰を直接検出するためのピンポイント検証。
    /// </summary>
    [Theory]
    [InlineData("CardManageDialog.xaml")]
    [InlineData("StaffManageDialog.xaml")]
    [InlineData("VirtualCardDialog.xaml")]
    public void Issue1615対象ダイアログがIsCancelを宣言していること(string fileName)
    {
        var path = Path.Combine(DialogsDirectory, fileName);
        File.Exists(path).Should().BeTrue($"{fileName} が存在するはず");

        var xaml = File.ReadAllText(path);

        Regex.IsMatch(xaml, @"IsCancel\s*=\s*""True""").Should().BeTrue(
            $"{fileName} の閉じるボタンは IsCancel=\"True\" を宣言し Esc で閉じられること（Issue #1615）");
    }

    private static string ResolveViewsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "src", "ICCardManager", "Views");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Views ディレクトリを {AppContext.BaseDirectory} の親階層から解決できませんでした");
    }
}
