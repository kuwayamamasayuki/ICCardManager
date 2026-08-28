namespace ICCardManager.Dtos
{
    /// <summary>
    /// バックアップ保存先フォルダーの解決結果（Issue #1924）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 設定されたバックアップ先が検証に失敗すると、既定パス
    /// （<c>%ProgramData%\ICCardManager\backup</c>）へフォールバックしてバックアップ自体は成功する。
    /// この「成功しているが、設定した場所には書かれていない」状態は Warning ログにしか残らず、
    /// 共有フォルダーを指定した管理者からは「共有フォルダーにバックアップが作成されない」という
    /// 症状としてのみ観測できた（<c>.claude/rules/development-conventions.md</c> の
    /// 「『ログには出ている』は無言失敗の免罪符にならない」）。
    /// </para>
    /// <para>
    /// 解決の結果に「設定値」と「フォールバックした理由」を同梱し、
    /// システム管理画面（F6）の「バックアップ状況」が管理者へ提示できるようにする。
    /// </para>
    /// </remarks>
    public class BackupFolderResolution
    {
        /// <summary>
        /// 実際にバックアップが書き込まれるフォルダー（正規化済み）
        /// </summary>
        public string EffectiveFolderPath { get; set; }

        /// <summary>
        /// 設定されていたバックアップ先。未設定の場合は null または空文字
        /// </summary>
        public string ConfiguredFolderPath { get; set; }

        /// <summary>
        /// 既定パスへフォールバックした理由。フォールバックしていない場合は null
        /// </summary>
        /// <remarks>
        /// 「バックアップ先が未設定のため既定を使う」ことは異常ではないので理由を立てない。
        /// 理由が入るのは「設定されていたのに使えなかった」ときだけであり、
        /// <see cref="IsFallback"/> はその意味で真になる。
        /// </remarks>
        public string FallbackReason { get; set; }

        /// <summary>
        /// 設定されたフォルダーが使えず既定パスへ退避したか
        /// </summary>
        public bool IsFallback => !string.IsNullOrEmpty(FallbackReason);
    }
}
