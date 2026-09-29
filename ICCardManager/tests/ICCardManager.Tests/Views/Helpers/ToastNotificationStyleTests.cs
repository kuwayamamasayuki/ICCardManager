using System;
using System.Linq;
using FluentAssertions;
using ICCardManager.Views;
using Xunit;

namespace ICCardManager.Tests.Views.Helpers;

/// <summary>
/// Issue #2141: トースト通知の種類ごとの見た目・表示時間と、自動では消えない通知の結線を固定する。
/// </summary>
/// <remarks>
/// <para>
/// 警告（<see cref="ToastType.Warning"/>）は貸出のブラシ（<c>Lending*</c>）を流用しており、
/// 「返却は記録済み・再タッチしないでください」が貸出と同じ暖色で 3 秒で消えていた。
/// <c>Lending*</c> は貸出のシグナル専用で、返却フローに使わない（#2079）。
/// </para>
/// <para>
/// 窓（<see cref="ToastNotificationWindow"/>）は STA 依存で xUnit から生成できないため、
/// 見た目と表示時間は純関数で、窓への結線はソーステキストの静的検査で固定する。
/// </para>
/// </remarks>
public class ToastNotificationStyleTests
{
    private static ToastType[] AllTypes => Enum.GetValues(typeof(ToastType)).Cast<ToastType>().ToArray();

    [Fact]
    public void 警告は警告専用のブラシで表示すること()
    {
        ToastNotificationWindow.GetBackgroundKey(ToastType.Warning).Should().Be("WarningBackgroundBrush");
        ToastNotificationWindow.GetBorderKey(ToastType.Warning).Should().Be("WarningBorderBrush");
        ToastNotificationWindow.GetTitleForegroundKey(ToastType.Warning).Should().Be("WarningForegroundBrush");
    }

    [Fact]
    public void 貸出のブラシは貸出の通知にだけ使うこと()
    {
        // 種類を足したときに Lending* を流用する形が戻らないよう、全種類を走査する（#1739「列挙型の全値で固定する」）
        foreach (var type in AllTypes.Where(t => t != ToastType.Lend))
        {
            new[]
            {
                ToastNotificationWindow.GetBackgroundKey(type),
                ToastNotificationWindow.GetBorderKey(type),
                ToastNotificationWindow.GetTitleForegroundKey(type),
            }.Should().NotContain(k => k.StartsWith("Lending"), $"{type} は貸出のシグナルではない（#2079）");
        }

        // 対の表明: 貸出の通知は貸出の色のまま（全種類から Lending* を外す実装を落とす）
        ToastNotificationWindow.GetBackgroundKey(ToastType.Lend).Should().Be("LendingBackgroundBrush");
    }

    [Fact]
    public void 全種類に見た目が定義されていること()
    {
        foreach (var type in AllTypes)
        {
            ToastNotificationWindow.GetIconText(type).Should().NotBeNullOrEmpty();
            ToastNotificationWindow.GetBackgroundKey(type).Should().EndWith("Brush");
            ToastNotificationWindow.GetBorderKey(type).Should().EndWith("Brush");
            ToastNotificationWindow.GetTitleForegroundKey(type).Should().EndWith("Brush");
        }
    }

    [Fact]
    public void 警告は貸出返却の通知より長く表示すること()
    {
        ToastNotificationWindow.GetDisplayDuration(ToastType.Warning).Should().Be(TimeSpan.FromSeconds(10));
        ToastNotificationWindow.GetDisplayDuration(ToastType.Lend).Should().Be(TimeSpan.FromSeconds(3),
            "対の表明: 貸出・返却の通知まで長くして職員の操作を塞がない");
        ToastNotificationWindow.GetDisplayDuration(ToastType.Return).Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void 自動では消えない通知の案内がキーでも閉じられることを述べること()
    {
        // 閉じる手段を増やしたら、それを述べる文言も直す（#2077 / #2078）
        ToastNotificationWindow.PersistentCloseHint.Should().Be("クリックまたは Esc キーで閉じる");
    }

    [Fact]
    public void 自動では消えない通知を置き場へ置いて1枚に差し替えること()
    {
        var source = ProductionSourceFiles.CSharp
            .Single(f => f.Name == "ToastNotificationWindow.xaml.cs").CodeOnly;

        source.Should().Contain("PersistentSlot.Put(toast)",
            "autoClose: false の通知を置き場へ置かないと、同じ画面隅へ重ねて積み上がる");
        source.Should().Contain("PersistentSlot.Dismiss()",
            "次の職員証タッチ・Esc で閉じる入口");
        source.Should().Contain("PersistentSlot.Release(this)",
            "クリックで閉じた通知が置き場に残ると、次の差し替えで閉じ済みの窓を閉じようとする");
    }

    [Fact]
    public void 記録済みの案内は警告の見た目で自動では消さないこと()
    {
        var source = ProductionSourceFiles.CSharp
            .Single(f => f.Name == "ToastNotificationService.cs").CodeOnly;

        source.Should().MatchRegex(
            @"ShowRecordedNotice\([^)]*\)\s*\{\s*ToastNotificationWindow\.Show\(ToastType\.Warning,[^;]*autoClose:\s*false\)",
            "再タッチを止める指示が 3 秒で消えると、見逃した職員の再タッチが逆の操作として記録される");
        source.Should().MatchRegex(
            @"ShowWarning\([^)]*\)\s*\{\s*ToastNotificationWindow\.Show\(ToastType\.Warning,\s*title,\s*message\)",
            "対の表明: 一般の警告は自動で消える（すべての警告を残すと同じ画面隅に重なる）");
    }

    #region Issue #2142: 補足行の読みやすさと読み上げ

    [Fact]
    public void 本文と補足行の文字色が全種類の背景で4対5対1以上あること()
    {
        // Issue #2142: 補足行は Opacity 0.6 で薄くしており、4 種の背景すべてで約 3.1:1 だった。
        // 色の検査（ForegroundContrastConventionTests）はブラシしか見ないので、不透明度は素通りしていた。
        // 文字色はスタイル辞書から読む（テスト側に色値を書き写さない。#1821）
        var brushes = AccessibilityBrushes.Load();

        foreach (var type in AllTypes)
        {
            var background = brushes[ToastNotificationWindow.GetBackgroundKey(type)];
            foreach (var key in new[] { ToastNotificationWindow.MessageForegroundKey, ToastNotificationWindow.SubMessageForegroundKey })
            {
                ColorMetrics.Contrast(brushes[key], background).Should().BeGreaterOrEqualTo(
                    4.5, "{0} の通知の背景（{1}）の上の {2} が読めること", type, background, key);
            }
        }
    }

    [Fact]
    public void 通知の文字を不透明度で薄くしないこと()
    {
        // 対の表明: 色を選び直しても、XAML に Opacity が残っていれば実効色は地色へ寄る
        var xaml = ProductionSourceFiles.Xaml.Single(f => f.Name == "ToastNotificationWindow.xaml").Text;
        var textBlocks = XamlElementInspection.EnumerateElementsIncludingNested(
            XamlElementInspection.StripXmlComments(xaml), "TextBlock").ToList();

        textBlocks.Should().HaveCountGreaterOrEqualTo(3, "タイトル・本文・補足行が走査対象に含まれること");
        textBlocks.Where(t => XamlElementInspection.GetAttribute(t.StartTag, "Opacity") != null)
            .Select(t => t.Line)
            .Should().BeEmpty("文字の階層は Opacity ではなく文字色（SubMessageForegroundKey）で表すこと");
    }

    [Fact]
    public void 補足行は本文より控えめな文字色であること()
    {
        // Opacity を外しただけで本文と同じ色にすると、残額不足の警告と閉じ方の案内が本文と区別できなくなる
        ToastNotificationWindow.SubMessageForegroundKey.Should().NotBe(ToastNotificationWindow.MessageForegroundKey);

        var source = ProductionSourceFiles.CSharp
            .Single(f => f.Name == "ToastNotificationWindow.xaml.cs").CodeOnly;
        source.Should().Contain("SubMessageText.Foreground = ResolveBrush(SubMessageForegroundKey)",
            "補足行へ補足行用の文字色を当てること（本文の色を流用しない）");
        source.Should().Contain("MessageText.Foreground = ResolveBrush(MessageForegroundKey)");
    }

    [Theory]
    [InlineData("おかえりなさい！", "はやかけん 001\n残額: 1,200円", null, "おかえりなさい！ はやかけん 001 残額: 1,200円")]
    [InlineData("おかえりなさい！", "はやかけん 001", "⚠️ 残額不足（10,000円以下）", "おかえりなさい！ はやかけん 001 ⚠️ 残額不足（10,000円以下）")]
    [InlineData("エラー", "読み取りに失敗しました", "", "エラー 読み取りに失敗しました")]
    [InlineData("記録済み", "返却は記録済みです", "再タッチしないでください\r\n（クリックまたは Esc キーで閉じる）",
        "記録済み 返却は記録済みです 再タッチしないでください （クリックまたは Esc キーで閉じる）")]
    public void 読み上げる内容は表示している行をすべて表示順に含むこと(string title, string message, string? sub, string expected)
    {
        // Issue #2142: UIA は LiveRegionChanged で通知した要素の Name を読む。窓の Name が固定の
        // 「通知ウィンドウ」だったため、通知しても結果（カード名・残額・エラー内容）は伝わらなかった
        ToastNotificationWindow.BuildAnnouncement(title, message, sub)
            .Should().Be(expected);
    }

    [Fact]
    public void 通知の表示のたびに内容を名前にして読み上げを発火すること()
    {
        var code = ProductionSourceFiles.CSharp
            .Single(f => f.Name == "ToastNotificationWindow.xaml.cs").CodeOnly;
        var show = TestSourceInspection.ExtractMethodBody(code, "public static void Show(ToastType type,");

        show.Should().Contain("AutomationProperties.SetName(toast, BuildAnnouncement(",
            "読み上げられる Name は通知の内容そのものにすること（固定のラベルにしない）");
        show.Should().Contain("LiveRegionAnnouncer.Announce(toast,",
            "LiveSetting を付けただけでは LiveRegionChanged は発火しない（#1509 / #1548）");
        show.IndexOf("AutomationProperties.SetName(toast", System.StringComparison.Ordinal)
            .Should().BeLessThan(show.IndexOf("LiveRegionAnnouncer.Announce(toast", System.StringComparison.Ordinal),
                "名前を内容にしてから通知すること（逆だと前の通知の内容が読まれる）");

        var xaml = XamlElementInspection.StripXmlComments(
            ProductionSourceFiles.Xaml.Single(f => f.Name == "ToastNotificationWindow.xaml").Text);
        var root = XamlElementInspection.GetRootStartTag(xaml)!;
        XamlElementInspection.GetAttribute(root, "AutomationProperties.Name").Should().BeNull(
            "窓に固定の Name を置くと、コードビハインドが内容を設定するまでの間・設定を忘れた経路で固定のラベルが読まれる");
        XamlElementInspection.GetAttribute(root, "AutomationProperties.HelpText").Should().Be(
            "通知ウィンドウ", "固定のラベルは補足（HelpText）として残す（UI テストの識別にも使う）");
        XamlElementInspection.GetAttribute(root, "AutomationProperties.LiveSetting").Should().Be("Assertive");
    }

    #endregion
}
