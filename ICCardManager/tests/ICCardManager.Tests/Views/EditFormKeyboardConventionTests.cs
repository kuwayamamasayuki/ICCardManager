using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using ICCardManager.ViewModels;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2080: 「一覧＋編集フォーム」型ダイアログのキーボード操作を固定する。
/// </summary>
/// <remarks>
/// <para>
/// カード管理・職員管理の編集フォームは「保存」に <c>IsDefault</c> が無く、Escape は
/// 「完了」（<c>IsCancel="True"</c>）へ割り当たっていた。氏名を入力して Enter を押しても保存されず、
/// 編集中に Escape を押すと確認なしで<b>入力内容ごとダイアログが閉じた</b>。
/// </para>
/// <para>
/// <b>走査対象はファイル名で列挙しない</b>（#1786）。「編集フォームの表示を <c>IsEditing</c> で
/// 切り替えるダイアログ」という<b>性質</b>から導出するので、同型の画面が増えても検査から漏れない。
/// 実際 Issue が名指ししたのは 2 件だったが、同じ欠陥は
/// <c>TransferStationGroupDialog</c>（#1905）にも残っていた — コードビハインドのコメントは
/// 「操作のたびに保存されるので未保存の状態を持たない」と述べていたが、
/// 編集フォームに入力している最中は未保存の入力を持つ。
/// <b>Issue の一覧は起票時点のスナップショットである</b>（#2075 / #2076 の再演）。
/// </para>
/// <para>
/// <b>検査できない範囲</b>（見ていないものは「見ていない」と書く）:
/// 実際に Enter で <c>SaveCommand</c> が走るか・Escape でフォームだけが閉じるかは WPF の実行時挙動であり、
/// XAML テキストからは確かめられない（<c>Window</c> は STA 依存で xUnit から生成できない）。
/// Escape の<b>判断</b>は
/// <see cref="ICCardManager.Tests.Views.Helpers.EditFormKeyPolicyTests"/> が単体テストで、
/// ここでは<b>結線</b>を固定する。実機でのキー操作は手動検証する。
/// </para>
/// <para>
/// 走査は <see cref="XamlElementInspection"/> へ集約し、私的コピーを増やさない（testing.md）。
/// </para>
/// </remarks>
public class EditFormKeyboardConventionTests
{
    /// <summary>
    /// 「編集フォームの表示を <c>IsEditing</c> で切り替える」ことを表す印。
    /// </summary>
    /// <remarks>
    /// 属性（<c>Visibility="{Binding IsEditing, …}"</c>）でも <c>Setter</c> でも同じ形で現れるため、
    /// 開始タグの属性に決め打たずバインディングのパスで照合する（#2075 のコードレビューと同じ理由）。
    /// </remarks>
    private static readonly Regex IsEditingBinding =
        new Regex(@"\{\s*Binding\s+(?:Path\s*=\s*)?IsEditing\s*[,}]", RegexOptions.Compiled);

    /// <summary>Escape を自前で処理するハンドラー名（XAML とコードビハインドで一致していること）。</summary>
    private const string EscapeHandlerName = "Dialog_KeyDown";

    /// <summary>
    /// Escape を拾うルーティングイベント。<b>バブル</b>であること（コードレビューで検出）。
    /// </summary>
    /// <remarks>
    /// <c>PreviewKeyDown</c>（トンネル）はウィンドウが最初に受け取るため、Escape を正当に消費する
    /// コントロールより先に走る。カード種別の <c>ComboBox</c> はドロップダウンを開いている間の Escape を
    /// <c>OnKeyDown</c>（バブル）で閉じるので、トンネルで拾うと「候補を開いたが選び直さずに閉じる」操作が
    /// <b>編集フォームごとの破棄</b>になる — この Issue が消そうとしている故障そのもの。
    /// </remarks>
    private const string EscapeRoutedEvent = "KeyDown";

    #region 走査対象の導出

    private sealed record EditFormDialog(string FileName, string Xaml, string CodeBehind, string ViewModelTypeName);

    private static IReadOnlyList<EditFormDialog> EnumerateEditFormDialogs()
    {
        var dialogs = new List<EditFormDialog>();

        foreach (var file in Directory
                     .EnumerateFiles(ViewSourceLocator.ResolveDirectory("Views"), "*.xaml", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var xaml = XamlElementInspection.StripXmlComments(File.ReadAllText(file));
            if (!IsEditingBinding.IsMatch(xaml))
            {
                continue;
            }

            var codeBehindPath = file + ".cs";
            var codeBehind = File.Exists(codeBehindPath)
                ? TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(codeBehindPath))
                : string.Empty;

            dialogs.Add(new EditFormDialog(
                Path.GetFileName(file),
                xaml,
                codeBehind,
                ExtractDesignInstanceTypeName(xaml)));
        }

        return dialogs;
    }

    /// <summary>
    /// <c>d:DataContext="{d:DesignInstance Type=vm:XxxViewModel}"</c> から ViewModel 型名を取り出す。
    /// </summary>
    private static string ExtractDesignInstanceTypeName(string xaml)
    {
        var match = Regex.Match(xaml, @"DesignInstance\s+(?:Type\s*=\s*)?(?:[A-Za-z0-9_]+:)?(?<type>[A-Za-z0-9_]+)");
        return match.Success ? match.Groups["type"].Value : string.Empty;
    }

    private static string RootWindowStartTag(string xaml)
        => XamlElementInspection.EnumerateElements(xaml, "Window").First().StartTag;

    /// <summary><c>SaveCommand</c> にバインドされた「保存」ボタンの開始タグ。</summary>
    private static string? SaveButtonStartTag(string xaml)
        => XamlElementInspection.EnumerateElements(xaml, "Button")
            .Where(b => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(b.StartTag, "Command")) == "SaveCommand")
            .Select(b => b.StartTag)
            .FirstOrDefault();

    #endregion

    /// <summary>
    /// 走査が空振りしていないことと、導出の判定そのものをサンプル入力で固定する。
    /// </summary>
    /// <remarks>
    /// 「違反が 0 件であること」だけを見ると、抽出が 1 件も拾わなくなった状態を検出できない（#1786）。
    /// あわせて判定ロジックを既知のサンプル入力で固定するので、実ファイルの書き方が変わっても
    /// 「検査は動いているが対象が空」を見分けられる。
    /// </remarks>
    [Fact]
    public void 編集フォームを持つダイアログが導出できること()
    {
        // 判定ロジックの固定（実データに依存しない）
        IsEditingBinding.IsMatch(@"Visibility=""{Binding IsEditing, Converter={StaticResource C}}""")
            .Should().BeTrue("属性形の Visibility バインディングを拾うこと");
        IsEditingBinding.IsMatch(@"<Setter Property=""Visibility"" Value=""{Binding Path=IsEditing}""/>")
            .Should().BeTrue("Setter 形と Path= 付きも拾うこと");
        IsEditingBinding.IsMatch(@"Visibility=""{Binding IsEditingHistory}""")
            .Should().BeFalse("前方一致で別のプロパティを巻き込まないこと");

        EnumerateEditFormDialogs().Select(d => d.FileName)
            .Should().BeEquivalentTo(
                new[] { "CardManageDialog.xaml", "StaffManageDialog.xaml", "TransferStationGroupDialog.xaml" },
                "Issue #2080 の対象は「編集フォームの表示を IsEditing で切り替えるダイアログ」。" +
                "増減したらこの一覧を更新し、増えた画面が規約を満たしているかを確かめること");
    }

    /// <summary>
    /// 「編集中の Enter で保存される」側。
    /// </summary>
    [Fact]
    public void 保存ボタンが編集中だけ既定ボタンであること()
    {
        foreach (var dialog in EnumerateEditFormDialogs())
        {
            var saveButton = SaveButtonStartTag(dialog.Xaml);
            saveButton.Should().NotBeNull(
                $"{dialog.FileName}: SaveCommand にバインドした「保存」ボタンがあること");

            XamlElementInspection.GetAttribute(saveButton!, "IsDefault")
                .Should().Be("{Binding IsEditing}",
                    $"{dialog.FileName}: Issue #2080: 入力欄で Enter を押したときに走るのは「保存」であること。" +
                    "IsDefault が無いと Enter が何も起こさず、\"True\" 直書きだと" +
                    "一覧を見ているだけのときにも既定ボタンとして振る舞う");
        }
    }

    /// <summary>
    /// 「非編集時の Enter で意図しない操作が起きない」側。
    /// </summary>
    /// <remarks>
    /// 上の表明と対になる。<c>IsDefault</c> が <c>IsEditing</c> に束縛されていることは上で見ているが、
    /// <b>他のボタンが既定ボタンを名乗っていないこと</b>は別の観測軸であり、
    /// 「閉じる」や「新規登録」に <c>IsDefault="True"</c> が付く退行はここでしか捕まらない
    /// （付くと一覧で Enter を押しただけでダイアログが閉じる／登録モードに入る）。
    /// </remarks>
    [Fact]
    public void 保存以外のボタンが既定ボタンを名乗っていないこと()
    {
        foreach (var dialog in EnumerateEditFormDialogs())
        {
            var others = XamlElementInspection.EnumerateElements(dialog.Xaml, "Button")
                .Where(b => XamlElementInspection.GetBindingPropertyName(
                    XamlElementInspection.GetAttribute(b.StartTag, "Command")) != "SaveCommand")
                .Where(b => XamlElementInspection.GetAttribute(b.StartTag, "IsDefault") != null)
                .Select(b => $"{dialog.FileName}:{b.Line}")
                .ToList();

            others.Should().BeEmpty(
                "Issue #2080: 既定ボタンは編集中の「保存」だけ。ほかのボタンに IsDefault が付くと、" +
                "一覧で Enter を押しただけで意図しない操作が走る（違反: " + string.Join(", ", others) + "）");
        }
    }

    /// <summary>
    /// 「禁止された形の不在」— Escape が「閉じる」へ直結していないこと。
    /// </summary>
    [Fact]
    public void 編集フォームを持つダイアログはIsCancelのボタンを持たないこと()
    {
        foreach (var dialog in EnumerateEditFormDialogs())
        {
            var violations = XamlElementInspection.EnumerateElements(dialog.Xaml, "Button")
                .Where(b => string.Equals(
                    XamlElementInspection.GetAttribute(b.StartTag, "IsCancel"), "True", StringComparison.Ordinal))
                .Select(b => $"{dialog.FileName}:{b.Line}")
                .ToList();

            violations.Should().BeEmpty(
                "Issue #2080: IsCancel=\"True\" は編集中かどうかに関わらず Escape で閉じるため、" +
                "入力途中の内容が確認なしで失われる。Escape の意味は EditFormKeyPolicy で決める" +
                "（違反: " + string.Join(", ", violations) + "）");
        }
    }

    /// <summary>
    /// 「正しい形の存在」— Escape を自前で拾い、判断を <c>EditFormKeyPolicy</c> へ委ねていること。
    /// </summary>
    /// <remarks>
    /// 不在だけを見ると、Escape の処理をまるごと取り去った実装（Escape が一切効かない）でも緑になる（#1786）。
    /// </remarks>
    [Fact]
    public void 編集フォームを持つダイアログはEscapeをEditFormKeyPolicyへ委ねていること()
    {
        foreach (var dialog in EnumerateEditFormDialogs())
        {
            XamlElementInspection.GetAttribute(RootWindowStartTag(dialog.Xaml), EscapeRoutedEvent)
                .Should().Be(EscapeHandlerName,
                    $"{dialog.FileName}: Escape は Window の KeyDown（バブル）で拾う");

            XamlElementInspection.GetAttribute(RootWindowStartTag(dialog.Xaml), "PreviewKeyDown")
                .Should().BeNull(
                    $"{dialog.FileName}: トンネル（PreviewKeyDown）で拾うと、Escape を正当に消費する" +
                    "コントロール（カード種別の ComboBox のドロップダウン）より先に走り、" +
                    "「候補を閉じるつもりの Escape」が編集フォームごとの破棄になる（コードレビューで検出）");

            dialog.CodeBehind.Should().Contain($"private void {EscapeHandlerName}(",
                $"{dialog.FileName}.cs: XAML が指すハンドラーが実在すること" +
                "（綴りが食い違っても XAML の解析時まではエラーにならない）");

            dialog.CodeBehind.Should().Contain("EditFormKeyPolicy.HandleEscape(this, _viewModel, e)",
                $"{dialog.FileName}.cs: Issue #2080: Escape の判断は EditFormKeyPolicy 1 か所に置き、" +
                "コードビハインドは結線だけを行う（#1763 同じ判断を配らない）");
        }
    }

    /// <summary>
    /// ダイアログと ViewModel の対応が両方向で揃っていることを固定する。
    /// </summary>
    /// <remarks>
    /// 片方向だけだと、①ViewModel が契約を落としても XAML 側の検査は緑のまま、
    /// ②契約を実装した ViewModel の画面が走査から漏れても気付けない、のどちらかが残る。
    /// </remarks>
    [Fact]
    public void 編集フォームを持つダイアログのViewModelがIEditFormViewModelを実装していること()
    {
        var dialogs = EnumerateEditFormDialogs();

        var contractTypes = typeof(IEditFormViewModel).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IEditFormViewModel).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        dialogs.Select(d => d.ViewModelTypeName).OrderBy(n => n, StringComparer.Ordinal)
            .Should().BeEquivalentTo(contractTypes,
                "Issue #2080: Escape の委譲先は IEditFormViewModel。" +
                "画面と契約のどちらか一方だけが増えると、結線できない画面か検査されない画面が生まれる");
    }

    /// <summary>
    /// Escape が「キャンセル」ボタンと同じ経路を通ることを固定する。
    /// </summary>
    /// <remarks>
    /// 別経路にすると、ボタンで取り消したときだけ走る後始末（カード読み取り抑制の解放 #1807、
    /// 編集対象の退避値の破棄 #1761）が Escape では走らない状態が生まれる。
    /// <c>[RelayCommand]</c> が <c>CancelEdit</c> から生成する <c>CancelEditCommand</c> が
    /// XAML のバインド先なので、同名の公開プロパティが在ることが「同じメソッド」の証拠になる。
    /// </remarks>
    [Fact]
    public void CancelEditがキャンセルボタンと同じメソッドであること()
    {
        var contractTypes = typeof(IEditFormViewModel).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IEditFormViewModel).IsAssignableFrom(t))
            .ToList();

        contractTypes.Should().NotBeEmpty("IEditFormViewModel の実装が 1 つも無い状態は走査の空振り");

        foreach (var type in contractTypes)
        {
            type.GetProperty("CancelEditCommand").Should().NotBeNull(
                $"{type.Name}: [RelayCommand] が CancelEdit から生成する CancelEditCommand が" +
                "「キャンセル」ボタンのバインド先。Escape はこれと同じ CancelEdit を呼ぶ（Issue #2080）");
        }

        foreach (var dialog in EnumerateEditFormDialogs())
        {
            XamlElementInspection.EnumerateElements(dialog.Xaml, "Button")
                .Select(b => XamlElementInspection.GetBindingPropertyName(
                    XamlElementInspection.GetAttribute(b.StartTag, "Command")))
                .Should().Contain("CancelEditCommand",
                    $"{dialog.FileName}: 「キャンセル」ボタンが CancelEditCommand にバインドされていること" +
                    "（Escape と同じ経路であることの対の表明）");
        }
    }

    /// <summary>
    /// Escape を処理する前に処理中（<c>IsBusy</c>）を見ていることを固定する（コードレビューで検出）。
    /// </summary>
    /// <remarks>
    /// 判断そのものは <see cref="ICCardManager.Tests.Views.Helpers.EditFormKeyPolicyTests"/> が
    /// 単体テストで固定する。ここでは<b>その判断が実際に ViewModel の状態から作られていること</b>
    /// （契約に <c>IsBusy</c> があり、結線が両方の状態を渡していること）を表明する —
    /// 純関数側だけを見ると、引数に定数 <c>false</c> を渡す実装でも緑になる。
    /// <c>HandleEscape</c> の内側（<c>ResolveEscapeAction</c> へ渡す値と、結果の振り分け）は
    /// ソーステキストではなく <c>EditFormKeyPolicyTests.HandleEscapeは編集状態と処理中に応じて振り分けること</c>
    /// が実物の <c>Window</c> とキーイベントで固定する（Issue #2102）。
    /// </remarks>
    [Fact]
    public void Escapeの判断が編集状態と処理中の両方から作られていること()
    {
        typeof(IEditFormViewModel).GetProperty("IsBusy").Should().NotBeNull(
            "Issue #2080（コードレビュー）: 保存の待機中に CancelEdit が走ると、" +
            "継続が空の入力欄を読み、試みてすらいない編集が競合として案内される");

        foreach (var dialog in EnumerateEditFormDialogs())
        {
            dialog.CodeBehind.Should().Contain("EditFormKeyPolicy.HandleEscape(this, _viewModel, e)",
                $"{dialog.FileName}.cs: 結線は ViewModel をそのまま渡し、" +
                "どの状態を見るかは EditFormKeyPolicy が決める（状態の取り出しを呼び出し元へ配らない）");
        }
    }

    /// <summary>
    /// Enter で保存できると案内する画面では、Enter が改行になる入力欄の存在を文言が断らないこと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 備考欄は <c>AcceptsReturn="True"</c> なので、そこにフォーカスがある間の Enter は改行になり
    /// 既定ボタンは発火しない。備考はフォームの最後の欄＝入力を終えたときにいる場所なので、
    /// 「Enter キーで保存」とだけ案内すると、読み上げでそれを知った職員は改行を積むことになる
    /// （#2077「境界を述べている文言を数える」と同じ、<b>操作を述べる文言が実装と食い違う</b>形）。
    /// </para>
    /// <para>
    /// <b>対の表明</b>: 複数行の入力欄を持たない画面（<c>TransferStationGroupDialog</c> は
    /// <c>AcceptsReturn="False"</c>）に同じ但し書きを求めない。求めると、実際には起きない制限を
    /// 案内することになる。
    /// </para>
    /// </remarks>
    [Fact]
    public void 複数行の入力欄がある画面だけがEnterの但し書きを持つこと()
    {
        foreach (var dialog in EnumerateEditFormDialogs())
        {
            var hasMultilineInput = XamlElementInspection.EnumerateElements(dialog.Xaml, "TextBox")
                .Any(t => string.Equals(
                    XamlElementInspection.GetAttribute(t.StartTag, "AcceptsReturn"), "True", StringComparison.Ordinal));

            var helpText = XamlElementInspection.GetAttribute(
                RootWindowStartTag(dialog.Xaml), "AutomationProperties.HelpText") ?? string.Empty;

            if (hasMultilineInput)
            {
                helpText.Should().Contain("改行",
                    $"{dialog.FileName}: AcceptsReturn=\"True\" の欄では Enter が改行になり保存されない。" +
                    "「Enter キーで保存」とだけ案内すると、その欄にいる職員には実行できない指示になる");
            }
            else
            {
                helpText.Should().NotContain("改行",
                    $"{dialog.FileName}: 複数行の入力欄が無い画面に但し書きを付けると、" +
                    "実際には起きない制限を案内することになる（対の表明）");
            }
        }
    }
}
