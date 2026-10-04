using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2163: 本体の Null 許容参照型をファイル単位で段階的に有効化する規約の静的検査。
/// </summary>
/// <remarks>
/// <para>
/// 本体（<c>src/ICCardManager</c>）は csproj で <c>&lt;Nullable&gt;</c> を宣言しておらず（C# 10 の既定で無効）、
/// 一括で有効にすると数千件の警告になって「ビルド警告ゼロ」と両立しない。そこで<b>新規ファイルと改修したファイル</b>の
/// 先頭へ <c>#nullable enable</c> を付け、移行を日常の改修に乗せる。
/// </para>
/// <para>
/// 「新規ファイルに付いていること」を git の追加履歴で見る形や、未移行ファイルの一覧を許可リストにする形は採らない。
/// 前者はテストが git の状態に依存し、後者は移行が進むたびに一覧から消す作業を強いる誤検出になる（#1786）。
/// 代わりに<b>「<c>#nullable enable</c> の無いファイル数」を上限値で固定</b>する（ラチェット）。新しいファイルを
/// 付けずに足すと上限を超えて赤くなり、移行したのに上限を下げ忘れると実数との差で赤くなる — 下げ忘れを許すと、
/// その余裕の分だけ新規ファイルが付けずに入り込めてしまうため。
/// </para>
/// <para>
/// 層ごとの移行が終わったディレクトリは <see cref="MigratedDirectories"/> に載せ、その配下は 1 件の例外も許さない。
/// 上限値は全体の件数しか見ないので、「移行済みの層へ付け忘れたファイルを足し、別の場所で 1 件移行する」形を
/// 上限値だけでは検出できないため。
/// </para>
/// </remarks>
public class NullableContextConventionTests
{
    /// <summary>
    /// <c>#nullable enable</c> がファイル全体に効いていない本番 <c>.cs</c> の数の上限。
    /// </summary>
    /// <remarks>
    /// <b>移行したら、この値を実数まで下げること</b>（<see cref="移行が進んだら上限を実数まで下げていること"/> が赤くなって促す）。
    /// <b>上げてはならない</b>。新しいファイルを足すなら、そのファイルの先頭に <c>#nullable enable</c> を付ける。
    /// 0 になったら csproj に <c>&lt;Nullable&gt;enable&lt;/Nullable&gt;</c> を置き、各ファイルの
    /// <c>#nullable enable</c> と <c>NoWarn</c> の CS8632 を外す（Issue #2163 の手順 4）。
    /// </remarks>
    private const int MaxFilesWithoutNullableEnable = 206;

    /// <summary>
    /// 移行を終えたディレクトリ（本番ソースのルートからの相対パス。入れ子を含む）。
    /// 層ごとの移行が終わったら追加する。<b>減らしてはならない</b>。
    /// </summary>
    private static readonly string[] MigratedDirectories =
    {
        // Issue #2163: 依存の少ない純関数群から着手した。
        "Common",
    };

    /// <summary>
    /// net48 に無いため <c>Common/Polyfills/NullableAttributes.cs</c> で定義している属性。
    /// </summary>
    public static IEnumerable<object[]> PolyfilledAttributeNames => new[]
    {
        "AllowNullAttribute",
        "DisallowNullAttribute",
        "MaybeNullAttribute",
        "NotNullAttribute",
        "MaybeNullWhenAttribute",
        "NotNullWhenAttribute",
        "NotNullIfNotNullAttribute",
        "DoesNotReturnAttribute",
        "DoesNotReturnIfAttribute",
        "MemberNotNullAttribute",
        "MemberNotNullWhenAttribute",
    }.Select(name => new object[] { name });

    #region 実データの検査

    [Fact]
    public void nullable_enableの無いファイル数が上限を超えていないこと()
    {
        var withoutEnable = FilesWithoutFileWideNullableEnable();

        withoutEnable.Count.Should().BeLessOrEqualTo(MaxFilesWithoutNullableEnable,
            "新しく追加した .cs の先頭には #nullable enable を付けること（Issue #2163）。" +
            "上限値を上げて通さないこと。\n" +
            "#nullable enable がファイル全体に効いていないファイル:\n" +
            string.Join("\n", withoutEnable.Select(f => $"  - {f}")));
    }

    [Fact]
    public void 移行が進んだら上限を実数まで下げていること()
    {
        var count = FilesWithoutFileWideNullableEnable().Count;

        count.Should().BeGreaterOrEqualTo(MaxFilesWithoutNullableEnable,
            $"#nullable enable の無いファイルが {count} 件に減っている。" +
            $"NullableContextConventionTests.MaxFilesWithoutNullableEnable を {count} へ下げること" +
            "（下げずに残した余裕の分だけ、新しいファイルが付けずに入り込めるため。Issue #2163）");
    }

    [Fact]
    public void 移行済みのディレクトリにはnullable_enableの無いファイルが無いこと()
    {
        foreach (var directory in MigratedDirectories)
        {
            var files = ProductionSourceFiles.CSharp.Under(directory).ToList();
            files.Should().NotBeEmpty($"移行済みディレクトリ {directory} が実在し、走査できていること");

            var violations = files
                .Where(f => !HasFileWideNullableEnableInCodeOnly(f.CodeOnlyPreservingLines))
                .Select(f => $"  - {f.RelativePath}")
                .ToList();

            violations.Should().BeEmpty(
                $"{directory} は Null 許容参照型へ移行済み。配下の .cs には先頭に #nullable enable を付けること" +
                "（Issue #2163）。\n" + string.Join("\n", violations));
        }
    }

    /// <summary>
    /// 走査が空振りすると、上限値の検査は「0 件 ≦ 上限」で無条件に緑になる。
    /// 判定が実際に両方の値を返していることを、実データで表明する。
    /// </summary>
    [Fact]
    public void 走査対象が実在し判定が両方の値を返すこと()
    {
        var files = ProductionSourceFiles.CSharp;
        files.Should().HaveCountGreaterThan(100, "本番ソースの走査が空振りしていないこと");

        files.Should().Contain(f => HasFileWideNullableEnableInCodeOnly(f.CodeOnlyPreservingLines),
            "移行済みのファイル（Common 配下）を enable と判定できていること");
        files.Should().Contain(f => !HasFileWideNullableEnableInCodeOnly(f.CodeOnlyPreservingLines),
            "未移行のファイルを enable でないと判定できていること（全件 true になる判定は上限値の検査を無力化する）");
    }

    #endregion

    #region Polyfill

    /// <summary>
    /// Null 許容のフロー解析に使う属性が本体アセンブリに揃っていること。
    /// </summary>
    /// <remarks>
    /// コンパイラは名前空間と型名で属性を認識するため、net48 でも同名の型があれば解析が働く。
    /// <b>internal</b> であることも表明する — public にすると、本体を参照する一般のアセンブリへ型が公開され、
    /// 参照側の同名の型と衝突する。InternalsVisibleTo の相手（本テスト・DebugDataViewer）には internal でも見えるため、
    /// そちらで同名の型を定義すると CS0436 になる（自前で定義せず本体の定義を使う）。
    /// </remarks>
    [Theory]
    [MemberData(nameof(PolyfilledAttributeNames))]
    public void Null許容の属性が本体にinternalで定義されていること(string attributeName)
    {
        var type = typeof(ServiceResult).Assembly.GetType("System.Diagnostics.CodeAnalysis." + attributeName);

        type.Should().NotBeNull($"{attributeName} の Polyfill が本体に定義されていること");
        type!.IsPublic.Should().BeFalse("Polyfill は internal にする（参照側の同名型と衝突させないため）");
        type.IsSealed.Should().BeTrue();
        typeof(Attribute).IsAssignableFrom(type).Should().BeTrue();
        type.GetCustomAttribute<AttributeUsageAttribute>().Should().NotBeNull(
            "適用先（引数・戻り値・メンバー）を BCL と同じに制限すること");
    }

    /// <summary>
    /// 属性は宣言しただけでは効かず、移行したファイルが実際に使って初めて呼び出し元の解析に届く。
    /// 代表として <c>TryGet…</c> 形と、フラグで非 null を約束する形が本体に付いていることを固定する。
    /// </summary>
    [Fact]
    public void 移行したファイルがPolyfillの属性を使っていること()
    {
        var tryParse = typeof(AppVersionInfo).GetMethod(nameof(AppVersionInfo.TryParseNormalized));
        tryParse.Should().NotBeNull();
        tryParse!.GetParameters()[1].CustomAttributes
            .Should().Contain(a => a.AttributeType.Name == "NotNullWhenAttribute",
                "out 引数に [NotNullWhen(true)] を付け、成功時に非 null であることを呼び出し元へ伝える");

        var isDecoded = typeof(TextDecodeResult).GetProperty(nameof(TextDecodeResult.IsDecoded));
        isDecoded.Should().NotBeNull();
        isDecoded!.CustomAttributes
            .Should().Contain(a => a.AttributeType.Name == "MemberNotNullWhenAttribute",
                "IsDecoded が true なら Text が非 null であることを呼び出し元へ伝える");
    }

    #endregion

    #region 判定ロジックの固定（サンプル入力）

    [Theory]
    [InlineData("#nullable enable\r\n\r\nusing System;\r\n")]
    [InlineData("#nullable enable\nnamespace N { class C { } }\n")]
    [InlineData("﻿#nullable enable\r\nusing System;\r\n")]
    [InlineData("// ヘッダーコメント\r\n/* ブロック */\r\n#nullable enable\r\nusing System;\r\n")]
    [InlineData("\r\n\r\n#nullable enable // 理由\r\nusing System;\r\n")]
    [InlineData("  #  nullable   enable\r\nusing System;\r\n")]
    [InlineData("#nullable enable\r\nclass C\r\n{\r\n#if DEBUG\r\n    int x;\r\n#endif\r\n}\r\n")]
    public void 判定_先頭のnullable_enableをファイル全体へ効いていると判定すること(string source)
    {
        HasFileWideNullableEnable(source).Should().BeTrue();
    }

    [Theory]
    [InlineData("using System;\r\n", "ディレクティブが無い")]
    [InlineData("", "空のファイル")]
    [InlineData("using System;\r\n#nullable enable\r\nclass C { }\r\n", "コードの後ろにあると、手前の部分が無効のまま")]
    [InlineData("#nullable enable warnings\r\nusing System;\r\n", "warnings だけでは注釈が無効（? を付けられない）")]
    [InlineData("#nullable enable annotations\r\nusing System;\r\n", "annotations だけでは警告が出ない")]
    [InlineData("#nullable enable\r\nclass A { }\r\n#nullable restore\r\nclass B { }\r\n", "本体の restore はプロジェクト既定（無効）へ戻す")]
    [InlineData("#nullable enable\r\nclass A { }\r\n#nullable disable warnings\r\nclass B { }\r\n", "途中で一部を無効にしている")]
    [InlineData("#region R\r\n#nullable enable\r\n#endregion\r\nusing System;\r\n", "他のディレクティブより後ろ（規約は最初の行に置く）")]
    [InlineData("#if DEBUG\r\n#nullable enable\r\n#endif\r\nusing System;\r\n", "#if の中は構成によって効かない")]
    [InlineData("// #nullable enable\r\nusing System;\r\n", "コメントの中は数えない")]
    [InlineData("class C { const string S = @\"\r\n#nullable enable\r\n\"; }\r\n", "文字列リテラルの中は数えない")]
    public void 判定_ファイル全体へ効いていない形をenableと判定しないこと(string source, string reason)
    {
        HasFileWideNullableEnable(source).Should().BeFalse(reason);
    }

    #endregion

    /// <summary>
    /// <paramref name="source"/> の Null 許容コンテキストが、ファイル全体で注釈・警告とも有効か。
    /// </summary>
    /// <remarks>
    /// 条件は ①コード（空行・コメントを除く最初の行）が <c>#nullable enable</c> であること
    /// ②以降に Null 許容コンテキストを変えるディレクティブが無いこと。
    /// 本体はプロジェクト既定が無効なので、<c>#nullable restore</c> も「無効へ戻す」に当たる。
    /// <c>#nullable disable</c> 自体は <see cref="BuildWarningSuppressionConventionTests"/> がソース全体で禁じているが、
    /// ここでも「効いていない」と数える（片方の検査が外れても、もう片方で数が合わなくなるように）。
    /// コメント・文字列リテラルの中のディレクティブは数えない。
    /// </remarks>
    internal static bool HasFileWideNullableEnable(string source)
        => HasFileWideNullableEnableInCodeOnly(TestSourceInspection.ToCodeOnlyPreservingLines(source.TrimStart('﻿')));

    /// <summary>
    /// <see cref="HasFileWideNullableEnable"/> の、コメントと文字列リテラルを剥がし済みの入力版
    /// （実データは <see cref="ProductionSourceFiles.SourceFile.CodeOnlyPreservingLines"/> のキャッシュを使う）。
    /// </summary>
    /// <remarks>
    /// <c>#region</c> / <c>#if</c> など他のディレクティブが <c>#nullable enable</c> より前にある形も「有効でない」と数える
    /// （<c>#if</c> の中に置くと構成によって効かなくなるため。安全側に倒し、規約も「最初の行」に置くことにしている）。
    /// </remarks>
    private static bool HasFileWideNullableEnableInCodeOnly(string codeOnly)
    {
        var codeLines = codeOnly
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (codeLines.Count == 0 || !IsFullNullableEnable(codeLines[0]))
        {
            return false;
        }

        return !codeLines.Skip(1).Any(IsNullableDirective);
    }

    private static bool IsFullNullableEnable(string trimmedLine)
        => Regex.IsMatch(trimmedLine, @"^#\s*nullable\s+enable$");

    private static bool IsNullableDirective(string trimmedLine)
        => Regex.IsMatch(trimmedLine, @"^#\s*nullable\b");

    private static List<string> FilesWithoutFileWideNullableEnable()
        => ProductionSourceFiles.CSharp
            .Where(f => !HasFileWideNullableEnableInCodeOnly(f.CodeOnlyPreservingLines))
            .Select(f => f.RelativePath)
            .ToList();
}
