using ICCardManager.Common.Exceptions;

namespace ICCardManager.Infrastructure.CardReader
{
    /// <summary>
    /// Issue #1169: カード読み取り操作の結果を表すResult型。
    /// 成功時は値を、失敗時はエラー情報を保持し、リーダーエラーと
    /// 「正常だがデータゼロ件」を呼び出し元で区別できるようにする。
    /// </summary>
    /// <typeparam name="T">読み取り結果の型</typeparam>
    public sealed class CardReadResult<T>
    {
        /// <summary>
        /// 操作が成功したかどうか
        /// </summary>
        public bool Success { get; }

        /// <summary>
        /// 成功時の値（失敗時はdefault）
        /// </summary>
        public T Value { get; }

        /// <summary>
        /// 失敗時のエラー情報（成功時はnull）
        /// </summary>
        public CardReaderException Error { get; }

        private CardReadResult(bool success, T value, CardReaderException error)
        {
            Success = success;
            Value = value;
            Error = error;
        }

        /// <summary>
        /// 成功結果を生成する
        /// </summary>
        public static CardReadResult<T> Ok(T value) => new CardReadResult<T>(true, value, null);

        /// <summary>
        /// 失敗結果を生成する
        /// </summary>
        public static CardReadResult<T> Fail(CardReaderException error) => new CardReadResult<T>(false, default, error);
    }
}
