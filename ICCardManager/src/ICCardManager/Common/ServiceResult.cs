namespace ICCardManager.Common
{
    /// <summary>
    /// サービス操作の結果を表す基底クラス。
    /// 新規のResult型を作成する際にはこのクラスを継承してください。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 既存のResult型（LendingResult, ReportGenerationResult等）は
    /// 後方互換性のため既存の定義を維持していますが、
    /// 新規に作成するResult型はこのクラスを継承することを推奨します。
    /// </para>
    /// <para>
    /// 使用例:
    /// <code>
    /// // 型パラメータなし（成功/失敗のみ）
    /// return ServiceResult.Ok();
    /// return ServiceResult.Fail("エラーメッセージ");
    ///
    /// // 型パラメータあり（データ付き）
    /// return ServiceResult&lt;int&gt;.Ok(42);
    /// return ServiceResult&lt;int&gt;.Fail("計算に失敗しました");
    /// </code>
    /// </para>
    /// </remarks>
    public class ServiceResult
    {
        /// <summary>
        /// 操作が成功したかどうか
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// エラーメッセージ（失敗時）
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 成功結果を作成
        /// </summary>
        public static ServiceResult Ok() => new() { Success = true };

        /// <summary>
        /// 失敗結果を作成
        /// </summary>
        public static ServiceResult Fail(string errorMessage) => new() { Success = false, ErrorMessage = errorMessage };

        /// <summary>
        /// bool演算子（if文で直接使用可能）
        /// </summary>
        public static implicit operator bool(ServiceResult result) => result.Success;
    }

    /// <summary>
    /// データ付きサービス操作の結果を表すジェネリッククラス。
    /// </summary>
    /// <typeparam name="T">結果データの型</typeparam>
    public class ServiceResult<T> : ServiceResult
    {
        /// <summary>
        /// 結果データ（成功時）
        /// </summary>
        public T Data { get; set; }

        /// <summary>
        /// データ付き成功結果を作成
        /// </summary>
        public static ServiceResult<T> Ok(T data) => new() { Success = true, Data = data };

        /// <summary>
        /// 失敗結果を作成
        /// </summary>
        public new static ServiceResult<T> Fail(string errorMessage) => new() { Success = false, ErrorMessage = errorMessage };
    }
}
