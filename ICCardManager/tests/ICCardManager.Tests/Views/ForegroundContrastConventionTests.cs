using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2074: 文字色として使うブラシが、アプリの面（背景）に対して WCAG AA の
/// コントラスト比 4.5:1 を満たすことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 発端は「枠線用の明るいブラシ（<c>ErrorBorderBrush</c> #F44336 等）を Foreground に使っている」
/// という指摘だったが、<b>名前で禁じる検査にはしない</b>。Issue の修正方針（案）どおり
/// 「<c>*BorderBrush</c> / <c>*ActionBrush</c> を Foreground に使わない」だけを見る形にすると、
/// 置き換え先として選んだ <c>WarningForegroundBrush</c> 自身が白背景 2.65:1 で、
/// <b>是正後も規約違反が残ったまま全件緑になる</b>。
/// 「リソースキーが違えば色も違う」が成り立たないのと同じ理由で（Issue #1855）、
/// 守りたい性質（可読性）は<b>解決後の色値</b>で表明する。
/// </para>
/// <para>
/// <b>走査は「文字色が決まる経路」を列挙して設計する</b>
/// （<c>.claude/rules/development-conventions.md</c> #1786）。本リポジトリには 5 形あり、
/// どれか 1 つでも落とすと実害のある箇所が丸ごと素通りする:
/// </para>
/// <list type="number">
///   <item>属性形 <c>Foreground="{DynamicResource K}"</c></item>
///   <item><c>&lt;Setter Property="Foreground" Value="{StaticResource K}"/&gt;</c>
///         （メイン画面のカード一覧・ステータスバーはすべてこの形）</item>
///   <item><c>Foreground="{Binding P, Converter={StaticResource ResourceKeyToBrushConverter}}"</c>
///         → C# のプロパティ <c>P</c> がキー文字列を返す</item>
///   <item>コードビハインドの <c>X.Foreground = …FindResource(k)</c>
///         （<c>k</c> はローカル変数で、名前に <c>Foreground</c> を含むとは限らない）</item>
///   <item>名前に <c>Foreground</c> を含むメンバーが返すキー
///         （<c>DiagnosticStatusPresenter.GetForegroundResourceKey</c>）</item>
/// </list>
/// <para>
/// 走査対象のファイルはファイル名で列挙せず本番ソースツリーから導出する。
/// <b><c>Views/</c> だけに絞らない</b> — <c>Resources/Styles/AccessibilityStyles.xaml</c> は
/// <c>TargetType</c> 単位の <c>Style</c> で文字色を決めており、個々の画面より波及が大きい。
/// </para>
/// <para>
/// <b>本検査が見ないもの</b>: 濃色の塗り（<c>Background</c>）の上に白文字を載せる形の
/// コントラスト。これは「背景側の色」を直す話で対象ブラシも修正箇所も異なるため、
/// 本 Issue のスコープ外（別途起票）。ここでは白文字ブラシを
/// <see cref="LightOnDarkBrushKeys"/> として明示的に除外し、除外が濃色へ静かに広がらないよう
/// 「除外キーは地色では読めないほど明るいこと」を対で表明する。
/// </para>
/// <para>
/// <b>固定の地色だけでは見えない組</b>（Issue #2102）: 親の <c>Border</c> が塗り、子の <c>TextBlock</c> が
/// 文字色を決める形は、上の固定値による検査にも、同じタグの中の組しか見ない塗りの検査にも現れない。
/// <see cref="XamlSurfacePairs"/> で要素木をたどって「文字色 × 実際に載る地色」の組を作り、
/// <see cref="文字色は実際に載る地色に対して4対5対1以上のコントラストを持つこと"/> が測る
/// （ここでは白文字も除外しない — 載る地色が分かっているので測れる）。
/// </para>
/// </remarks>
public class ForegroundContrastConventionTests
{
    /// <summary>
    /// WCAG 2.1 AA が通常サイズの文字に求めるコントラスト比。
    /// </summary>
    private const double MinContrast = 4.5;

    /// <summary>
    /// 判定の基準にする地色。<b>白（#FFFFFF）ではない</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 文字が載る面は白だけではない。一覧の交互行（<c>AlternatingRowBrush</c> #FAFAFA）と
    /// パネル（<c>NeutralBackgroundBrush</c> #F5F5F5）が全画面で使われており、
    /// <b>暗い文字にとってはこれらのほうが厳しい</b>（同じ文字色でもコントラストは下がる）。
    /// </para>
    /// <para>
    /// 白だけを見ると「4.5:1 を達成した」と宣言しながら実際には未達の組み合わせが残る。
    /// #2074 の初版がまさにその形で、<c>SecondaryTextBrush</c>（最多の文字色）は
    /// 白 4.61:1 に対し <c>NeutralBackgroundBrush</c> 上では 4.23:1 だった（コードレビューで検出）。
    /// 3 面のうち<b>最も厳しい面</b>を基準にする。
    /// </para>
    /// <para>
    /// 有彩色の面（選択行の <c>CheckedRowBackgroundBrush</c> 等）は本検査の対象外。
    /// そこに載る文字色は面ごとに決まっており、面と文字の対応を静的に辿れないため。
    /// </para>
    /// </remarks>
    private const string SurfaceColor = "#F5F5F5";

    /// <summary>
    /// 基準の地色が実際に「最も厳しい面」であることを確かめるための、面のブラシキー。
    /// </summary>
    private static readonly string[] NeutralSurfaceBrushKeys =
    {
        "NeutralBackgroundBrush",
        "AlternatingRowBrush",
    };

    /// <summary>
    /// 「濃色の塗りの上に載せる文字色」として定義されており、明るい面では使わないブラシ。
    /// </summary>
    /// <remarks>
    /// 除外は<b>ホワイトリストではなく例外</b>であり、増えるときは必ずこの配列への追記になる。
    /// 追記された色が実は濃色（＝ただ検査を避けたいだけ）でないことは
    /// <see cref="除外キーは明るい面では読めないほど明るい色であること"/> が表明する。
    /// </remarks>
    private static readonly string[] LightOnDarkBrushKeys = { "OnPrimaryBrush" };

    /// <summary>
    /// 地色との組で既知の違反（ファイル名・文字色・地色）。<b>Issue #2109（未解決）で起票済み</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2102 で検査を「実際に載る地色」へ広げた時点で本番に実在したもの。本体の配色は
    /// #2109 で直すため、ここでは検査の是正だけを行い、違反を名指しで固定する。
    /// </para>
    /// <para>
    /// <b>組の単位で固定する（ファイル単位にしない）</b>。ファイルごと許すと、同じ画面に
    /// 新しい違反が入っても緑になる。行番号は含めない（無関係な編集で行がずれるたびに赤くなる）。
    /// </para>
    /// <para>
    /// <b>件数で固定する</b>（コードレビューで検出。<c>ColorLiteralSingleSourceOfTruthTests</c> の許可リストと同じ方針）。
    /// 組の集合だけで持つと、同じ画面に同じ組を増やしても緑になり、一部だけ直しても陳腐化を検出できない。
    /// 件数は「その組を作る要素の数」で、増えれば
    /// <see cref="文字色は実際に載る地色に対して4対5対1以上のコントラストを持つこと"/> が、減れば
    /// <see cref="既知の地色との組の違反がまだ残っていること"/> が赤くなる（実数との完全一致）。
    /// </para>
    /// </remarks>
    private static readonly Dictionary<(string File, string ForegroundKey, string BackgroundKey), int> KnownSurfaceViolations = new()
    {
        // Issue #2109: 明細のグループバッジ（白文字 on #388E3C = 4.12:1、on #F57C00 = 2.70:1）
        [("LedgerDetailDialog.xaml", "OnPrimaryBrush", "LedgerGroupBadge2Brush")] = 1,
        [("LedgerDetailDialog.xaml", "OnPrimaryBrush", "LedgerGroupBadge3Brush")] = 1,

        // Issue #2109: エラー表示の枠（DangerTextBrush #D32F2F on ErrorBackgroundBrush #FFEBEE = 4.36:1）
        [("CardTypeSelectionDialog.xaml", "DangerTextBrush", "ErrorBackgroundBrush")] = 1,
        [("DataExportImportDialog.xaml", "DangerTextBrush", "ErrorBackgroundBrush")] = 1,
        [("SystemManageDialog.xaml", "DangerTextBrush", "ErrorBackgroundBrush")] = 1,

        // Issue #2109 の追記候補（#2102 で検査を広げて見つかった。#2109 の本文には未記載）:
        // 返却系の淡い青の地色（ReturnBackgroundBrush #E3F2FD）の上の補足文字（#6E6E6E = 4.46:1）と
        // メイン画面のデバッグ用パネルの [DEBUG] 表示（#D32F2F = 4.36:1）
        [("ConnectionDiagnosticsDialog.xaml", "SecondaryTextBrush", "ReturnBackgroundBrush")] = 1,
        [("LedgerDetailDialog.xaml", "SecondaryTextBrush", "ReturnBackgroundBrush")] = 2,
        [("MainWindow.xaml", "SecondaryTextBrush", "ReturnBackgroundBrush")] = 3,
        [("OperationLogDialog.xaml", "SecondaryTextBrush", "ReturnBackgroundBrush")] = 1,
        [("TransferStationGroupDialog.xaml", "SecondaryTextBrush", "ReturnBackgroundBrush")] = 2,
        [("MainWindow.xaml", "DangerTextBrush", "ReturnBackgroundBrush")] = 1,
    };

    #region 検査

    [Fact]
    public void 文字色に使うブラシは地色に対して4対5対1以上のコントラストを持つこと()
    {
        var brushes = AccessibilityBrushes.Load();
        var usages = CollectForegroundUsages();

        var violations = usages
            .Where(u => !LightOnDarkBrushKeys.Contains(u.Key, StringComparer.Ordinal))
            .Select(u => new { u.Key, Color = ResolveColor(brushes, u.Key), u.Sources })
            .Select(x => new
            {
                x.Key,
                x.Color,
                x.Sources,
                Contrast = ColorMetrics.Contrast(x.Color, SurfaceColor),
            })
            .Where(x => x.Contrast < MinContrast)
            .Select(x => string.Format(
                CultureInfo.InvariantCulture,
                "{0} ({1}) = {2:F2}:1 — {3}",
                x.Key,
                x.Color,
                x.Contrast,
                string.Join(", ", x.Sources)))
            .ToList();

        violations.Should().BeEmpty(
            "文字色は地色 {0} に対して {1}:1 以上必要（Issue #2074）。"
                + "枠線・塗り用のブラシ（*BorderBrush / *ActionBrush / PrimaryBrush）ではなく "
                + "*ForegroundBrush / *TextBrush を使うこと",
            SurfaceColor,
            MinContrast);
    }

    [Fact]
    public void 基準の地色が実際に最も厳しい面であること()
    {
        // 基準を「白」に戻す変更が入ったとき、それが緩和であることを検出する。
        // 暗い文字にとっては、明るい面ほどコントラストが高い＝甘い判定になる。
        var brushes = AccessibilityBrushes.Load();
        var reference = ColorMetrics.RelativeLuminance(SurfaceColor);

        ColorMetrics.RelativeLuminance("#FFFFFF").Should().BeGreaterThan(
            reference, "白は基準の地色より明るい（＝判定が甘くなる）こと");

        foreach (var key in NeutralSurfaceBrushKeys)
        {
            var color = ResolveColor(brushes, key);

            ColorMetrics.RelativeLuminance(color).Should().BeGreaterOrEqualTo(
                reference,
                "{0} ({1}) は基準の地色 {2} 以上に明るいこと"
                    + "（より暗い無彩色の面が増えたら基準を見直す）",
                key,
                color,
                SurfaceColor);
        }
    }

    [Fact]
    public void 除外キーは明るい面では読めないほど明るい色であること()
    {
        var brushes = AccessibilityBrushes.Load();

        LightOnDarkBrushKeys.Should().NotBeEmpty();

        foreach (var key in LightOnDarkBrushKeys)
        {
            var color = ResolveColor(brushes, key);

            // 濃色を除外リストへ紛れ込ませると、本検査を避けるためだけの抜け道になる。
            // 「濃色背景専用」を名乗る以上、明るい面では実際に読めない明るさであること。
            ColorMetrics.Contrast(color, "#FFFFFF").Should().BeLessThan(
                MinContrast,
                "{0} は「濃色背景専用の文字色」として除外されている。"
                    + "明るい面でも読める濃さなら除外する理由が無い",
                key);

            ColorMetrics.RelativeLuminance(color).Should().BeGreaterThan(
                0.5, "{0} は濃色の塗りの上に載せる明るい文字色であること", key);
        }
    }

    [Fact]
    public void 意味色の文字色が色覚多様性でも分離していること()
    {
        // #2074 で貸出・警告を濃くした際、単純に暗くすると橙どうしが同じ明度域へ寄り、
        // 1 型／2 型色覚での分離が後退する。コントラストの是正が色覚多様性の犠牲に
        // ならないことを対で表明する（是正前の実測最小は 8.41、是正後は 8.38）。
        var brushes = AccessibilityBrushes.Load();
        var keys = new[]
        {
            "ErrorForegroundBrush",
            "SuccessForegroundBrush",
            "LendingForegroundBrush",
            "WarningForegroundBrush",
        };

        for (var i = 0; i < keys.Length; i++)
        {
            for (var j = i + 1; j < keys.Length; j++)
            {
                var a = ResolveColor(brushes, keys[i]);
                var b = ResolveColor(brushes, keys[j]);

                ColorMetrics.MinDeltaEAcrossColorVisionTypes(a, b).Should().BeGreaterThan(
                    8.0, "{0} と {1} は色覚多様性でも分離していること", keys[i], keys[j]);
            }
        }
    }

    [Fact]
    public void 文字色は実際に載る地色に対して4対5対1以上のコントラストを持つこと()
    {
        // Issue #2102: 上の検査は地色を #F5F5F5 に固定しており、親の Border が塗る地色
        // （エラー表示の ErrorBackgroundBrush、グループバッジの色）の上に載る文字を見ていなかった。
        // 要素木をたどって「文字色 × 実際の地色」の組を作り、色値で測る。
        // 既知の違反（Issue #2109 で起票済み・未解決）は許可リストで固定し、それ以外の違反は赤にする
        // 件数を超えた分は、同じ組でも新しい違反として報告する（組の集合で許すと、同じ画面に同じ組を足しても緑）
        var unexpected = SurfaceViolations()
            .GroupBy(v => (v.File, v.ForegroundKey, v.BackgroundKey))
            .Where(g => !KnownSurfaceViolations.TryGetValue(g.Key, out var allowed) || g.Count() > allowed)
            .SelectMany(g => g.Select(v => v.Describe()
                + (KnownSurfaceViolations.TryGetValue(g.Key, out var allowed)
                    ? string.Format(CultureInfo.InvariantCulture, "（許可リストは {0} 件、実数は {1} 件）", allowed, g.Count())
                    : string.Empty)))
            .ToList();

        // 件数で比べるので、違反は 1 件目だけでなく全件を示す（FluentAssertions は先頭しか表示しない）
        unexpected.Should().BeEmpty(
            "文字色はその文字が載る地色に対して {0}:1 以上必要（Issue #2074 / #2102）。"
                + "地色を変えずに文字色だけを差し替えると、別の地色の上で読めなくなることがある:\n{1}",
            MinContrast,
            string.Join("\n", unexpected));
    }

    [Fact]
    public void 既知の地色との組の違反がまだ残っていること()
    {
        // 許可リストの陳腐化検出。Issue #2109 で配色を直したら、該当する行の件数を減らす（0 なら外す）こと
        // （残しておくと、同じ組が再び入っても検査が緑になる）
        var actual = SurfaceViolations()
            .GroupBy(v => (v.File, v.ForegroundKey, v.BackgroundKey))
            .ToDictionary(g => g.Key, g => g.Count());

        KnownSurfaceViolations.Should().NotBeEmpty(
            "許可リストが空になったら、このテストごと削除してよい（Issue #2109 の解消）");

        var stale = KnownSurfaceViolations
            .Where(kv => !actual.TryGetValue(kv.Key, out var count) || count < kv.Value)
            .Select(kv => string.Format(
                CultureInfo.InvariantCulture,
                "{0} の {1} on {2}: 許可リストは {3} 件、実数は {4} 件",
                kv.Key.File,
                kv.Key.ForegroundKey,
                kv.Key.BackgroundKey,
                kv.Value,
                actual.TryGetValue(kv.Key, out var count) ? count : 0))
            .ToList();

        stale.Should().BeEmpty(
            "許可リストの違反が減っている（{0}:1 以上へ是正された）。許可リストの件数を実数へ減らし、"
                + "0 件になった行は外すこと（Issue #2109）:\n{1}",
            MinContrast,
            string.Join("\n", stale));
    }

    [Fact]
    public void 地色との組の走査が実データへ届いていること()
    {
        // 「違反がゼロ」は、組が 1 つも作られない壊れ方でも成立する。親の塗りと子の文字色の組、
        // トリガーで切り替わる組が実際に作られていることを対で表明する
        var pairs = CollectSurfacePairs();

        pairs.Should().Contain(
            p => p.File == "CardTypeSelectionDialog.xaml"
                 && p.ForegroundKey == "HintForegroundBrush"
                 && p.BackgroundKey == "WarningBackgroundBrush",
            "親の Border が塗り、子の TextBlock が文字色を決める組が作られること");
        pairs.Should().Contain(
            p => p.File == "LedgerDetailDialog.xaml"
                 && p.ForegroundKey == "OnPrimaryBrush"
                 && p.BackgroundKey == "LedgerGroupBadge1Brush",
            "親と子のスタイルのトリガーで切り替わる組が作られること");
        pairs.Should().NotContain(
            p => p.File == "LedgerDetailDialog.xaml"
                 && p.ForegroundKey == "SecondaryTextBrush"
                 && p.BackgroundKey.StartsWith("LedgerGroupBadge", StringComparison.Ordinal),
            "同じ条件で切り替わる塗りと文字色は、同時に起こる組だけを作ること"
                + "（未所属の灰色の文字は色付きのバッジの上には載らない）");
        pairs.Should().Contain(
            p => p.File == "AdminDashboardDialog.xaml"
                 && p.ForegroundKey == "SecondaryTextBrush"
                 && p.BackgroundKey == "NeutralBackgroundBrush",
            "キー付きのスタイル（サマリータイルの SummaryTileStyle）が決める塗りの上の組が作られること"
                + "（Style 属性を見た時点で打ち切る形へ戻ると消える）");

        // 測らない・緩める組は、その理由が実在する範囲に留まっていること
        var brushes = AccessibilityBrushes.Load();
        pairs.Where(p => IsTranslucent(ResolveColor(brushes, p.BackgroundKey)))
            .Select(p => p.BackgroundKey)
            .Distinct()
            .Should().BeEquivalentTo(
                new[] { "OverlayBrush" },
                "半透明ゆえに測らない地色は処理中オーバーレイだけであること（増えたら測らない理由が成り立つか確かめる）");
        pairs.Should().Contain(
            p => p.File == "MainWindow.xaml" && p.BackgroundKey == "HeaderBackgroundBrush",
            "見出し帯の上の太字の見出しが走査対象に含まれること（大きな文字の 3:1 で測る）");
    }

    #endregion

    #region 空振り検出（走査が実際に各経路へ届いていること）

    [Fact]
    public void 走査が5つの経路すべてへ届いていること()
    {
        var usages = CollectForegroundUsages();
        var sources = usages.SelectMany(u => u.Sources).Distinct(StringComparer.Ordinal).ToList();

        usages.Should().HaveCountGreaterThan(
            8, "文字色として使われているブラシが複数種類あること");

        // ① 属性形 ② Setter 形（メイン画面のカード一覧・ステータスバーは Setter 形しかない）
        sources.Should().Contain(
            "MainWindow.xaml", "メイン画面の Foreground が走査対象に含まれること");

        // TargetType 単位の Style。個々の画面より波及が大きいのに Views/ の外にある
        sources.Should().Contain(
            "AccessibilityStyles.xaml",
            "スタイル辞書自身の Foreground が走査対象に含まれること");

        // ③ Binding + ResourceKeyToBrushConverter → C# のプロパティ
        sources.Should().Contain(
            "ReportExportStatusPresenter.cs",
            "Binding 経由で C# が返すキーが走査対象に含まれること");

        // ④ コードビハインドの .Foreground = …FindResource(ローカル変数)
        sources.Should().Contain(
            "StaffAuthDialog.xaml.cs",
            "ローカル変数を経由する文字色が走査対象に含まれること");
        sources.Should().Contain(
            "ToastNotificationWindow.xaml.cs",
            "コードビハインドが差し替える文字色が走査対象に含まれること");

        // ⑤ 名前に Foreground を含むメンバーが返すキー
        sources.Should().Contain(
            "DiagnosticStatusPresenter.cs",
            "C# 側がキー文字列で返す文字色が走査対象に含まれること");
    }

    [Fact]
    public void ブラシ定義の抽出が全件を拾えていること()
    {
        // 抽出漏れは非対称に効く。XAML 経路は ResolveColor が赤くなるが、
        // C# 経路は「ブラシキーと一致しない」として収集自体が止まり緑のまま（fail-open）。
        // 数える側も AccessibilityBrushes へ寄せる（#1763）。ここに私的な数え方を残すと、
        // 数え方を変える人が片方を取りこぼし、その片方だけが静かに誤検出／見落としになる
        AccessibilityBrushes.Load().Should().NotBeEmpty(
            "AccessibilityStyles.xaml のブラシ抽出が空振りしていないこと（両辺が 0 件でも件数は一致する）");
        AccessibilityBrushes.Load().Should().HaveCount(
            AccessibilityBrushes.CountDeclarations(),
            "AccessibilityStyles.xaml の SolidColorBrush 定義をすべて抽出できていること"
                + "（属性順や追加属性で正規表現から漏れると、そのキーだけ静かに検査されなくなる）");
    }

    [Fact]
    public void システム色への参照を対象外としていること()
    {
        // AccessibilityStyles.xaml は {DynamicResource {x:Static SystemColors.…}} で
        // OS の文字色へ委ねる箇所を持つ。ブラシキーではないので検査できないが、
        // 「見ていない」ことを表明しておかないと、見落としと区別が付かない。
        var xaml = XamlElementInspection.StripXmlComments(File.ReadAllText(AccessibilityStylesPath));

        Regex.Matches(xaml, "SystemColors\\.[A-Za-z]+BrushKey").Count.Should().BeGreaterThan(
            0, "システム色への委譲が実在すること（消えたら本テストの前提が変わる）");

        CollectForegroundUsages()
            .Select(u => u.Key)
            .Should().NotContain(
                k => k.StartsWith("SystemColors", StringComparison.Ordinal),
                "システム色は OS が決めるため本検査の対象外");
    }

    #endregion

    #region 判定ロジックそのものの固定（実データが空でも働く）

    [Theory]
    // 旧実装が Foreground に使っていた枠線・塗り用のブラシ（検出されるべき）
    [InlineData("#F44336", false)] // ErrorBorderBrush
    [InlineData("#4CAF50", false)] // SuccessActionBrush
    [InlineData("#FF9800", false)] // WarningActionBrush
    [InlineData("#F57F17", false)] // 旧 WarningForegroundBrush（名前では捕まらない違反）
    [InlineData("#808080", false)] // 旧 SecondaryTextBrush（Gray）
    [InlineData("#757575", false)] // 白基準なら通るが地色基準では落ちる（4.61 → 4.23）
    [InlineData("#1976D2", false)] // PrimaryBrush（同上。4.60 → 4.22）
    // 是正後の文字色（検出されないべき）
    [InlineData("#B71C1C", true)] // ErrorForegroundBrush
    [InlineData("#1B5E20", true)] // SuccessForegroundBrush
    [InlineData("#AC5910", true)] // WarningForegroundBrush
    [InlineData("#6E6E6E", true)] // SecondaryTextBrush
    [InlineData("#1565C0", true)] // InfoTextBrush
    public void 判定ロジックが既知の入力で期待どおり動くこと(string color, bool expectedPass)
    {
        // 実データが空でも空振り検出が働くよう、判定そのものを既知の入力で固定する
        // （.claude/rules/development-conventions.md #1786）
        var passes = ColorMetrics.Contrast(color, SurfaceColor) >= MinContrast;

        passes.Should().Be(expectedPass);
    }

    [Fact]
    public void 走査が塗りと文字色を取り違えないこと()
    {
        // 同じブラシキーは塗りにも使われる（ChartGeometryCalculator が SuccessActionBrush を
        // 積み上げ棒の塗りとして受け取る）。拾う側・拾わない側をサンプル入力で対に固定する。
        var brushKeys = AccessibilityBrushes.Load().Keys.ToList();

        const string FillSource = @"
public IReadOnlyList<Bar> CalculateBars(double[] values, string brushKey)
{
    var fallback = ""SuccessActionBrush"";
    return Build(values, brushKey ?? fallback);
}";

        const string ForegroundMemberSource = @"
public static string GetForegroundResourceKey(DiagnosticStatus status)
{
    switch (status)
    {
        case DiagnosticStatus.Warning:
            return ""WarningActionBrush"";
        default:
            return ""SecondaryTextBrush"";
    }
}";

        const string LocalVariableSource = @"
private void Apply(bool isError)
{
    var borderKey = isError ? ""ErrorBorderBrush"" : ""SuccessBorderBrush"";
    var fgKey = isError ? ""ErrorForegroundBrush"" : ""SuccessForegroundBrush"";
    StatusBorder.BorderBrush = (Brush)FindResource(borderKey);
    StatusText.Foreground = (Brush)FindResource(fgKey);
}";

        CollectFromCSharp(FillSource, brushKeys, new string[0])
            .Should().BeEmpty("Foreground へ流れないキーは拾わないこと");

        CollectFromCSharp(ForegroundMemberSource, brushKeys, new string[0])
            .Should().BeEquivalentTo(
                new[] { "WarningActionBrush", "SecondaryTextBrush" },
                "名前に Foreground を含むメンバーが返すキーは本体まで辿って拾うこと");

        var fromLocal = CollectFromCSharp(LocalVariableSource, brushKeys, new string[0]).ToList();
        fromLocal.Should().Contain("ErrorForegroundBrush");
        fromLocal.Should().Contain("SuccessForegroundBrush");
        fromLocal.Should().NotContain(
            "ErrorBorderBrush",
            "隣で BorderBrush へ流れているキーまで巻き込まないこと（誤検出はガードの寿命を縮める）");
    }

    [Fact]
    public void バインド経由のプロパティ名を抽出できること()
    {
        const string Xaml =
            "<TextBlock Foreground=\"{Binding ExportStateBrushKey, "
            + "Converter={StaticResource ResourceKeyToBrushConverter}}\"/>"
            + "<Rectangle Fill=\"{Binding BrushKey, "
            + "Converter={StaticResource ResourceKeyToBrushConverter}}\"/>";

        ExtractConverterBoundPropertyNames(Xaml).Should().BeEquivalentTo(
            new[] { "ExportStateBrushKey" },
            "Foreground のバインドだけを拾い、Fill（塗り）のバインドは拾わないこと");
    }

    [Fact]
    public void Setterの書き方の違いを取りこぼさないこと()
    {
        const string Xaml = @"
<Setter Property=""Foreground"" Value=""{DynamicResource AttributeOrderA}""/>
<Setter Value=""{StaticResource AttributeOrderB}"" Property=""Foreground""/>
<Setter TargetName=""x"" Property=""Foreground"" Value=""{DynamicResource WithTargetName}""/>
<Setter Property='Foreground' Value='{DynamicResource SingleQuoted}'/>
<Setter Property=""Background"" Value=""{DynamicResource NotForeground}""/>
<TextBlock Foreground=""{StaticResource PlainAttribute}""/>";

        ExtractXamlForegroundKeys(Xaml).Should().BeEquivalentTo(
            new[]
            {
                "AttributeOrderA",
                "AttributeOrderB",
                "WithTargetName",
                "SingleQuoted",
                "PlainAttribute",
            },
            "属性順・TargetName の有無・引用符の種類で取りこぼさず、Background は拾わないこと");
    }

    [Fact]
    public void 地色との組を親の塗りと子の文字色から作ること()
    {
        // 判定ロジックを既知の入力で固定する（実データの違反が #2109 で消えても空振りを検出できるように）
        const string Xaml = @"
<Grid xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
    <!-- <Border Background=""{DynamicResource CommentedBrush}""><TextBlock Foreground=""{DynamicResource X}""/></Border> -->
    <Border Background=""{DynamicResource PanelBrush}"">
        <StackPanel Background=""Transparent"">
            <TextBlock Foreground=""{DynamicResource ThroughTransparent}""/>
        </StackPanel>
    </Border>
    <Border Background=""{Binding StateBrush}"">
        <TextBlock Foreground=""{DynamicResource UnderBinding}""/>
    </Border>
    <Border Background=""{DynamicResource OuterBrush}"">
        <ContentControl>
            <ContentControl.ContentTemplate>
                <DataTemplate>
                    <TextBlock Foreground=""{DynamicResource InsideTemplate}""/>
                </DataTemplate>
            </ContentControl.ContentTemplate>
        </ContentControl>
    </Border>
    <TextBlock Foreground=""{DynamicResource NoSurface}""/>
</Grid>";

        XamlSurfacePairs.Collect("sample.xaml", Xaml, XamlSurfacePairs.NoSharedStyles).Pairs
            .Select(p => (p.ForegroundKey, p.BackgroundKey))
            .Should().BeEquivalentTo(
                new[] { ("ThroughTransparent", "PanelBrush") },
                "透明な塗りは素通りして祖先の塗りと組にし、Binding の塗り・テンプレートの境界・"
                    + "塗りの無い面・コメントの中では組を作らないこと");
    }

    [Fact]
    public void 同じ条件で切り替わる塗りと文字色は同時に起こる組だけを作ること()
    {
        // グループバッジと同じ形。直積を取ると「未所属の灰色の文字 × 色付きのバッジ」という
        // 起こらない組まで違反になり、誤検出が修正者を検査の除外へ誘導する（#1786）
        const string Xaml = @"
<Border xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
    <Border.Style>
        <Style TargetType=""Border"">
            <Setter Property=""Background"" Value=""Transparent""/>
            <Style.Triggers>
                <DataTrigger Binding=""{Binding Index}"" Value=""1"">
                    <Setter Property=""Background"" Value=""{DynamicResource Badge1}""/>
                </DataTrigger>
                <DataTrigger Binding=""{Binding Index}"" Value=""2"">
                    <Setter Property=""Background"" Value=""{DynamicResource Badge2}""/>
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </Border.Style>
    <TextBlock>
        <TextBlock.Style>
            <Style TargetType=""TextBlock"">
                <Setter Property=""Foreground"" Value=""{DynamicResource OnBadge}""/>
                <Style.Triggers>
                    <DataTrigger Binding=""{Binding Index}"" Value=""0"">
                        <Setter Property=""Foreground"" Value=""{DynamicResource Muted}""/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </TextBlock.Style>
    </TextBlock>
</Border>";

        XamlSurfacePairs.Collect("sample.xaml", Xaml, XamlSurfacePairs.NoSharedStyles).Pairs
            .Select(p => (p.ForegroundKey, p.BackgroundKey))
            .Should().BeEquivalentTo(new[] { ("OnBadge", "Badge1"), ("OnBadge", "Badge2") });
    }

    [Fact]
    public void ローカル値の文字色はスタイルのトリガーより優先すること()
    {
        // WPF の優先順位（ローカル値 > スタイルのトリガー > スタイルの Setter）どおりに評価する
        const string Xaml = @"
<Border xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" Background=""{DynamicResource Panel}"">
    <TextBlock Foreground=""{DynamicResource Local}"">
        <TextBlock.Style>
            <Style TargetType=""TextBlock"">
                <Style.Triggers>
                    <Trigger Property=""IsMouseOver"" Value=""True"">
                        <Setter Property=""Foreground"" Value=""{DynamicResource FromTrigger}""/>
                    </Trigger>
                </Style.Triggers>
            </Style>
        </TextBlock.Style>
    </TextBlock>
</Border>";

        XamlSurfacePairs.Collect("sample.xaml", Xaml, XamlSurfacePairs.NoSharedStyles).Pairs
            .Select(p => (p.ForegroundKey, p.BackgroundKey))
            .Should().BeEquivalentTo(new[] { ("Local", "Panel") });
    }

    [Fact]
    public void キー付きのスタイルを解決してから塗りと文字色を読むこと()
    {
        // コードレビューで検出: Style="{StaticResource …}" を見た時点で「確かめられない」と打ち切っていたため、
        // 共有のエラー枠スタイル（ErrorStatusStyle）の上に DangerTextBrush を載せても組が作られなかった
        const string SharedDictionary = @"
<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
    <Style x:Key=""IndicatorStyle"" TargetType=""Border"">
        <Setter Property=""Padding"" Value=""8""/>
    </Style>
    <Style x:Key=""ErrorFrameStyle"" TargetType=""Border"" BasedOn=""{StaticResource IndicatorStyle}"">
        <Setter Property=""Background"" Value=""{StaticResource ErrorSurface}""/>
    </Style>
    <Style x:Key=""DerivedFrameStyle"" TargetType=""Border"" BasedOn=""{StaticResource ErrorFrameStyle}"">
        <Setter Property=""Margin"" Value=""4""/>
    </Style>
</ResourceDictionary>";

        const string Xaml = @"
<Grid xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
      xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
    <Grid.Resources>
        <Style x:Key=""LocalTextStyle"" TargetType=""TextBlock"">
            <Setter Property=""Foreground"" Value=""{DynamicResource FromLocalStyle}""/>
        </Style>
    </Grid.Resources>
    <Border Style=""{StaticResource ErrorFrameStyle}"">
        <TextBlock Foreground=""{DynamicResource OnSharedStyle}""/>
    </Border>
    <Border Style=""{StaticResource DerivedFrameStyle}"">
        <TextBlock Foreground=""{DynamicResource ThroughBasedOn}""/>
    </Border>
    <Border Background=""{DynamicResource Panel}"">
        <TextBlock Style=""{StaticResource LocalTextStyle}""/>
    </Border>
    <Border Style=""{StaticResource DefinedElsewhere}"">
        <TextBlock Foreground=""{DynamicResource UnderUnresolved}""/>
    </Border>
</Grid>";

        XamlSurfacePairs.Collect("sample.xaml", Xaml, XamlSurfacePairs.LoadKeyedStyles(SharedDictionary)).Pairs
            .Select(p => (p.ForegroundKey, p.BackgroundKey))
            .Should().BeEquivalentTo(
                new[]
                {
                    ("OnSharedStyle", "ErrorSurface"),
                    ("ThroughBasedOn", "ErrorSurface"),
                    ("FromLocalStyle", "Panel"),
                },
                "共有のスタイル辞書・BasedOn・同じファイルの中のスタイルを解決し、"
                    + "解決できないスタイルの上では組を作らないこと");
    }

    [Fact]
    public void 継承した文字色が内側の別の塗りに載る組を作ること()
    {
        // コードレビューで検出: 文字色を決めた要素から祖先へしか辿らなかったため、白文字を決めた枠の
        // 内側に淡い塗りの枠があっても組が作られなかった（白文字 on #FFEBEE が緑）
        const string Xaml = @"
<Grid xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
    <Border Background=""{DynamicResource Primary}"" TextElement.Foreground=""{DynamicResource OnPrimary}"">
        <StackPanel>
            <Border Background=""{DynamicResource ErrorSurface}"">
                <TextBlock Text=""内側の塗りの上""/>
            </Border>
            <TextBlock Text=""外側の塗りの上""/>
            <Button Background=""{DynamicResource ButtonSurface}"">
                <TextBlock Text=""テーマが文字色を決め直す""/>
            </Button>
            <Border Background=""{DynamicResource ErrorSurface}"">
                <TextBlock Foreground=""{DynamicResource OwnForeground}""/>
            </Border>
        </StackPanel>
    </Border>
    <Border Background=""{DynamicResource NoForegroundAbove}"">
        <TextBlock Text=""継承する文字色が無い""/>
    </Border>
</Grid>";

        XamlSurfacePairs.Collect("sample.xaml", Xaml, XamlSurfacePairs.NoSharedStyles).Pairs
            .Select(p => (p.ForegroundKey, p.BackgroundKey))
            .Should().BeEquivalentTo(
                new[]
                {
                    // 文字色を決めた枠自身の組（従来どおり）
                    ("OnPrimary", "Primary"),
                    // 継承した文字色 × 内側の塗り
                    ("OnPrimary", "ErrorSurface"),
                    // 自分で文字色を決めた TextBlock は従来の経路で組になる
                    ("OwnForeground", "ErrorSurface"),
                },
                "継承した文字色は内側の塗りとも組にし、テーマが文字色を決め直す要素（Button）を越えた継承・"
                    + "継承する文字色が無い文字では組を作らないこと");
    }

    [Fact]
    public void 要素で書いた塗りと文字色は解釈せずに組を作らないこと()
    {
        // コードレビューで検出: <Setter.Value> の中身を「値が無い＝透明」と読み、塗られている面を
        // 素通りして祖先の塗りと誤った組を作っていた。確かめられない値は組を作らない側へ倒す
        const string Xaml = @"
<Border xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" Background=""{DynamicResource Panel}"">
    <StackPanel>
        <Border>
            <Border.Style>
                <Style TargetType=""Border"">
                    <Setter Property=""Background"">
                        <Setter.Value>
                            <SolidColorBrush Color=""{DynamicResource SomeColor}""/>
                        </Setter.Value>
                    </Setter>
                </Style>
            </Border.Style>
            <TextBlock Foreground=""{DynamicResource UnderSetterValue}""/>
        </Border>
        <Border>
            <Border.Background>
                <SolidColorBrush Color=""{DynamicResource SomeColor}""/>
            </Border.Background>
            <TextBlock Foreground=""{DynamicResource UnderPropertyElement}""/>
        </Border>
        <TextBlock>
            <TextBlock.Style>
                <Style TargetType=""TextBlock"">
                    <Style.Triggers>
                        <Trigger Property=""IsMouseOver"" Value=""True"">
                            <Setter Property=""Foreground"">
                                <Setter.Value>
                                    <SolidColorBrush Color=""{DynamicResource SomeColor}""/>
                                </Setter.Value>
                            </Setter>
                        </Trigger>
                    </Style.Triggers>
                </Style>
            </TextBlock.Style>
        </TextBlock>
        <TextBlock Foreground=""{DynamicResource Plain}""/>
    </StackPanel>
</Border>";

        XamlSurfacePairs.Collect("sample.xaml", Xaml, XamlSurfacePairs.NoSharedStyles).Pairs
            .Select(p => (p.ForegroundKey, p.BackgroundKey))
            .Should().BeEquivalentTo(
                new[] { ("Plain", "Panel") },
                "Setter.Value・プロパティ要素で書いた値は解釈せず、その要素を起点にした組を作らないこと"
                    + "（素直に書いた隣の文字は従来どおり組になる）");
    }

    #endregion

    #region ヘルパー

    /// <summary>本番 XAML 全体から「文字色 × 実際に載る地色」の組を集める。</summary>
    private static IReadOnlyList<XamlSurfacePairs.SurfacePair> CollectSurfacePairs()
    {
        var pairs = new List<XamlSurfacePairs.SurfacePair>();
        var tooComplex = new List<string>();
        var files = FillForegroundPairs.EnumerateProductionXaml().ToList();

        // 画面が Style="{StaticResource …}" で参照する共有スタイルを解決できるようにする（コードレビューで検出）
        var styleDictionaries = files.Where(f => f.Name == "AccessibilityStyles.xaml").ToList();
        styleDictionaries.Should().ContainSingle("共有スタイルの辞書 AccessibilityStyles.xaml が走査対象に 1 つあること");
        var sharedStyles = XamlSurfacePairs.LoadKeyedStyles(styleDictionaries[0].Text);
        sharedStyles.Should().ContainKey("ErrorStatusStyle", "共有のエラー枠スタイルを解決先として読めていること");
        sharedStyles["ErrorStatusStyle"].Should().NotBeNull("キーが一意に解決できること");

        foreach (var (name, text) in files)
        {
            var result = XamlSurfacePairs.Collect(name, text, sharedStyles);
            pairs.AddRange(result.Pairs);
            tooComplex.AddRange(result.TooComplex);
        }

        tooComplex.Should().BeEmpty("トリガーの条件が多すぎて評価を打ち切った要素が無いこと（黙って検査の外へ出さない）");
        return pairs;
    }

    private sealed class SurfaceViolation
    {
        public SurfaceViolation(XamlSurfacePairs.SurfacePair pair, string foreground, string background, double contrast)
        {
            File = pair.File;
            Line = pair.Line;
            ForegroundKey = pair.ForegroundKey;
            BackgroundKey = pair.BackgroundKey;
            Foreground = foreground;
            Background = background;
            Contrast = contrast;
            IsLargeBoldText = string.Equals(pair.FontWeight, "Bold", StringComparison.Ordinal)
                              && LargeBoldFontSizeKeys.Contains(
                                  FillForegroundPairs.ResourceKeyOf(pair.FontSize), StringComparer.Ordinal);
        }

        public bool IsLargeBoldText { get; }

        public string File { get; }

        public int Line { get; }

        public string ForegroundKey { get; }

        public string BackgroundKey { get; }

        public string Foreground { get; }

        public string Background { get; }

        public double Contrast { get; }

        public string Describe()
            => string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1} {2} ({3}) on {4} ({5}) = {6:F2}:1",
                File,
                Line,
                ForegroundKey,
                Foreground,
                BackgroundKey,
                Background,
                Contrast);
    }

    /// <summary>WCAG 2.1 AA が大きな文字（太字 14pt ≒ 18.66px 以上）に求めるコントラスト比。</summary>
    private const double LargeTextMinContrast = 3.0;

    /// <summary>
    /// 太字で書けば「大きな文字」になる文字サイズのリソースキー。
    /// </summary>
    /// <remarks>
    /// <c>TitleFontSize</c> は文字サイズ「小」でも 19px（≧ 18.66px）であることを
    /// <c>BackgroundContrastConventionTests.文字サイズが最小の設定でも見出しが大きな文字の条件を満たすこと</c> が
    /// 本番の値から固定している。その前提が崩れればあちらが赤くなる。
    /// </remarks>
    private static readonly string[] LargeBoldFontSizeKeys = { "TitleFontSize" };

    /// <summary>地色との組のうち、求められるコントラスト比に届かないもの。</summary>
    /// <remarks>
    /// <para>
    /// <b>地色が半透明の組は測らない</b>（<see cref="IsTranslucent"/>）。処理中オーバーレイ（<c>OverlayBrush</c>）は
    /// 下にある画面と重ねて見えるため、地色が静的には決まらない。<c>#AARRGGBB</c> の RGB だけで測ると
    /// 実際とは別の色を測ることになる。
    /// </para>
    /// <para>
    /// 太字の見出し（<see cref="LargeBoldFontSizeKeys"/>）は大きな文字の 3:1 で測る。
    /// <c>BackgroundContrastConventionTests</c> が見出し帯（<c>HeaderBackgroundBrush</c>）に置いている例外と同じ根拠。
    /// </para>
    /// </remarks>
    private static IReadOnlyList<SurfaceViolation> SurfaceViolations()
    {
        var brushes = AccessibilityBrushes.Load();

        return CollectSurfacePairs()
            .Where(p => !IsTranslucent(ResolveColor(brushes, p.BackgroundKey)))
            .Select(p => new SurfaceViolation(
                p,
                ResolveColor(brushes, p.ForegroundKey),
                ResolveColor(brushes, p.BackgroundKey),
                ColorMetrics.Contrast(ResolveColor(brushes, p.ForegroundKey), ResolveColor(brushes, p.BackgroundKey))))
            .Where(v => v.Contrast < RequiredContrast(v))
            .ToList();
    }

    private static double RequiredContrast(SurfaceViolation v)
        => v.IsLargeBoldText ? LargeTextMinContrast : MinContrast;

    /// <summary><c>#AARRGGBB</c> で、不透明（<c>FF</c>）でないか。</summary>
    private static bool IsTranslucent(string color)
        => color.Length == 9 && !color.StartsWith("#FF", StringComparison.OrdinalIgnoreCase);

    private sealed class ForegroundUsage
    {
        public ForegroundUsage(string key)
        {
            Key = key;
            Sources = new List<string>();
        }

        public string Key { get; }

        public List<string> Sources { get; }
    }

    private static string ProductionRoot => TestPaths.GetProductionSourceRoot();

    private static string AccessibilityStylesPath => AccessibilityBrushes.StylesPath;

    /// <summary>
    /// 本番ソース全体から、文字色として参照されているリソースキーと参照元を集める。
    /// </summary>
    private static IReadOnlyList<ForegroundUsage> CollectForegroundUsages()
    {
        var brushKeys = AccessibilityBrushes.Load().Keys.ToList();
        var usages = new Dictionary<string, ForegroundUsage>(StringComparer.Ordinal);

        void Add(string key, string source)
        {
            if (!usages.TryGetValue(key, out var usage))
            {
                usage = new ForegroundUsage(key);
                usages[key] = usage;
            }

            if (!usage.Sources.Contains(source, StringComparer.Ordinal))
            {
                usage.Sources.Add(source);
            }
        }

        // XAML: 属性形・Setter 形・Binding 形
        var boundPropertyNames = new List<string>();
        foreach (var path in EnumerateProductionFiles("*.xaml"))
        {
            var text = XamlElementInspection.StripXmlComments(File.ReadAllText(path));
            var name = Path.GetFileName(path);

            foreach (var key in ExtractXamlForegroundKeys(text))
            {
                Add(key, name);
            }

            boundPropertyNames.AddRange(ExtractConverterBoundPropertyNames(text));
        }

        // C#: .Foreground への代入・名前に Foreground を含むメンバー・バインド先のプロパティ
        var seeds = boundPropertyNames.Distinct(StringComparer.Ordinal).ToList();
        var sources = EnumerateProductionFiles("*.cs")
            .Select(p => (Name: Path.GetFileName(p), Text: StripCSharpComments(File.ReadAllText(p))))
            .ToList();

        foreach (var (key, file) in CollectFromCSharp(sources, brushKeys, seeds))
        {
            Add(key, file);
        }

        return usages.Values.ToList();
    }

    private static IEnumerable<string> EnumerateProductionFiles(string pattern)
        => Directory.GetFiles(ProductionRoot, pattern, SearchOption.AllDirectories)
            .Where(p => !IsGeneratedOrIntermediate(p));

    private static bool IsGeneratedOrIntermediate(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.IndexOf(sep + "obj" + sep, StringComparison.Ordinal) >= 0
            || path.IndexOf(sep + "bin" + sep, StringComparison.Ordinal) >= 0
            || path.EndsWith(".g.cs", StringComparison.Ordinal)
            || path.EndsWith(".g.i.cs", StringComparison.Ordinal);
    }

    #endregion

    #region XAML の抽出

    /// <summary>
    /// XAML から、文字色として指定されているリソースキーを取り出す。
    /// </summary>
    internal static IEnumerable<string> ExtractXamlForegroundKeys(string xaml)
    {
        var keys = new List<string>();

        // ① 属性形（Foreground="{DynamicResource K}"）。
        //    TextElement.Foreground などの添付プロパティも接尾辞一致で拾う
        foreach (Match m in Regex.Matches(
            xaml,
            "(?:^|[\\s.])Foreground\\s*=\\s*(?:\"(?<v>[^\"]*)\"|'(?<v>[^']*)')"))
        {
            var key = FillForegroundPairs.ResourceKeyOf(m.Groups["v"].Value);
            if (key != null)
            {
                keys.Add(key);
            }
        }

        // ② Setter 形。属性順・TargetName の有無に依存しないよう、タグ全体を読んでから引く。
        //    開始タグの切り出しは引用符を見る共有ヘルパーへ寄せる（Issue #2102。`[^>]*` は属性値の `>` で途切れる）
        foreach (var setter in XamlElementInspection.EnumerateElements(xaml, "Setter"))
        {
            var property = XamlElementInspection.GetAttribute(setter.StartTag, "Property");
            if (property == null
                || !property.EndsWith("Foreground", StringComparison.Ordinal))
            {
                continue;
            }

            var key = FillForegroundPairs.ResourceKeyOf(XamlElementInspection.GetAttribute(setter.StartTag, "Value"));
            if (key != null)
            {
                keys.Add(key);
            }
        }

        return keys.Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// <c>Foreground="{Binding P, Converter={StaticResource ResourceKeyToBrushConverter}}"</c>
    /// の <c>P</c> を取り出す。
    /// </summary>
    /// <remarks>
    /// 同じコンバーターは <c>Fill</c> / <c>Stroke</c>（塗り・線）にも使われるため、
    /// <b>Foreground のバインドだけ</b>を対象にする。
    /// </remarks>
    internal static IEnumerable<string> ExtractConverterBoundPropertyNames(string xaml)
    {
        var names = new List<string>();

        foreach (Match m in Regex.Matches(
            xaml,
            "(?:^|[\\s.])Foreground\\s*=\\s*[\"']\\{Binding\\s+(?:Path=)?(?<prop>[A-Za-z0-9_.]+)"
                + "[^\"']*ResourceKeyToBrushConverter"))
        {
            var prop = m.Groups["prop"].Value;
            var lastDot = prop.LastIndexOf('.');
            names.Add(lastDot >= 0 ? prop.Substring(lastDot + 1) : prop);
        }

        return names.Distinct(StringComparer.Ordinal);
    }

    #endregion

    #region C# の抽出

    /// <summary>
    /// 参照を辿る深さの上限。
    /// </summary>
    /// <remarks>
    /// 実在する最長の経路は 2 段（XAML のバインド → ViewModel のプロパティ →
    /// <c>DiagnosticStatusPresenter</c> のメソッド）。上限が無いと、識別子を辿るうちに
    /// 無関係なメンバーまで到達して<b>塗りのキーを文字色として誤検出する</b>。
    /// </remarks>
    private const int MaxResolutionDepth = 3;

    /// <summary>
    /// 本番ソース全体から、文字色として流れるブラシキーと、それが書かれているファイルを集める。
    /// </summary>
    internal static IEnumerable<(string Key, string File)> CollectFromCSharp(
        IReadOnlyList<(string Name, string Text)> sources,
        IReadOnlyCollection<string> brushKeys,
        IReadOnlyCollection<string> boundPropertyNames)
    {
        var found = new HashSet<(string, string)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<(string Name, int Depth)>();

        foreach (var (file, text) in sources)
        {
            // 種 1: `X.Foreground = <式>;` の右辺
            foreach (Match m in Regex.Matches(text, "\\.Foreground\\s*=\\s*(?<expr>[^;]*);"))
            {
                Harvest(m.Groups["expr"].Value, file, 0);
            }

            // 種 2: 名前に Foreground を含むメンバー／変数の本体
            foreach (Match m in Regex.Matches(
                text, "\\b[A-Za-z0-9_]*[Ff]oreground[A-Za-z0-9_]*\\b"))
            {
                var body = ReadMemberOrStatementBody(text, m.Index + m.Length);
                if (body != null)
                {
                    Harvest(body, file, 0);
                }
            }
        }

        // 種 3: XAML のバインドが指しているプロパティ
        foreach (var name in boundPropertyNames)
        {
            pending.Enqueue((name, 0));
        }

        while (pending.Count > 0)
        {
            var (name, depth) = pending.Dequeue();
            if (depth >= MaxResolutionDepth || !visited.Add(name))
            {
                continue;
            }

            foreach (var (file, text) in sources)
            {
                foreach (var body in ReadDeclarationBodies(text, name))
                {
                    Harvest(body, file, depth + 1);
                }
            }
        }

        return found.Select(x => (x.Item1, x.Item2));

        void Harvest(string text, string file, int depth)
        {
            foreach (Match literal in Regex.Matches(text, "\"(?<key>[A-Za-z0-9_]+)\""))
            {
                var key = literal.Groups["key"].Value;
                if (brushKeys.Contains(key, StringComparer.Ordinal))
                {
                    found.Add((key, file));
                }
            }

            if (depth >= MaxResolutionDepth)
            {
                return;
            }

            // 辿る識別子は「ローカル変数（先頭小文字）」と「呼び出し（直後が `(`）」に限る。
            // 型名・名前空間まで辿ると、無関係なメンバーの本体からリテラルを拾ってしまう
            foreach (Match id in Regex.Matches(
                text, "\\b(?<name>[a-z_][A-Za-z0-9_]*)\\b|\\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*\\("))
            {
                pending.Enqueue((id.Groups["name"].Value, depth + 1));
            }
        }
    }

    /// <summary>
    /// 単一のソース断片を対象にした <see cref="CollectFromCSharp"/>（判定ロジックの固定用）。
    /// </summary>
    internal static IEnumerable<string> CollectFromCSharp(
        string source,
        IReadOnlyCollection<string> brushKeys,
        IReadOnlyCollection<string> boundPropertyNames)
        => CollectFromCSharp(
                new[] { ("(inline)", source) },
                brushKeys,
                boundPropertyNames)
            .Select(x => x.Key)
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// 指定した名前の「宣言・代入」の本体を返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 対象は 2 形。①代入（<c>var fgKey = …;</c> / <c>titleForegroundKey = …;</c>）と、
    /// ②メンバー宣言（<c>public static string GetForegroundResourceKey(…) { … }</c> /
    /// <c>public string P =&gt; …;</c>）。
    /// </para>
    /// <para>
    /// <b>呼び出し側は拾わない</b>。<c>Foo(</c> に一致するだけで本体を読むと、
    /// <c>string.IsNullOrEmpty(…)</c> のような無関係な呼び出しから後続のリテラルを
    /// 巻き込む。メンバー宣言は「戻り値の型 + 名前 + <c>(</c>」の形を要求する。
    /// </para>
    /// </remarks>
    private static IEnumerable<string> ReadDeclarationBodies(string source, string name)
    {
        var escaped = Regex.Escape(name);
        var bodies = new List<string>();

        // ① 代入（`==` を除く）
        foreach (Match m in Regex.Matches(source, "\\b" + escaped + "\\s*=(?!=)"))
        {
            var semicolon = source.IndexOf(';', m.Index);
            if (semicolon > m.Index)
            {
                bodies.Add(source.Substring(m.Index, semicolon - m.Index));
            }
        }

        // ② メンバー宣言（メソッド・プロパティ）
        foreach (Match m in Regex.Matches(
            source,
            "[A-Za-z0-9_<>\\[\\],?.]+\\s+" + escaped + "\\s*(?<tail>\\(|=>|\\{)"))
        {
            var body = ReadMemberOrStatementBody(source, m.Groups["tail"].Index);
            if (body != null)
            {
                bodies.Add(body);
            }
        }

        return bodies;
    }

    /// <summary>
    /// 識別子の直後から、その識別子が属する「本体」の文字列を返す。
    /// </summary>
    /// <remarks>
    /// 引数リスト <c>( … )</c> は読み飛ばし、最初に現れたのが <c>{</c> ならブロックを
    /// 対応する <c>}</c> まで、<c>=</c>（<c>=&gt;</c> を含む）なら式として <c>;</c> まで、
    /// <c>;</c> なら本体を持たない参照として <c>null</c> を返す。
    /// </remarks>
    private static string ReadMemberOrStatementBody(string source, int start)
    {
        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];

            if (c == '(')
            {
                var closing = FindMatching(source, i, '(', ')');
                if (closing < 0)
                {
                    return null;
                }

                i = closing;
                continue;
            }

            if (c == ';')
            {
                return null;
            }

            if (c == '{')
            {
                return ReadBalancedBlock(source, i);
            }

            if (c == '=')
            {
                var semicolonAt = source.IndexOf(';', i);
                return semicolonAt < 0 ? null : source.Substring(i, semicolonAt - i);
            }
        }

        return null;
    }

    private static int FindMatching(string source, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < source.Length; i++)
        {
            if (source[i] == open)
            {
                depth++;
            }
            else if (source[i] == close)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string ReadBalancedBlock(string source, int openBraceIndex)
    {
        var closing = FindMatching(source, openBraceIndex, '{', '}');
        return closing < 0 ? null : source.Substring(openBraceIndex, closing - openBraceIndex + 1);
    }

    #endregion

    #region 色値の解決

    private static string ResolveColor(IDictionary<string, string> brushes, string key)
    {
        brushes.Should().ContainKey(
            key,
            "文字色 {0} は AccessibilityStyles.xaml に #RRGGBB 形式で定義されているべき"
                + "（色値リテラルの直書き・名前付きの色は禁止。Issue #1822 / #2074）",
            key);

        return brushes[key];
    }

    /// <summary>
    /// C# のコメントを除去する（文字列リテラルは残す）。
    /// </summary>
    /// <remarks>
    /// 素の正規表現で <c>//</c> を消すと <c>"http://…"</c> のような文字列リテラルの中身まで
    /// 消えて引用符の対応が崩れ、以後のリテラル抽出が別の場所を見る。
    /// 私的コピーを増やさず <see cref="TestSourceInspection"/> のリテラル対応版へ委譲する。
    /// </remarks>
    private static string StripCSharpComments(string source)
        => TestSourceInspection.RemoveCommentsPreservingLines(source);

    #endregion
}
