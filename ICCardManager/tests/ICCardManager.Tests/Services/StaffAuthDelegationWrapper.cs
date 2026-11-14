using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ICCardManager.Tests.Services;

/// <summary>
/// 職員認証の包み（<c>ModalCardReadingSuppressingStaffAuthService</c>。Issue #2484）を、職員認証の呼び出しの静的検査から
/// 除くための共有の定義。
/// </summary>
/// <remarks>
/// <para>
/// 包みの中の <c>RequestAuthenticationAsync</c> の呼び出しは、認証結果を受けて使うのではなく、受け取った操作名で呼んで結果を
/// 呼び出し元へそのまま返す委譲なので、「その場で Enter して using で受ける」「操作名はリテラル」の対象にしない。
/// </para>
/// <para>
/// 除外は<b>この 1 ファイルだけ</b>に固定する（PR #2491 のレビュー 4 回目）。「<c>class X : IStaffAuthService</c> を含むファイル」の
/// ように形で除くと、画面のファイルに入れ子の実装を 1 つ置く（<c>#if</c> で無効にしたものでもよい）だけで、そのファイルの
/// 本物の違反が検査から外れる。代わりに、除いたファイルの中身が委譲だけであること（呼び出しがちょうど 1 件で、受け取った
/// 引数をそのまま渡す）を <see cref="FindDelegationViolations"/> で表明する。
/// </para>
/// </remarks>
internal static class StaffAuthDelegationWrapper
{
    /// <summary>除外する唯一のファイル（本番ソースのルートからの相対パス）</summary>
    public static readonly string RelativePath = Path.Combine("ViewModels", "ModalCardReadingSuppressingStaffAuthService.cs");

    private static readonly Regex AuthCall = new(@"\.RequestAuthenticationAsync\(");

    private static readonly Regex PassThroughCall = new(@"\b_inner\s*\.\s*RequestAuthenticationAsync\(\s*operationDescription\s*\)");

    private static readonly Regex Signature = new(@"\bRequestAuthenticationAsync\(\s*string\s+operationDescription\s*\)");

    /// <summary><paramref name="fullPath"/> が除外する包みのファイルか（ファイル名ではなく本番ソースの中の位置で照合する）</summary>
    public static bool IsWrapperFile(string fullPath)
        => string.Equals(
            Path.GetFullPath(fullPath),
            Path.GetFullPath(Path.Combine(TestPaths.GetProductionSourceRoot(), RelativePath)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 包みのコード（コメントを除いたもの）が委譲だけでないなら、その理由を列挙する。
    /// </summary>
    public static IReadOnlyList<string> FindDelegationViolations(string code)
    {
        var findings = new List<string>();
        var calls = AuthCall.Matches(code).Count;
        if (calls != 1)
        {
            findings.Add($"RequestAuthenticationAsync の呼び出しが {calls} 件（委譲の 1 件だけであること）");
        }

        if (PassThroughCall.Matches(code).Count != 1)
        {
            findings.Add("受け取った operationDescription をそのまま _inner へ渡す呼び出しが無い");
        }

        if (!Signature.IsMatch(code))
        {
            findings.Add("RequestAuthenticationAsync(string operationDescription) の実装が無い");
        }

        return findings;
    }
}
