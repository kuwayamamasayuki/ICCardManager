using System;
using System.IO;

namespace ICCardManager.Infrastructure.Security
{
    /// <summary>
    /// Issue #1266: felicalib.dll (Sony PaSoRi + FeliCa ネイティブライブラリ) の
    /// 整合性を検証する専用ガードサービス。
    /// </summary>
    /// <remarks>
    /// <para>
    /// felicalib.dll は NuGet 管理外のネイティブ x86 DLL で、アプリディレクトリに
    /// コピーされる。内部者が偽造 DLL と差し替えることで IDm 盗聴・改ざんを
    /// 試みる攻撃への防御として、起動時に SHA-256 ハッシュを検証する。
    /// </para>
    /// <para>
    /// 期待ハッシュ値 <see cref="ExpectedSha256"/> はビルド時に埋め込まれ、
    /// 偽造 DLL 差し替えのみでは突破できない。ただし攻撃者が exe も差し替え
    /// 可能な場合はこの防御は無効化される点に注意（運用でホワイトリスト配布を併用）。
    /// </para>
    /// </remarks>
    public sealed class FelicalibIntegrityGuard
    {
        /// <summary>
        /// アプリディレクトリに配置する felicalib.dll の既知正規ハッシュ（SHA-256 16進小文字）。
        /// </summary>
        /// <remarks>
        /// tmurakam/felicalib (MIT License) 由来の x86 バイナリ。<c>ICCardManager.csproj</c>
        /// の None 宣言コメントと一致する。DLL を更新する場合はここも同期更新が必要。
        /// </remarks>
        public const string ExpectedSha256 =
            "f49c3af37dadf3d8a309492a2eb7fcded8c66dd3bb1bec855597bd65f9d9460d";

        /// <summary>felicalib.dll の既定ファイル名。</summary>
        public const string FelicalibDllName = "felicalib.dll";

        private readonly DllIntegrityVerifier _verifier;
        private readonly string _expectedSha256;

        /// <summary>
        /// 既定の期待ハッシュ（<see cref="ExpectedSha256"/>）で検証するインスタンスを生成する。
        /// </summary>
        public FelicalibIntegrityGuard()
            : this(new DllIntegrityVerifier(), ExpectedSha256)
        {
        }

        /// <summary>
        /// テスト用。任意の verifier と期待値で検証するインスタンスを生成する。
        /// </summary>
        public FelicalibIntegrityGuard(DllIntegrityVerifier verifier, string expectedSha256)
        {
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
            if (string.IsNullOrWhiteSpace(expectedSha256))
                throw new ArgumentException("期待するハッシュ値が指定されていません。", nameof(expectedSha256));
            _expectedSha256 = expectedSha256;
        }

        /// <summary>
        /// アプリケーションのベースディレクトリ配下の <c>felicalib.dll</c> を検証する。
        /// </summary>
        /// <returns>検証結果レポート</returns>
        public VerificationReport VerifyInBaseDirectory()
        {
            return VerifyAt(AppContext.BaseDirectory);
        }

        /// <summary>
        /// 指定ディレクトリ配下の <c>felicalib.dll</c> を検証する。
        /// </summary>
        /// <param name="directory">検索対象のディレクトリパス</param>
        public VerificationReport VerifyAt(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("ディレクトリパスが指定されていません。", nameof(directory));

            var dllPath = Path.Combine(directory, FelicalibDllName);
            return _verifier.Verify(dllPath, _expectedSha256);
        }
    }
}
