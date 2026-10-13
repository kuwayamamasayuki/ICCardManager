#nullable enable
namespace ICCardManager.Services
{
    /// <summary>
    /// この PC の帳票出力先フォルダー（<c>report_output_config.txt</c>）を読み書きする（Issue #2266）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 帳票の出力先は <b>PC ごとの設定が正</b>。共有 DB の <c>settings.report_output_folder</c> は
    /// 出力先として読まない。共有 DB は共有フォルダーモードでは全 PC が書けるため、その値を帳票作成画面の
    /// 既定値・出力状況の自動走査に採用すると、DB を書き換えた者が指定したフォルダー（UNC なら別ホスト）へ、
    /// 画面を開いただけでこの PC が SMB 接続する（Windows のサインイン情報による認証が走る）。
    /// また各 PC の設定が DB を後勝ちで上書きし合い、ほかの PC で選んだフォルダーが既定値として出ていた。
    /// </para>
    /// <para>
    /// 値を書くのはインストーラー（帳票出力先フォルダのページ）と、この PC の帳票作成画面（F1）だけ。
    /// </para>
    /// </remarks>
    public interface IReportOutputFolderStore
    {
        /// <summary>
        /// この PC の帳票出力先フォルダーを返す。未設定・読めない場合は空文字。
        /// </summary>
        string Load();

        /// <summary>
        /// この PC の帳票出力先フォルダーを保存する。
        /// </summary>
        /// <exception cref="System.Exception">書き込みに失敗した場合（呼び出し側で受けること）</exception>
        void Save(string folder);
    }
}
