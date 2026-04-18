using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ICCardManager.Infrastructure.Security
{
    /// <summary>
    /// ネイティブDLL等のファイル整合性を SHA-256 ハッシュで検証するサービス (Issue #1266)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// DLL Hijacking 攻撃（アプリディレクトリに偽造 DLL を配置）への防御として、
    /// 既知の正規 DLL の SHA-256 ハッシュを期待値として渡し、実ファイルと比較する。
    /// </para>
    /// <para>
    /// 本クラスは純粋なハッシュ計算・比較を行い、期待値の保管場所（コード定数・
    /// レジストリ・設定ファイル等）には関与しない。期待値は呼び出し側で決定する。
    /// </para>
    /// </remarks>
    public sealed class DllIntegrityVerifier
    {
        /// <summary>
        /// 指定ファイルの SHA-256 ハッシュを計算し、期待値と比較する。
        /// </summary>
        /// <param name="filePath">検証対象のファイルパス</param>
        /// <param name="expectedSha256">期待される SHA-256 ハッシュ（16進小文字、大文字どちらも許容）</param>
        /// <returns>検証結果レポート</returns>
        /// <exception cref="ArgumentException"><paramref name="filePath"/> または <paramref name="expectedSha256"/> が null/空の場合</exception>
        public VerificationReport Verify(string filePath, string expectedSha256)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("ファイルパスが指定されていません。", nameof(filePath));
            if (string.IsNullOrWhiteSpace(expectedSha256))
                throw new ArgumentException("期待するハッシュ値が指定されていません。", nameof(expectedSha256));

            var normalizedExpected = NormalizeHash(expectedSha256);

            if (!File.Exists(filePath))
            {
                return new VerificationReport(
                    VerificationResult.FileNotFound,
                    expectedSha256: normalizedExpected,
                    actualSha256: null,
                    filePath: filePath,
                    errorMessage: $"対象ファイルが見つかりません: {filePath}");
            }

            try
            {
                var actual = ComputeSha256(filePath);
                var normalizedActual = NormalizeHash(actual);

                var isMatch = string.Equals(
                    normalizedActual, normalizedExpected, StringComparison.OrdinalIgnoreCase);

                return new VerificationReport(
                    isMatch ? VerificationResult.Verified : VerificationResult.HashMismatch,
                    expectedSha256: normalizedExpected,
                    actualSha256: normalizedActual,
                    filePath: filePath,
                    errorMessage: isMatch ? null : "SHA-256 ハッシュが期待値と一致しません。");
            }
            catch (IOException ex)
            {
                return new VerificationReport(
                    VerificationResult.Error,
                    expectedSha256: normalizedExpected,
                    actualSha256: null,
                    filePath: filePath,
                    errorMessage: $"ファイル読み取り中に I/O エラーが発生しました: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                return new VerificationReport(
                    VerificationResult.Error,
                    expectedSha256: normalizedExpected,
                    actualSha256: null,
                    filePath: filePath,
                    errorMessage: $"ファイル読み取り権限がありません: {ex.Message}");
            }
        }

        /// <summary>
        /// 指定ファイルの SHA-256 ハッシュを 16進小文字文字列で返す。
        /// </summary>
        /// <param name="filePath">対象ファイルのパス</param>
        /// <returns>SHA-256 ハッシュ（64文字の16進小文字）</returns>
        /// <remarks>純粋関数でテスト用にも直接利用できる。</remarks>
        public static string ComputeSha256(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return BytesToHex(hash);
        }

        /// <summary>
        /// バイト配列を 16進小文字文字列に変換する。
        /// </summary>
        internal static string BytesToHex(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// ハッシュ文字列を正規化する（前後空白除去・小文字化・区切り文字除去）。
        /// </summary>
        internal static string NormalizeHash(string hash)
        {
            if (hash == null) return string.Empty;
            return hash.Trim().Replace("-", "").Replace(":", "").Replace(" ", "").ToLowerInvariant();
        }
    }

    /// <summary>
    /// 整合性検証結果の種別。
    /// </summary>
    public enum VerificationResult
    {
        /// <summary>ハッシュが期待値と一致し、ファイルは改変されていないと判定。</summary>
        Verified,
        /// <summary>ファイルは存在するがハッシュが期待値と不一致（改変または偽造の可能性）。</summary>
        HashMismatch,
        /// <summary>対象ファイルが存在しない。</summary>
        FileNotFound,
        /// <summary>読み取り権限不足・I/O エラー等、検証自体が実行できなかった。</summary>
        Error,
    }

    /// <summary>
    /// 整合性検証結果の詳細レポート。
    /// </summary>
    public sealed class VerificationReport
    {
        public VerificationReport(
            VerificationResult result,
            string expectedSha256,
            string actualSha256,
            string filePath,
            string errorMessage)
        {
            Result = result;
            ExpectedSha256 = expectedSha256;
            ActualSha256 = actualSha256;
            FilePath = filePath;
            ErrorMessage = errorMessage;
        }

        /// <summary>検証結果種別。</summary>
        public VerificationResult Result { get; }

        /// <summary>期待していた SHA-256 ハッシュ（正規化済み16進小文字）。</summary>
        public string ExpectedSha256 { get; }

        /// <summary>実ファイルから計算した SHA-256 ハッシュ。検証失敗時は null の場合あり。</summary>
        public string ActualSha256 { get; }

        /// <summary>検証対象ファイルパス。</summary>
        public string FilePath { get; }

        /// <summary>失敗時のエラーメッセージ。成功時は null。</summary>
        public string ErrorMessage { get; }

        /// <summary>検証に成功した場合 true。</summary>
        public bool IsVerified => Result == VerificationResult.Verified;
    }
}
