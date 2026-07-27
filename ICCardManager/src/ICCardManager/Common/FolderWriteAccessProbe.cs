using System;
using System.IO;

namespace ICCardManager.Common
{
    /// <summary>
    /// フォルダへの書き込み可否の実測結果（Issue #1690）
    /// </summary>
    public enum FolderWriteAccess
    {
        /// <summary>書き込みできた</summary>
        Writable,

        /// <summary>パスが指定されていない</summary>
        PathNotSpecified,

        /// <summary>フォルダが存在しない（ネットワーク切断・共有名変更など）</summary>
        FolderNotFound,

        /// <summary>フォルダは見えるが書き込み権限がない</summary>
        AccessDenied,

        /// <summary>上記以外の理由で書き込めなかった（ディスク満杯・I/Oエラーなど）</summary>
        Failed
    }

    /// <summary>
    /// 指定フォルダへ実際に書き込めるかを試して確認するヘルパー（Issue #1690）
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Directory.Exists"/> だけでは「見えるが書けない」読み取り専用共有を検出できず、
    /// ACL の解析は共有フォルダ（SMB）越しでは実効権限と一致しないことがある。
    /// そのため実際に一時ファイルを作成して確かめる方式を採る。
    /// </para>
    /// <para>
    /// 一時ファイルは <see cref="FileOptions.DeleteOnClose"/> で作成するため、
    /// 途中で例外が発生してもハンドルが閉じられた時点で OS が削除する。
    /// 診断がゴミファイルを残さないことを構造的に保証するためであり、
    /// 明示的な <see cref="File.Delete(string)"/> には頼らない。
    /// </para>
    /// </remarks>
    public static class FolderWriteAccessProbe
    {
        /// <summary>
        /// 一時ファイル名の接頭辞。万一残った場合に由来が分かるようアプリ名を含める
        /// </summary>
        internal const string ProbeFileNamePrefix = "iccard_write_probe_";

        /// <summary>
        /// 指定フォルダへの書き込み可否を実測する
        /// </summary>
        /// <param name="folderPath">対象フォルダのパス（UNC 可）</param>
        /// <returns>実測結果。例外は投げず、失敗理由を列挙値で返す</returns>
        public static FolderWriteAccess Probe(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return FolderWriteAccess.PathNotSpecified;

            try
            {
                if (!Directory.Exists(folderPath))
                    return FolderWriteAccess.FolderNotFound;

                var probePath = Path.Combine(
                    folderPath,
                    ProbeFileNamePrefix + Guid.NewGuid().ToString("N") + ".tmp");

                using (var stream = new FileStream(
                    probePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.DeleteOnClose))
                {
                    // 0 バイトのファイル作成だけでは書き込み権限の確認として不十分な
                    // ファイルシステムがあるため、実際に 1 バイト書く。
                    stream.WriteByte(0);
                }

                return FolderWriteAccess.Writable;
            }
            catch (UnauthorizedAccessException)
            {
                return FolderWriteAccess.AccessDenied;
            }
            catch (DirectoryNotFoundException)
            {
                // Directory.Exists の直後にネットワークが切れた等の競合ケース
                return FolderWriteAccess.FolderNotFound;
            }
            catch (Exception)
            {
                // IOException（ディスク満杯・共有違反）、PathTooLongException、
                // NotSupportedException など。診断は失敗理由の粒度より
                // 「書けなかった」事実の確定を優先し、例外を外へ漏らさない。
                return FolderWriteAccess.Failed;
            }
        }
    }
}
