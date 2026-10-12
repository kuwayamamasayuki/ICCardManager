#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using ICCardManager.Infrastructure.Timing;

namespace ICCardManager.Services
{
    /// <summary>
    /// 現在認証されている操作者のセッション情報を保持するコンテキスト。
    /// Issue #1265: 監査ログ (operation_log) の operator_idm / operator_name を
    /// 呼び出し元引数からではなく、このコンテキストから一元的に取得することで
    /// 内部者による監査ログなりすましを防止する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 想定ライフサイクル: Singleton。認証を要求した画面が認証結果（<see cref="StaffAuthResult"/>）の
    /// <see cref="StaffAuthResult.Enter"/> で <see cref="BeginSession"/> を呼び、
    /// OperationLogger が記録時に CurrentIdm / CurrentName を読む。
    /// </para>
    /// <para>
    /// Issue #2260: セッションは<b>認証した 1 操作に限る</b>。2 つの意味で限る。
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>時間</b>: <see cref="BeginSession"/> が返すスコープを操作の完了時（成功・失敗・キャンセル・例外のいずれでも）に
    /// 破棄するとセッションが終わる。加えて開始から絶対上限（<see cref="CurrentOperatorContext.MaxSessionDuration"/>、
    /// 30 分）を超えると失効する。上限は GC などに依存しない決定的な防御で、閉じ忘れに対する本当の防御は
    /// 静的検査（<c>StaffAuthScopeConventionTests</c>）が担う。
    /// </description></item>
    /// <item><description>
    /// <b>流れ</b>: セッションは <see cref="AsyncLocal{T}"/> に持ち、<see cref="BeginSession"/> を呼んだ非同期の流れ
    /// （ExecutionContext）と、そこから派生した流れ（<c>await</c> の続き・<c>Task.Run</c>・そこから投げた
    /// <c>Dispatcher.InvokeAsync</c> 等）にだけ見える。メイン画面で新しく始まった入力イベントは別の流れなので、
    /// 認証した操作の待ち時間中に別の職員が始めた操作は「GUI操作」、別の職員が重ねて認証した操作はその職員として
    /// 記録される（シングルトンのフィールドに持つと、操作の待ち時間中に始まった別の操作が認証を拾い、重ねた認証が
    /// 先の操作の残りの記録を後の職員の名前へ書き換えた）。
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>呼び出し方の制約</b>: <see cref="BeginSession"/> は、セッションを効かせたい流れの中で<b>同期的に</b>呼ぶこと。
    /// <c>async</c> メソッドの内側で設定した値は、そのメソッドから戻った呼び出し元には見えない
    /// （ExecutionContext は async メソッドの境界で復元される）。認証サービス（async）の内側で開始せず、
    /// 呼び出し元の画面が <see cref="StaffAuthResult.Enter"/> を呼ぶのはこのため。
    /// </para>
    /// <para>
    /// セッションを終える手段はスコープの破棄だけで、任意の時点でセッションを消す操作は持たない
    /// （終える責任を認証した操作の側に一本化するため。旧 <c>ClearSession</c> は本番の呼び出し元が無く削除した）。
    /// </para>
    /// </remarks>
    public interface ICurrentOperatorContext
    {
        /// <summary>現在の流れで有効なセッションの操作者 IDm。無効な場合は null。</summary>
        string? CurrentIdm { get; }

        /// <summary>現在の流れで有効なセッションの操作者氏名。無効な場合は null。</summary>
        string? CurrentName { get; }

        /// <summary>現在の流れに有効なセッションが存在するか（スコープが破棄されておらず、絶対上限も超えていない）。</summary>
        bool HasSession { get; }

        /// <summary>
        /// 現在の流れで有効なセッションの操作者を 1 回の判定で取り出す。
        /// </summary>
        /// <remarks>
        /// <see cref="HasSession"/> → <see cref="CurrentIdm"/> → <see cref="CurrentName"/> と別々に読むと、
        /// その間に上限を迎えた・スコープが破棄されたときに「セッションあり」なのに IDm・氏名が null になり得る。
        /// 操作者の記録にはこちらを使う。
        /// </remarks>
        /// <returns>有効なセッションがあれば true（<paramref name="idm"/> と <paramref name="name"/> は非 null）。</returns>
        bool TryGetOperator([NotNullWhen(true)] out string? idm, [NotNullWhen(true)] out string? name);

        /// <summary>
        /// 現在の非同期の流れで認証セッションを開始する。<see cref="StaffAuthResult.Enter"/> のみが呼び出すべき。
        /// </summary>
        /// <param name="idm">認証された操作者の IDm（16桁16進）</param>
        /// <param name="name">認証された操作者の氏名</param>
        /// <returns>
        /// このセッションのスコープ。破棄するとセッションを終了する（Issue #2260）。
        /// 同じ流れで後から開始したセッションがあれば、その破棄で元のセッションへ戻る。
        /// </returns>
        /// <remarks>
        /// セッションを効かせたい流れの中で同期的に呼ぶこと。<c>async</c> メソッドの内側で呼ぶと、
        /// そのメソッドから戻った呼び出し元には見えない。
        /// </remarks>
        IDisposable BeginSession(string idm, string name);
    }

    /// <summary>
    /// ICurrentOperatorContext の既定実装。シングルトンとして登録されることを前提とする。
    /// </summary>
    public class CurrentOperatorContext : ICurrentOperatorContext
    {
        private readonly ISystemClock _clock;
        private readonly TimeSpan _sessionDuration;

        /// <summary>
        /// 非同期の流れごとのセッション。インスタンスのフィールドなので、コンテキストごとに独立する。
        /// </summary>
        private readonly AsyncLocal<SessionState?> _current = new AsyncLocal<SessionState?>();

        /// <summary>
        /// セッションの絶対上限（開始から 30 分）。
        /// </summary>
        /// <remarks>
        /// Issue #2260: セッションは認証した操作の完了時にスコープの破棄で終わる。この上限は、
        /// スコープを閉じ忘れた経路で認証が残り続けないための決定的な（GC 等に依存しない）防御であり、
        /// 「この時間内なら認証が引き継がれる」という仕様ではない。旧実装の 5 分は、入力に時間のかかる
        /// 認証操作の途中で認証を切って後の記録を「GUI操作」にしていたため、操作が収まる長さへ広げた。
        /// </remarks>
        public static readonly TimeSpan MaxSessionDuration = TimeSpan.FromMinutes(30);

        public CurrentOperatorContext(ISystemClock clock)
            : this(clock, MaxSessionDuration)
        {
        }

        /// <summary>
        /// テスト目的でセッション長を差し替えるためのコンストラクタ。
        /// </summary>
        public CurrentOperatorContext(ISystemClock clock, TimeSpan sessionDuration)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (sessionDuration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(sessionDuration), "セッション有効期間は正の値である必要があります。");
            }
            _sessionDuration = sessionDuration;
        }

        public bool HasSession => ResolveActiveState() != null;

        public string? CurrentIdm => ResolveActiveState()?.Idm;

        public string? CurrentName => ResolveActiveState()?.Name;

        public bool TryGetOperator([NotNullWhen(true)] out string? idm, [NotNullWhen(true)] out string? name)
        {
            var state = ResolveActiveState();
            idm = state?.Idm;
            name = state?.Name;
            return state != null;
        }

        public IDisposable BeginSession(string idm, string name)
        {
            if (string.IsNullOrEmpty(idm))
            {
                throw new ArgumentException("IDm は必須です。", nameof(idm));
            }
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("氏名は必須です。", nameof(name));
            }

            var state = new SessionState(idm, name, _clock.Now + _sessionDuration, _current.Value);
            _current.Value = state;
            return new SessionScope(this, state);
        }

        /// <summary>
        /// 現在の流れで有効なセッションを返す。終了したセッションは読み飛ばし、同じ流れで先に開始した
        /// セッションへ戻る（後のスコープを別の流れで破棄した場合も、先の操作の帰属を失わないため）。
        /// </summary>
        private SessionState? ResolveActiveState()
        {
            var state = _current.Value;
            while (state != null && state.Ended)
            {
                state = state.Previous;
            }

            return state != null && _clock.Now < state.ExpiresAt ? state : null;
        }

        /// <summary>
        /// スコープの破棄。セッションを終了済みにし、この流れの現在値がそのセッションなら 1 つ前へ戻す。
        /// </summary>
        private void EndSession(SessionState state)
        {
            state.Ended = true;
            if (ReferenceEquals(_current.Value, state))
            {
                _current.Value = state.Previous;
            }
        }

        /// <summary>
        /// 1 つの認証セッション。終了の印は、同じセッションを共有する派生した流れ（<c>Task.Run</c> 等）にも効く。
        /// </summary>
        private sealed class SessionState
        {
            private volatile bool _ended;

            public SessionState(string idm, string name, DateTime expiresAt, SessionState? previous)
            {
                Idm = idm;
                Name = name;
                ExpiresAt = expiresAt;
                Previous = previous;
            }

            public string Idm { get; }

            public string Name { get; }

            public DateTime ExpiresAt { get; }

            public SessionState? Previous { get; }

            public bool Ended
            {
                get => _ended;
                set => _ended = value;
            }
        }

        /// <summary>
        /// <see cref="BeginSession"/> が返すスコープ。破棄で自分のセッションだけを終了する。
        /// </summary>
        private sealed class SessionScope : IDisposable
        {
            private CurrentOperatorContext? _owner;
            private readonly SessionState _state;

            public SessionScope(CurrentOperatorContext owner, SessionState state)
            {
                _owner = owner;
                _state = state;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.EndSession(_state);
            }
        }
    }
}
