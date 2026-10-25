using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2142: ボタンの塗りの色と、主操作・キャンセルの並びをダイアログ間で揃える規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// 塗りの色はボタンごとの <c>Background</c> 直書きで決まっていたため、同じ「保存」が画面によって
/// 緑（<c>SuccessActionBrush</c>）と青（<c>PrimaryBrush</c>）に分かれ、安全な「プレビュー」がリストアと同じ警告色になり、
/// 文字色用の <c>InfoTextBrush</c> が塗りに流用されていた。色は役割ごとのスタイル
/// （<c>PrimaryActionButtonStyle</c> / <c>SecondaryActionButtonStyle</c> / <c>CautionActionButtonStyle</c>）で決める。
/// </para>
/// <para>
/// 並びは多数派の「右端が主操作、その左にキャンセル／閉じる」へ揃えた。主操作が左にあった 3 画面
/// （カード登録方法・同一視グループ・履歴詳細）は、職員が別の画面で覚えた位置を押すと逆の操作になる。
/// </para>
/// <para>
/// 走査対象は本番の全 XAML から導出する（ファイル名で列挙しない。#1786）。
/// 同じ形が再発しないよう、個別の画面ではなく性質（開始タグの <c>Background</c>・並び）で検査する。
/// </para>
/// </remarks>
public class ButtonRoleConventionTests
{
    private const string PrimaryRoleStyle = "PrimaryActionButtonStyle";
    private const string SecondaryRoleStyle = "SecondaryActionButtonStyle";
    private const string CautionRoleStyle = "CautionActionButtonStyle";

    /// <summary>
    /// 開始タグに <c>Background</c> を書いてよいボタン（ファイル名 → 件数）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// いずれも「操作の役割」ではなく別の意味で色を使っているもの。件数で固定するので、
    /// 同じファイルへ新たに直書きしても赤くなる（<c>ColorLiteralSingleSourceOfTruthTests</c> と同じ作法）。
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>CardTypeSelectionDialog.xaml</c>: 「職員証」「交通系ICカード」は対等な 2 つの選択肢で、
    /// 色は主操作／二次操作ではなく選択肢の識別に使っている（役割スタイルを当てると、一方が主操作に見える）</description></item>
    /// <item><description><c>StaffAuthDialog.xaml</c> / <c>MainWindow.xaml</c>: デバッグビルドだけの仮想タッチ（淡い塗り＋既定の濃い文字）</description></item>
    /// </list>
    /// </remarks>
    private static readonly Dictionary<string, int> AllowedDirectFills = new(StringComparer.Ordinal)
    {
        ["CardTypeSelectionDialog.xaml"] = 2,
        ["StaffAuthDialog.xaml"] = 1,
        ["MainWindow.xaml"] = 1,
    };

    /// <summary>
    /// 閉じる／キャンセルより左に置いてよい、役割の無い補助のボタン（ファイル名, アクセスキーを除いた内容）（Issue #2332）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 現在は空。確定を伴わない補助（管理者ダッシュボードの「出力したファイルを開く」・接続診断の「再診断」）は
    /// 03 §4.5 の役割表の二次操作に当たるので、許可ではなく <c>SecondaryActionButtonStyle</c> を付けた。
    /// 許可は抑制に近いので、役割表のどれにも当たらないボタンだけを理由とともに載せる。
    /// </para>
    /// </remarks>
    private static readonly HashSet<(string File, string Content)> AllowedRolelessButtonsLeftOfDismiss = new();

    #region 色

    [Fact]
    public void ボタンの開始タグに塗りを直書きしないこと()
    {
        var found = AllButtons()
            .Where(b => XamlElementInspection.GetAttribute(b.Element.StartTag, "Background") != null)
            .GroupBy(b => b.Source)
            .ToDictionary(g => g.Key, g => g.Select(b => b.Element.Line).ToList());

        var violations = found
            .Where(kv => !AllowedDirectFills.TryGetValue(kv.Key, out var allowed) || kv.Value.Count > allowed)
            .SelectMany(kv => kv.Value.Select(line => $"{kv.Key}:{line}"))
            .ToList();

        violations.Should().BeEmpty(
            "ボタンの塗りは役割スタイル（{0} / {1} / {2}）で決めること。直書きすると同じ役割のボタンが画面ごとに" +
            "別の色になる（Issue #2142）。違反: {3}",
            PrimaryRoleStyle, SecondaryRoleStyle, CautionRoleStyle, string.Join(", ", violations));
    }

    [Fact]
    public void 塗りを直書きしてよいボタンの許可がまだ有効であること()
    {
        // 許可リストの陳腐化検出。是正して数が減ったら許可リストからも外すこと
        // （残しておくと、同じ場所へ再び直書きしても検査が緑になる）
        var counts = AllButtons()
            .Where(b => XamlElementInspection.GetAttribute(b.Element.StartTag, "Background") != null)
            .GroupBy(b => b.Source)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var allowed in AllowedDirectFills)
        {
            counts.TryGetValue(allowed.Key, out var actual);
            actual.Should().Be(allowed.Value, "{0} の許可件数が実態と一致すること", allowed.Key);
        }
    }

    [Theory]
    [InlineData(PrimaryRoleStyle, "SuccessActionBrush")]
    [InlineData(SecondaryRoleStyle, "PrimaryBrush")]
    [InlineData(CautionRoleStyle, "WarningActionBrush")]
    public void 役割スタイルが役割ごとの塗りと白文字を決め共有の土台に乗っていること(string styleKey, string fillKey)
    {
        var style = XamlElementInspection.EnumerateElements(AccessibilityBrushes.ReadStyles(), "Style")
            .SingleOrDefault(s => XamlElementInspection.GetAttribute(s.StartTag, "x:Key") == styleKey);
        style.Should().NotBeNull("{0} が AccessibilityStyles.xaml に定義されていること", styleKey);

        FillForegroundPairs.ResourceKeyOf(XamlElementInspection.GetSetterValue(style!.Body, "Background"))
            .Should().Be(fillKey);
        FillForegroundPairs.ResourceKeyOf(XamlElementInspection.GetSetterValue(style.Body, "Foreground"))
            .Should().Be("OnPrimaryBrush");

        // 対話状態の塗りは共有テンプレートが導出する（Issue #2094）。土台を外すと hover で白文字が読めなくなる
        FillForegroundPairs.ResourceKeyOf(XamlElementInspection.GetAttribute(style.StartTag, "BasedOn"))
            .Should().Be("FilledActionButtonStyle");
    }

    [Fact]
    public void 保存ボタンはすべて主操作の役割であること()
    {
        // Issue #2142 の欠陥そのもの: 同じ「保存」が緑の画面と青の画面に分かれていた。
        // 「保存して次へ」は保存の補助（主操作は隣の「保存」）なので対象外
        var saves = AllButtons()
            .Where(b => ContentOf(b.Element).StartsWith("保存", StringComparison.Ordinal)
                        && !ContentOf(b.Element).StartsWith("保存して", StringComparison.Ordinal))
            .ToList();

        saves.Should().HaveCountGreaterOrEqualTo(8, "保存ボタンが複数の画面で走査対象に含まれること（空振り防止）");
        saves.Where(b => RoleOf(b.Element) != PrimaryRoleStyle)
            .Select(b => $"{b.Source}:{b.Element.Line}")
            .Should().BeEmpty("「保存」は画面によらず主操作（{0}）の色にすること", PrimaryRoleStyle);
    }

    [Fact]
    public void 安全な操作に注意の色を使わないこと()
    {
        // 「プレビュー」は何も書き込まない。リストアと同じ注意の色にすると、職員は押すのをためらい、
        // 本当に注意の要る操作との区別も失われる
        var previews = AllButtons()
            .Where(b => ContentOf(b.Element).StartsWith("プレビュー", StringComparison.Ordinal))
            .ToList();

        previews.Should().NotBeEmpty("プレビューのボタンが走査対象に含まれること（空振り防止）");
        previews.Where(b => RoleOf(b.Element) == CautionRoleStyle)
            .Select(b => $"{b.Source}:{b.Element.Line}")
            .Should().BeEmpty();

        // 対の表明: 注意の役割が実在する（役割スタイルを誰も使わない実装を落とす）
        AllButtons().Should().Contain(
            b => ContentOf(b.Element).Contains("リストア") && RoleOf(b.Element) == CautionRoleStyle,
            "取り消しにくい上書き（リストア）は注意の役割であること");
    }

    [Fact]
    public void 文字色用のブラシを塗りに流用しないこと()
    {
        // InfoTextBrush（文字色）を「保存して次へ」「別々の履歴に分割」の塗りに使っていた（Issue #2142）。
        // 文字色用のブラシは地色に対するコントラストで選ばれており、白文字を載せる塗りとしての保証が無い
        var violations = AllButtons()
            .SelectMany(b => OwnFillKeys(b.Element)
                .Where(IsTextBrushKey)
                .Select(key => $"{b.Source}:{b.Element.Line} {key}"))
            .ToList();

        violations.Should().BeEmpty();

        // 空振り防止: ボタン自身の塗りを実際に拾えていること（帳票の「先月／今月」は Button.Style の Setter で塗る）
        AllButtons().SelectMany(b => OwnFillKeys(b.Element)).Should().Contain("ReturnBackgroundBrush");
    }

    [Theory]
    [InlineData("<Button Background=\"{DynamicResource InfoTextBrush}\"/>", "InfoTextBrush")]
    [InlineData("<Button><Button.Style><Style TargetType=\"Button\"><Setter Property=\"Background\" Value=\"{StaticResource PrimaryBrush}\"/></Style></Button.Style></Button>", "PrimaryBrush")]
    // テンプレートの部品（TargetName 付き）の塗りはボタンの塗りではない（履歴詳細の分割線は 2px の線）
    [InlineData("<Button><Button.Style><Style TargetType=\"Button\"><Setter TargetName=\"HoverLine\" Property=\"Background\" Value=\"{StaticResource DangerTextBrush}\"/></Style></Button.Style></Button>", null)]
    public void ボタン自身の塗りの抽出が既知の入力を正しく分類すること(string xaml, string? expected)
    {
        var button = XamlElementInspection.EnumerateElementsIncludingNested(xaml, "Button").Single();
        var keys = OwnFillKeys(button).ToList();

        if (expected == null)
        {
            keys.Should().BeEmpty();
        }
        else
        {
            keys.Should().Equal(expected);
        }
    }

    [Theory]
    [InlineData("InfoTextBrush", true)]
    [InlineData("WaitingForegroundBrush", true)]
    [InlineData("SuccessActionBrush", false)]
    [InlineData("PrimaryBrush", false)]
    [InlineData("ReturnBackgroundBrush", false)]
    public void 文字色用のブラシの判定が既知の入力を正しく分類すること(string key, bool expected)
    {
        IsTextBrushKey(key).Should().Be(expected);
    }

    #endregion

    #region 並び

    [Fact]
    public void 同じ行でキャンセルや閉じるが主操作より左にあること()
    {
        var violations = FillForegroundPairs.EnumerateProductionXaml()
            .SelectMany(f => FindOrderViolations(f.Text).Select(v => $"{f.Name}: {v}"))
            .ToList();

        violations.Should().BeEmpty(
            "ボタンの並びは「右端が主操作、その左にキャンセル／閉じる」に揃えること。" +
            "画面ごとに逆だと、別の画面で覚えた位置を押して逆の操作になる（Issue #2142）");
    }

    [Fact]
    public void 並びの検査が実データの主操作とキャンセルの組へ届いていること()
    {
        // 空振り防止: 主操作とキャンセルが同じ行に並ぶ画面が実在し、検査がそれを組として見ていること。
        // 組が 0 件なら「違反なし」は何も検査していない
        var pairs = FillForegroundPairs.EnumerateProductionXaml()
            .Where(f => CountRowPairs(f.Text) > 0)
            .Select(f => f.Name)
            .ToList();

        pairs.Should().Contain(new[]
        {
            "CardRegistrationModeDialog.xaml",
            "TransferStationGroupDialog.xaml",
            "LedgerDetailDialog.xaml",
            "SettingsDialog.xaml",
            "MergeHistoryDialog.xaml",
            "IncompleteBusStopDialog.xaml",
        }, "Issue #2142 で並びを直した 3 画面と、多数派の画面、主操作に役割スタイルを付けた 2 画面（Issue #2332）が検査の対象に含まれること");
    }

    /// <summary>
    /// 閉じる／キャンセルより左に並ぶボタンは、役割（役割スタイルか既定ボタン）を宣言していること（Issue #2332）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 並びの検査は主操作を「役割スタイルか IsDefault」で見分けるため、役割スタイルを付け忘れた主操作は
    /// 「主操作が無い行」として素通りしていた（統合履歴・バス停名未入力の 2 画面で、主操作が閉じるの左にあった）。
    /// 主操作かどうかは画面の目的で決まり機械的には判定できないので、閉じる／キャンセルより左にある役割の無いボタンは、
    /// 役割スタイルを付けることを求める（確定を伴わない補助は二次操作 <c>SecondaryActionButtonStyle</c>）。役割表のどれにも当たらない
    /// ボタンだけを <see cref="AllowedRolelessButtonsLeftOfDismiss"/> に理由とともに載せる（どちらも無いまま足すと赤になる）。
    /// </para>
    /// <para>
    /// 閉じる／キャンセルより右の役割の無いボタン（行編集の「戻る／次へ」等）は、並びの規約（閉じるの右に主操作）に反しないので対象外。
    /// </para>
    /// <para>
    /// <b>検査できない形</b>: 「同じ行」は共通の祖先が横並びの <c>StackPanel</c> / <c>WrapPanel</c> かで判定し、左右は XAML 上の出現順で決める。
    /// <c>Grid</c> の列に分けて並べた形・<c>DockPanel</c>（<c>DockPanel.Dock="Right"</c> は見た目の順が XAML の順と逆になる）は
    /// 見ていない。並びの検査（<see cref="同じ行でキャンセルや閉じるが主操作より左にあること"/>）も同じ制約を持つ。
    /// 描画後の位置で確かめたい画面は FlaUI の UI テスト（<c>DialogButtonOrderAndStaffNameUiTests</c>）で座標を比べる
    /// </para>
    /// </remarks>
    [Fact]
    public void 閉じるやキャンセルより左のボタンは役割を宣言していること()
    {
        var violations = FillForegroundPairs.EnumerateProductionXaml()
            .SelectMany(f => FindUndeclaredRoleButtons(f.Text)
                .Where(v => !IsAllowedRoleless(f.Name, v.Content))
                .Select(v => $"{f.Name}: {v.Message}"))
            .ToList();

        violations.Should().BeEmpty(
            "閉じる／キャンセルより左のボタンには役割スタイル（{0} / {1} / {2}）を付けること。" +
            "付けないと主操作かどうかが分からず、並びの検査（右端が主操作）が届かない。" +
            "確定を伴わない補助なら二次操作のスタイルを付け、役割表のどれにも当たらないボタンだけを AllowedRolelessButtonsLeftOfDismiss に理由とともに載せること（Issue #2332）:\n{3}",
            PrimaryRoleStyle, SecondaryRoleStyle, CautionRoleStyle, string.Join("\n", violations));
    }

    [Fact]
    public void 役割の無い補助のボタンの許可がまだ有効であること()
    {
        // 許可した補助のボタンが消えたり役割を持ったりしたら、許可も外す（使われない許可は、次に足す無印のボタンの抜け穴になる）
        var found = FillForegroundPairs.EnumerateProductionXaml()
            .SelectMany(f => FindUndeclaredRoleButtons(f.Text).Select(v => (File: f.Name, Content: NormalizeContent(v.Content))))
            .ToList();

        AllowedRolelessButtonsLeftOfDismiss
            .Where(allowed => !found.Contains(allowed))
            .Should().BeEmpty("許可した補助のボタンが実データに無い。許可の一覧から外すこと");
    }

    [Theory]
    // 役割スタイルの無い操作ボタンが閉じるより左にある（Issue #2332 の欠陥の形）
    [InlineData(
        "<StackPanel Orientation=\"Horizontal\"><Button Content=\"選択した統合を元に戻す\"/><Button Content=\"閉じる\" IsCancel=\"True\"/></StackPanel>",
        1)]
    // 入れ子の横並びでも同じ行
    [InlineData(
        "<StackPanel Orientation=\"Horizontal\"><StackPanel Orientation=\"Horizontal\"><Button Content=\"バス停名を入力\"/></StackPanel><Button Content=\"閉じる\"/></StackPanel>",
        1)]
    // 役割を宣言していれば対象外（並びの検査の側で見る）
    [InlineData(
        "<StackPanel Orientation=\"Horizontal\"><Button Content=\"バス停名を入力\"><Button.Style><Style TargetType=\"Button\" BasedOn=\"{StaticResource PrimaryActionButtonStyle}\"/></Button.Style></Button><Button Content=\"閉じる\" IsCancel=\"True\"/></StackPanel>",
        0)]
    [InlineData(
        "<StackPanel Orientation=\"Horizontal\"><Button Style=\"{StaticResource SecondaryActionButtonStyle}\" Content=\"Excel出力\"/><Button Content=\"閉じる\"/></StackPanel>",
        0)]
    // 閉じるより右は対象外（ウィザードの「次へ」）
    [InlineData(
        "<StackPanel Orientation=\"Horizontal\"><Button Content=\"キャンセル\" IsCancel=\"True\"/><Button Content=\"次へ\"/></StackPanel>",
        0)]
    // 別の行（縦の並び）は対象外
    [InlineData(
        "<StackPanel><Button Content=\"検索\"/><Button Content=\"閉じる\"/></StackPanel>",
        0)]
    // 閉じる／キャンセルの無い行は対象外
    [InlineData(
        "<StackPanel Orientation=\"Horizontal\"><Button Content=\"追加\"/><Button Content=\"削除\"/></StackPanel>",
        0)]
    // ポップアップの中のキャンセルは、ポップアップを開くツールバーの行とは別の行（メイン画面の「その他の月...」）
    [InlineData(
        "<StackPanel Orientation=\"Horizontal\"><Button Content=\"先月\"/><Popup><StackPanel Orientation=\"Horizontal\"><Button Content=\"キャンセル\"/><Button Style=\"{StaticResource SecondaryActionButtonStyle}\" Content=\"表示\"/></StackPanel></Popup></StackPanel>",
        0)]
    public void 役割の宣言漏れの検査が既知の入力を正しく分類すること(string xaml, int expectedViolations)
    {
        FindUndeclaredRoleButtons(xaml).Should().HaveCount(expectedViolations);
    }

    [Theory]
    [InlineData("再診断", "再診断")]
    [InlineData("再診断(_R)", "再診断")]
    [InlineData("_再診断", "再診断")]
    public void 許可の照合はアクセスキーの表記を無視すること(string content, string expected)
    {
        NormalizeContent(content).Should().Be(expected);
    }

    [Theory]
    // 主操作が左（Issue #2142 の欠陥の形）
    [InlineData(
        "<StackPanel Orientation=\"Horizontal\"><Button Content=\"OK\" IsDefault=\"True\"/><Button Content=\"キャンセル\" IsCancel=\"True\"/></StackPanel>",
        1)]
    // 役割スタイルで主操作と分かるボタンが、閉じるより左（既定ボタンでなくても主操作）
    [InlineData(
        "<StackPanel Orientation=\"Horizontal\"><Button Style=\"{StaticResource PrimaryActionButtonStyle}\" Content=\"保存\"/><Button Content=\"閉じる\"/></StackPanel>",
        1)]
    // 入れ子の横並び（履歴詳細の分割保存ボタン群）。共通の祖先が横並びなら同じ行
    [InlineData(
        "<StackPanel Orientation=\"Horizontal\"><StackPanel Orientation=\"Horizontal\"><Button Content=\"更新\"><Button.Style><Style TargetType=\"Button\" BasedOn=\"{StaticResource PrimaryActionButtonStyle}\"/></Button.Style></Button></StackPanel><Button Content=\"閉じる\"/></StackPanel>",
        1)]
    // WrapPanel も横並び
    [InlineData(
        "<WrapPanel><Button Content=\"保存\" IsDefault=\"{Binding IsEditing}\"/><Button Content=\"キャンセル\"/></WrapPanel>",
        1)]
    // 正しい並び
    [InlineData(
        "<StackPanel Orientation=\"Horizontal\"><Button Content=\"キャンセル\" IsCancel=\"True\"/><Button Content=\"OK\" IsDefault=\"True\"/></StackPanel>",
        0)]
    // 別の行（縦の StackPanel の別々の子）は並びの対象外
    [InlineData(
        "<StackPanel><Button Content=\"保存\" IsDefault=\"True\"/><Button Content=\"閉じる\"/></StackPanel>",
        0)]
    // Grid の別セル（編集フォームの保存と、画面下部の閉じる）は並びの対象外
    [InlineData(
        "<Grid><StackPanel Orientation=\"Horizontal\"><Button Content=\"保存\" IsDefault=\"True\"/></StackPanel><Button Content=\"閉じる\"/></Grid>",
        0)]
    // 既定ボタンを兼ねるキャンセル（安全側の既定）は主操作ではない（帳票の事前チェックの形）
    [InlineData(
        "<StackPanel Orientation=\"Horizontal\"><Button Content=\"中止して修正する\" IsDefault=\"True\" IsCancel=\"True\"/><Button Style=\"{StaticResource PrimaryActionButtonStyle}\" Content=\"このまま作成する\"/></StackPanel>",
        0)]
    // IsDefault="False" は主操作ではない
    [InlineData(
        "<StackPanel Orientation=\"Horizontal\"><Button Content=\"検索\" IsDefault=\"False\"/><Button Content=\"閉じる\"/></StackPanel>",
        0)]
    public void 並びの検査が既知の入力を正しく分類すること(string xaml, int expectedViolations)
    {
        FindOrderViolations(xaml).Should().HaveCount(expectedViolations);
    }

    #endregion

    #region ヘルパー

    private sealed record SourcedButton(string Source, XamlElementInspection.XamlElementSpan Element);

    private static IReadOnlyList<SourcedButton> AllButtons()
        => FillForegroundPairs.EnumerateProductionXaml()
            .SelectMany(f => XamlElementInspection.EnumerateElementsIncludingNested(f.Text, "Button")
                .Select(b => new SourcedButton(f.Name, b)))
            .ToList();

    private static string ContentOf(XamlElementInspection.XamlElementSpan button)
        => XamlElementInspection.GetAttribute(button.StartTag, "Content") ?? string.Empty;

    /// <summary>
    /// ボタンの役割スタイル（属性の <c>Style</c>、または <c>&lt;Button.Style&gt;</c> の <c>BasedOn</c>）。無ければ null。
    /// </summary>
    private static string? RoleOf(XamlElementInspection.XamlElementSpan button)
    {
        var roles = new[] { PrimaryRoleStyle, SecondaryRoleStyle, CautionRoleStyle };
        var direct = FillForegroundPairs.ResourceKeyOf(XamlElementInspection.GetAttribute(button.StartTag, "Style"));
        if (direct != null && roles.Contains(direct))
        {
            return direct;
        }

        return XamlElementInspection.EnumerateElements(button.Body, "Style")
            .Select(s => FillForegroundPairs.ResourceKeyOf(XamlElementInspection.GetAttribute(s.StartTag, "BasedOn")))
            .FirstOrDefault(k => k != null && roles.Contains(k));
    }

    /// <summary>
    /// ボタン自身の塗り（開始タグの <c>Background</c> と、<c>TargetName</c> を持たない <c>Background</c> の Setter）のキー。
    /// </summary>
    /// <remarks>
    /// テンプレートの部品（<c>TargetName</c> 付きの Setter）の塗りは除く。履歴詳細の分割線ボタンは、
    /// 2px の線の色に <c>DangerTextBrush</c> を使っており、文字を載せる塗りではない。
    /// </remarks>
    private static IEnumerable<string> OwnFillKeys(XamlElementInspection.XamlElementSpan button)
    {
        var tagFill = FillForegroundPairs.ResourceKeyOf(XamlElementInspection.GetAttribute(button.StartTag, "Background"));
        if (tagFill != null)
        {
            yield return tagFill;
        }

        foreach (var setter in XamlElementInspection.EnumerateElementsIncludingNested(button.Body, "Setter"))
        {
            if (XamlElementInspection.GetAttribute(setter.StartTag, "TargetName") == null
                && XamlElementInspection.IsSetterFor(XamlElementInspection.GetAttribute(setter.StartTag, "Property"), "Background"))
            {
                var key = FillForegroundPairs.ResourceKeyOf(XamlElementInspection.GetAttribute(setter.StartTag, "Value"));
                if (key != null)
                {
                    yield return key;
                }
            }
        }
    }

    private static bool IsTextBrushKey(string key)
        => key.EndsWith("TextBrush", StringComparison.Ordinal)
           || key.EndsWith("ForegroundBrush", StringComparison.Ordinal);

    private static bool IsDismiss(XamlElementInspection.XamlElementSpan button)
    {
        var content = ContentOf(button);
        return XamlElementInspection.GetAttribute(button.StartTag, "IsCancel") == "True"
               || content.StartsWith("キャンセル", StringComparison.Ordinal)
               || content.StartsWith("閉じる", StringComparison.Ordinal);
    }

    private static bool IsPrimary(XamlElementInspection.XamlElementSpan button)
    {
        // 既定ボタンでも、キャンセルを兼ねるもの（帳票の事前チェックの「中止して修正する」）は安全側の既定であって
        // 主操作ではない。Enter で「中止」が走るのは意図した設計（警告を見落として作成へ進ませない）
        if (IsDismiss(button))
        {
            return false;
        }

        if (RoleOf(button) == PrimaryRoleStyle)
        {
            return true;
        }

        var isDefault = XamlElementInspection.GetAttribute(button.StartTag, "IsDefault");
        return isDefault != null && !string.Equals(isDefault, "False", StringComparison.Ordinal);
    }

    private static bool IsHorizontalRow(XamlElementInspection.XamlElementSpan element)
        => element.StartTag.StartsWith("<WrapPanel", StringComparison.Ordinal)
           || (element.StartTag.StartsWith("<StackPanel", StringComparison.Ordinal)
               && XamlElementInspection.GetAttribute(element.StartTag, "Orientation") == "Horizontal");

    /// <summary>
    /// 2 つのボタンを含む最も内側の要素が横並びのパネルなら、同じ行に並んでいるとみなす。
    /// </summary>
    /// <remarks>
    /// 横並びとみなすのは <c>StackPanel Orientation="Horizontal"</c> と <c>WrapPanel</c> だけ。<c>Grid</c> の列・<c>DockPanel</c>
    /// （<c>Dock="Right"</c> で見た目の順が XAML の順と逆になる）に並べたボタンは同じ行と判定しない（検査できない形。Issue #2332）。
    /// </remarks>
    private static bool AreInSameRow(string xaml, XamlElementInspection.XamlElementSpan a, XamlElementInspection.XamlElementSpan b)
    {
        var ancestorsOfB = new HashSet<int>(XamlElementInspection.EnumerateEnclosingElements(xaml, b.Start).Select(e => e.Start));
        var common = XamlElementInspection.EnumerateEnclosingElements(xaml, a.Start)
            .Where(e => ancestorsOfB.Contains(e.Start))
            .OrderByDescending(e => e.Start)
            .FirstOrDefault();

        return common != null && IsHorizontalRow(common);
    }

    private static IEnumerable<string> FindOrderViolations(string xaml)
    {
        var buttons = XamlElementInspection.EnumerateElementsIncludingNested(xaml, "Button").ToList();
        foreach (var primary in buttons.Where(IsPrimary))
        {
            foreach (var dismiss in buttons.Where(d => d.Start > primary.Start && IsDismiss(d)))
            {
                if (AreInSameRow(xaml, primary, dismiss))
                {
                    yield return $"{primary.Line} 行の「{ContentOf(primary)}」が {dismiss.Line} 行の「{ContentOf(dismiss)}」より左にある";
                }
            }
        }
    }

    /// <summary>
    /// 閉じる／キャンセルより左にある、役割を宣言していないボタン（内容と報告文）。
    /// </summary>
    private static IEnumerable<(string Content, string Message)> FindUndeclaredRoleButtons(string xaml)
    {
        var buttons = XamlElementInspection.EnumerateElementsIncludingNested(xaml, "Button").ToList();
        var dismisses = buttons.Where(IsDismiss).ToList();
        foreach (var button in buttons.Where(b => !IsDismiss(b) && !IsPrimary(b) && RoleOf(b) == null && !OwnFillKeys(b).Any()))
        {
            var dismiss = dismisses.FirstOrDefault(d => d.Start > button.Start
                                                        && AreInSameRow(xaml, button, d)
                                                        && !IsSeparatedByPopup(xaml, button, d));
            if (dismiss != null)
            {
                var content = ContentOf(button);
                yield return (content, $"{button.Line} 行の「{content}」が {dismiss.Line} 行の「{ContentOf(dismiss)}」より左にある");
            }
        }
    }

    /// <summary>
    /// 2 つのボタンの共通の祖先より内側に <c>Popup</c> があれば、別の面（ポップアップ）に載っている。
    /// </summary>
    private static bool IsSeparatedByPopup(string xaml, XamlElementInspection.XamlElementSpan a, XamlElementInspection.XamlElementSpan b)
    {
        var ancestorsOfA = XamlElementInspection.EnumerateEnclosingElements(xaml, a.Start).ToList();
        var ancestorsOfB = XamlElementInspection.EnumerateEnclosingElements(xaml, b.Start).ToList();
        var startsOfB = new HashSet<int>(ancestorsOfB.Select(e => e.Start));
        var common = ancestorsOfA.Where(e => startsOfB.Contains(e.Start)).OrderByDescending(e => e.Start).FirstOrDefault();
        var commonStart = common?.Start ?? -1;
        return ancestorsOfA.Concat(ancestorsOfB)
            .Any(e => e.Start > commonStart && e.StartTag.StartsWith("<Popup", StringComparison.Ordinal));
    }

    private static bool IsAllowedRoleless(string fileName, string content)
        => AllowedRolelessButtonsLeftOfDismiss.Contains((fileName, NormalizeContent(content)));

    /// <summary>アクセスキーの表記（「(_R)」・先頭の「_」）を除いた内容。アクセスキーの追加で許可が外れないようにする。</summary>
    private static string NormalizeContent(string content)
        => System.Text.RegularExpressions.Regex.Replace(content, @"\(_.\)$", string.Empty).Replace("_", string.Empty).Trim();

    private static int CountRowPairs(string xaml)
    {
        var buttons = XamlElementInspection.EnumerateElementsIncludingNested(xaml, "Button").ToList();
        return buttons.Where(IsPrimary)
            .SelectMany(p => buttons.Where(IsDismiss).Select(d => (p, d)))
            .Count(x => x.p.Start != x.d.Start
                        && AreInSameRow(xaml, x.p.Start < x.d.Start ? x.p : x.d, x.p.Start < x.d.Start ? x.d : x.p));
    }

    #endregion
}
