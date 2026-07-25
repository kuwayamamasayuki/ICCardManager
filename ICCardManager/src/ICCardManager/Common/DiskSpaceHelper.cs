using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ICCardManager.Common
{
    /// <summary>
    /// 指定パスが属するボリュームの空き容量を取得するヘルパー（Issue #1689）
    /// </summary>
    /// <remarks>
    /// <para>
    /// .NET Framework の <see cref="DriveInfo"/> は UNC パス（<c>\\server\share\...</c>）を
    /// 受け付けず <see cref="ArgumentException"/> を投げるため使用できない。共有フォルダモードでは
    /// バックアップ保存先が UNC になり得るので、UNC を直接扱える Win32 API
    /// <c>GetDiskFreeSpaceEx</c> を P/Invoke する。
    /// </para>
    /// <para>
    /// 取得するのは <c>lpFreeBytesAvailable</c>（呼び出しユーザーが実際に使える空き容量）であり、
    /// ディスククォータが設定された共有フォルダでもユーザー視点の正しい値になる。
    /// </para>
    /// </remarks>
    internal static class DiskSpaceHelper
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailable,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

        /// <summary>
        /// 指定フォルダの利用可能な空き容量（バイト）を取得する
        /// </summary>
        /// <param name="folderPath">対象フォルダのパス（末尾の区切り文字は不要）</param>
        /// <returns>
        /// 取得できた場合はバイト数。パスが空・存在しない・アクセスできない等で取得できなかった場合は null。
        /// 呼び出し側は null を「不明」として扱い、例外にしないこと（空き容量は補助情報であり、
        /// 取得できなくてもバックアップ自体は継続できるため）。
        /// </returns>
        public static long? TryGetAvailableFreeSpace(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return null;

            try
            {
                // GetDiskFreeSpaceEx は末尾に区切り文字が付いたディレクトリ名を要求する場合があるため付与する。
                var normalized = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                 + Path.DirectorySeparatorChar;

                if (!GetDiskFreeSpaceEx(normalized, out var freeBytesAvailable, out _, out _))
                    return null;

                // long の範囲を超える巨大ボリュームは現実的に存在しないが、
                // ulong → long のオーバーフローで負値になるのを防ぐ。
                return freeBytesAvailable > long.MaxValue ? long.MaxValue : (long)freeBytesAvailable;
            }
            catch (Exception)
            {
                // DllNotFoundException / ArgumentException 等。空き容量は補助情報のため握りつぶす。
                return null;
            }
        }

        /// <summary>
        /// バイト数を人間が読める単位（GB / MB）に整形する
        /// </summary>
        /// <param name="bytes">バイト数。null の場合は「不明」を返す</param>
        /// <returns>「12.3 GB」のような文字列</returns>
        public static string FormatBytes(long? bytes)
        {
            if (bytes == null)
                return "不明";

            const double Gb = 1024d * 1024d * 1024d;
            const double Mb = 1024d * 1024d;

            if (bytes.Value >= Gb)
                return $"{bytes.Value / Gb:N1} GB";
            if (bytes.Value >= Mb)
                return $"{bytes.Value / Mb:N1} MB";

            return $"{bytes.Value:N0} バイト";
        }
    }
}
