using System;
using System.Reflection;

namespace ICCardManager.Common
{
    /// <summary>
    /// アプリケーション自身のバージョン情報を提供する静的ヘルパー（Issue #1687）
    /// </summary>
    /// <remarks>
    /// App.xaml.cs（WPF層）以外のData層・Services層からもバージョン番号を参照できるよう、
    /// アセンブリバージョンの取得を一元化する。バージョンの実体は csproj の
    /// &lt;AssemblyVersion&gt; で管理される。
    /// </remarks>
    public static class AppVersionInfo
    {
        /// <summary>
        /// 現在のアプリケーションバージョン（Major.Minor.Build の3要素に正規化）
        /// </summary>
        /// <remarks>
        /// AssemblyVersion は 4 要素（2.10.0.0）だが、Revision を含めたまま
        /// 3 要素表記（latest_version.txt 等）と比較すると "2.10.0.0" &gt; "2.10.0" と
        /// 誤判定されるため、Revision を落とした 3 要素で返す。
        /// </remarks>
        public static Version Current
        {
            get
            {
                var version = typeof(AppVersionInfo).Assembly.GetName().Version;
                return version == null
                    ? new Version(0, 0, 0)
                    : new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
            }
        }

        /// <summary>
        /// 現在のアプリケーションバージョン文字列（例: "2.10.0"）
        /// </summary>
        public static string CurrentString => Current.ToString(3);

        /// <summary>
        /// バージョン文字列を Major.Minor.Build の3要素に正規化してパースする
        /// </summary>
        /// <param name="text">バージョン文字列（先頭の "v"/"V" と前後空白は無視。例: "v2.11.0"）</param>
        /// <param name="version">パース結果（3要素に正規化済み）</param>
        /// <returns>パースに成功した場合true</returns>
        public static bool TryParseNormalized(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var trimmed = text.Trim();
            if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(1);

            if (!Version.TryParse(trimmed, out var parsed))
                return false;

            // "2.11"（2要素）も許容し、Build は 0 とみなす
            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
            return true;
        }
    }
}
