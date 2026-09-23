using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
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
/// <para>
/// <b>代替手段は「Escape の KeyBinding があること」では認めない。</b> それだけを見ると、
/// 検索条件のクリア等に Escape を割り当てたダイアログが IsCancel を外しても検査が通り、
/// 「Esc で閉じられる」規約が静かに破れる（緩和は 1 ダイアログのためでも、検査は母集団全体に効く）。
/// KeyBinding の <c>Command</c> がクローズを要求するコマンド（名前に Close を含む）へ
/// 束縛されていることまで確かめる。
/// </para>
/// <para>
/// <b>「一覧＋編集フォーム」型のダイアログ（CardManage / StaffManage / TransferStationGroup）は
/// IsCancel を持たない</b>（Issue #2080）。IsCancel は編集中かどうかに関わらず Escape で閉じるため、
/// 入力途中の内容が確認なしで失われる。これらは Window の <c>KeyDown</c> で Escape を拾い、
/// <c>EditFormKeyPolicy.HandleEscape</c> が「編集中ならフォームを閉じる／そうでなければダイアログを閉じる」を決める。
/// 本テストはこれを 3 つ目の手段として認める（IsCancel を戻す方向へ修正者を誘導しない）。
/// IsCancel を持たないことと委譲の結線は <c>EditFormKeyboardConventionTests</c> が個別に固定する。
/// </para>
/// <para>
/// Issue #2102: 照合の前に XAML コメントとコードビハインドのコメントを除く。以前はコメントを除いておらず、
/// 上の 3 画面は「Issue #2080: IsCancel="True" は付けない。」という<b>付けない理由を書いたコメント</b>に一致して
/// 合格していた（極性の反転、#1692）。コメントを言い換えると赤になり、#2080 に反して IsCancel を戻す方向へ誘導していた。
/// </para>
/// </remarks>
public class DialogEscapeCloseConventionTests
{
    private static string DialogsDirectory => ViewSourceLocator.ResolveDirectory(Path.Combine("Views", "Dialogs"));

    /// <summary>ダイアログが Esc で閉じる手段。</summary>
    public enum EscapeCloseMeans
    {
        /// <summary>手段が見つからない。</summary>
        None,

        /// <summary><c>IsCancel="True"</c> のボタン（Issue #1615）。</summary>
        IsCancel,

        /// <summary>クローズ要求コマンドへ束縛した Escape の <c>KeyBinding</c>（Issue #1743）。</summary>
        CloseKeyBinding,

        /// <summary>Window の <c>KeyDown</c> から <c>EditFormKeyPolicy.HandleEscape</c> への委譲（Issue #2080）。</summary>
        EditFormKeyPolicy,
    }

    /// <summary>
    /// Views/Dialogs 配下の各ダイアログ XAML は、Esc キーで閉じる手段を 1 つ以上持たなければならない。
    /// </summary>
    [Fact]
    public void 全ダイアログがEscで閉じる手段を宣言していること()
    {
        var dialogs = Directory.GetFiles(DialogsDirectory, "*.xaml", SearchOption.TopDirectoryOnly);
        dialogs.Should().NotBeEmpty("Views/Dialogs 配下にダイアログ XAML が存在するはず");

        var violations = dialogs
            .Where(path => DetectEscapeCloseMeans(File.ReadAllText(path), ReadCodeBehind(path)).Count == 0)
            .Select(Path.GetFileName)
            .ToList();

        violations.Should().BeEmpty(
            "すべてのダイアログは Esc キーで閉じられること（Issue #1615）。通常は IsCancel=\"True\" ボタン、" +
            "未保存変更ガードを OnClosing に持つダイアログは Escape の KeyBinding をクローズ要求コマンドへ" +
            "束縛して用いる（Issue #1743）。編集フォームを持つダイアログは Window の KeyDown から " +
            "EditFormKeyPolicy.HandleEscape へ委ねる（Issue #2080）。" +
            "違反ダイアログ: " + string.Join(", ", violations));
    }

    /// <summary>
    /// 検査ロジック自体をサンプル入力で固定する（空振り防止）。
    /// </summary>
    /// <remarks>
    /// 実データ（現状は LedgerDetailDialog ただ 1 件）だけで確かめると、そのダイアログが
    /// IsCancel へ戻ったときに代替手段の検査が誰にも検証されないまま残る。
    /// </remarks>
    [Theory]
    [InlineData(@"<KeyBinding Key=""Escape"" Command=""{Binding RequestCloseCommand}""/>", true)]
    [InlineData(@"<KeyBinding Command=""{Binding CloseCommand}"" Key=""Escape""/>", true)]
    [InlineData(@"<KeyBinding Key=""Escape"" Command=""{Binding ClearFilterCommand}""/>", false)]
    [InlineData(@"<KeyBinding Key=""Escape""/>", false)]
    [InlineData(@"<KeyBinding Key=""S"" Modifiers=""Control"" Command=""{Binding CloseCommand}""/>", false)]
    public void Escape代替手段の判定がバインド先を見ていること(string xaml, bool expected)
    {
        DetectEscapeCloseMeans(xaml, string.Empty).Contains(EscapeCloseMeans.CloseKeyBinding).Should().Be(expected,
            "Escape の KeyBinding は、クローズを要求するコマンドへ束縛されている場合にのみ" +
            "IsCancel の代替として認める（Issue #1743）");
    }

    /// <summary>
    /// 手段の判定がコメントを見ず、実在する属性と結線だけを数えることをサンプル入力で固定する（Issue #2102）。
    /// </summary>
    [Theory]
    [InlineData(@"<Window><!-- IsCancel=""True"" は付けない --><Button Content=""完了""/></Window>", "", EscapeCloseMeans.None)]
    [InlineData(@"<Window><Button Content=""閉じる"" IsCancel=""True""/></Window>", "", EscapeCloseMeans.IsCancel)]
    [InlineData(@"<Window><!-- <KeyBinding Key=""Escape"" Command=""{Binding CloseCommand}""/> --></Window>", "", EscapeCloseMeans.None)]
    [InlineData(@"<Window KeyDown=""Dialog_KeyDown""></Window>",
        "private void Dialog_KeyDown(object sender, KeyEventArgs e)\n{\n    EditFormKeyPolicy.HandleEscape(this, _viewModel, e);\n}",
        EscapeCloseMeans.EditFormKeyPolicy)]
    [InlineData(@"<Window KeyDown=""Dialog_KeyDown""></Window>",
        "private void Dialog_KeyDown(object sender, KeyEventArgs e)\n{\n    // EditFormKeyPolicy.HandleEscape(this, _viewModel, e);\n}",
        EscapeCloseMeans.None)]
    [InlineData(@"<Window></Window>",
        "private void Dialog_KeyDown(object sender, KeyEventArgs e)\n{\n    EditFormKeyPolicy.HandleEscape(this, _viewModel, e);\n}",
        EscapeCloseMeans.None)]
    // WPF の bool 変換は大文字小文字を区別しない（Issue #2102 のコードレビュー: 小文字の true を誤検出していた）
    [InlineData(@"<Window><Button Content=""閉じる"" IsCancel=""true""/></Window>", "", EscapeCloseMeans.IsCancel)]
    [InlineData(@"<Window><Button Content=""閉じる"" IsCancel=""False""/></Window>", "", EscapeCloseMeans.None)]
    // Window の PreviewKeyDown から委ねる形も認める
    [InlineData(@"<Window PreviewKeyDown=""Dialog_PreviewKeyDown""></Window>",
        "private void Dialog_PreviewKeyDown(object sender, KeyEventArgs e)\n{\n    EditFormKeyPolicy.HandleEscape(this, _viewModel, e);\n}",
        EscapeCloseMeans.EditFormKeyPolicy)]
    // 子要素の PreviewKeyDown は Window の Escape を拾わないので認めない
    [InlineData(@"<Window><TextBox PreviewKeyDown=""Dialog_PreviewKeyDown""/></Window>",
        "private void Dialog_PreviewKeyDown(object sender, KeyEventArgs e)\n{\n    EditFormKeyPolicy.HandleEscape(this, _viewModel, e);\n}",
        EscapeCloseMeans.None)]
    public void Esc手段の判定がコメントを数えず結線を見ていること(string xaml, string codeBehind, EscapeCloseMeans expected)
    {
        var means = DetectEscapeCloseMeans(xaml, codeBehind);

        if (expected == EscapeCloseMeans.None)
        {
            means.Should().BeEmpty(
                "コメントに書かれた字句・XAML から呼ばれないハンドラーは Esc で閉じる手段にならない（Issue #2102）");
        }
        else
        {
            means.Should().Equal(new[] { expected });
        }
    }

    /// <summary>
    /// Issue #1615 で名指しされた 3 ダイアログ（CardManage / StaffManage / VirtualCard）が
    /// Esc で閉じる手段を確実に持つことを個別に固定する。汎用テストの母集合が将来変わっても、
    /// 本 Issue が是正した対象の回帰を直接検出するためのピンポイント検証。
    /// </summary>
    /// <remarks>
    /// CardManage / StaffManage は Issue #2080 で IsCancel から EditFormKeyPolicy への委譲に移った。
    /// 期待する手段を名指しするので、コメントの字句で合格することも、IsCancel を戻して合格することもない。
    /// </remarks>
    [Theory]
    [InlineData("CardManageDialog.xaml", EscapeCloseMeans.EditFormKeyPolicy)]
    [InlineData("StaffManageDialog.xaml", EscapeCloseMeans.EditFormKeyPolicy)]
    [InlineData("VirtualCardDialog.xaml", EscapeCloseMeans.IsCancel)]
    public void Issue1615対象ダイアログがEscで閉じる手段を宣言していること(string fileName, EscapeCloseMeans expected)
    {
        var path = Path.Combine(DialogsDirectory, fileName);
        File.Exists(path).Should().BeTrue($"{fileName} が存在するはず");

        DetectEscapeCloseMeans(File.ReadAllText(path), ReadCodeBehind(path)).Should().Equal(new[] { expected },
            $"{fileName} は Esc で閉じられること（Issue #1615）。編集フォームを持つ画面は IsCancel ではなく " +
            "EditFormKeyPolicy に委ねる（Issue #2080）");
    }

    private static string ReadCodeBehind(string xamlPath)
        => File.Exists(xamlPath + ".cs") ? File.ReadAllText(xamlPath + ".cs") : string.Empty;

    /// <summary>
    /// ダイアログが持つ Esc で閉じる手段を、コメントを除いたうえで列挙する。
    /// </summary>
    private static IReadOnlyList<EscapeCloseMeans> DetectEscapeCloseMeans(string rawXaml, string rawCodeBehind)
    {
        var xaml = XamlElementInspection.StripXmlComments(rawXaml);
        var means = new List<EscapeCloseMeans>();

        // WPF の bool / Key の型変換は大文字小文字を区別しない（IsCancel="true" も Key="escape" も有効）。
        // 大文字の表記だけを認めると、正当な XAML が「Esc で閉じる手段なし」と誤検出される（Issue #2102 のコードレビュー）
        if (XamlElementInspection.EnumerateStartTags(xaml)
            .Any(tag => string.Equals(
                XamlElementInspection.GetPropertyAttribute(tag.StartTag, "IsCancel")?.Trim(), "True",
                StringComparison.OrdinalIgnoreCase)))
        {
            means.Add(EscapeCloseMeans.IsCancel);
        }

        if (XamlElementInspection.EnumerateElements(xaml, "KeyBinding")
            .Any(element =>
                string.Equals(XamlElementInspection.GetAttribute(element.StartTag, "Key")?.Trim(), "Escape",
                    StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(
                    XamlElementInspection.GetAttribute(element.StartTag, "Command") ?? string.Empty,
                    @"^\{Binding\s+\w*Close\w*Command\}$")))
        {
            means.Add(EscapeCloseMeans.CloseKeyBinding);
        }

        if (DelegatesEscapeToEditFormKeyPolicy(xaml, rawCodeBehind))
        {
            means.Add(EscapeCloseMeans.EditFormKeyPolicy);
        }

        return means;
    }

    /// <summary>
    /// ルートの Window が <c>KeyDown</c> / <c>PreviewKeyDown</c> で指すハンドラーが、コードビハインドで
    /// <c>EditFormKeyPolicy.HandleEscape</c> を呼んでいるか。
    /// </summary>
    /// <remarks>
    /// <c>PreviewKeyDown</c> で受けても Escape は同じく処理できる（子のコントロールより先に拾う違いだけ）。
    /// <c>KeyDown</c> だけを認めると、正当な書き方が誤検出される（Issue #2102 のコードレビュー）。
    /// </remarks>
    private static bool DelegatesEscapeToEditFormKeyPolicy(string xaml, string rawCodeBehind)
    {
        var window = XamlElementInspection.EnumerateElements(xaml, "Window").FirstOrDefault();
        if (window == null)
        {
            return false;
        }

        var code = TestSourceInspection.ToCodeOnly(rawCodeBehind);
        foreach (var handler in new[] { "KeyDown", "PreviewKeyDown" }
                     .Select(e => XamlElementInspection.GetAttribute(window.StartTag, e))
                     .Where(h => !string.IsNullOrEmpty(h)))
        {
            var signature = Regex.Match(code, $@"\bvoid\s+{Regex.Escape(handler!)}\s*\(");
            if (signature.Success
                && TestSourceInspection.ExtractMethodBody(code, signature.Value).Contains("EditFormKeyPolicy.HandleEscape("))
            {
                return true;
            }
        }

        return false;
    }
}
