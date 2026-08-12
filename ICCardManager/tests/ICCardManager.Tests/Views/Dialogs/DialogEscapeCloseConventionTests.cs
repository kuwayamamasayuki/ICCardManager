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
/// <para>
/// 未保存変更の破棄確認を OnClosing で行うダイアログ（LedgerDetailDialog、Issue #1743）は
/// IsCancel を使えない。IsCancel は Click 処理の後に無条件で DialogResult=false を設定するため
/// 確認で「いいえ」を選んでも閉じてしまい、Closing をキャンセルしても DialogResult が false の
/// まま残って以後の Escape が無反応になる。この場合は Escape を
/// <c>KeyBinding</c>（→ ViewModel のクローズ要求コマンド → <c>Window.Close()</c>）で配線し、
/// OnClosing の確認を通す。本テストはどちらの手段でも「Esc で閉じられる」規約を満たすとみなす
/// （配線の正しさは <c>LedgerDetailDialogCloseGuardTests</c> が個別に固定する）。
/// </para>
/// </remarks>
public class DialogEscapeCloseConventionTests
{
    private static readonly string ViewsDirectory = ResolveViewsDirectory();

    private static string DialogsDirectory => Path.Combine(ViewsDirectory, "Dialogs");

    /// <summary>
    /// Views/Dialogs 配下の各ダイアログ XAML は、Esc キーで閉じられるよう
    /// <c>IsCancel="True"</c> を宣言したボタン、または Escape の <c>KeyBinding</c>
    /// （未保存変更ガードを持つダイアログ用、Issue #1743）のいずれかを持たなければならない。
    /// </summary>
    [Fact]
    public void 全ダイアログがEscで閉じる手段を宣言していること()
    {
        var dialogs = Directory.GetFiles(DialogsDirectory, "*.xaml", SearchOption.TopDirectoryOnly);
        dialogs.Should().NotBeEmpty("Views/Dialogs 配下にダイアログ XAML が存在するはず");

        var violations = new List<string>();

        foreach (var path in dialogs)
        {
            var xaml = File.ReadAllText(path);
            var hasIsCancel = Regex.IsMatch(xaml, @"IsCancel\s*=\s*""True""");
            var hasEscapeKeyBinding = Regex.IsMatch(xaml, @"<KeyBinding\s+Key\s*=\s*""Escape""");
            if (!hasIsCancel && !hasEscapeKeyBinding)
            {
                violations.Add(Path.GetFileName(path));
            }
        }

        violations.Should().BeEmpty(
            "すべてのダイアログは Esc キーで閉じられること（Issue #1615）。通常は IsCancel=\"True\" ボタン、" +
            "未保存変更ガードを OnClosing に持つダイアログは Escape の KeyBinding を用いる（Issue #1743）。" +
            "独自の Window_KeyDown Esc ハンドラは用いない。" +
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
