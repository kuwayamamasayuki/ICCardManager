#nullable enable
using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;

namespace ICCardManager.ViewModels
{
    /// <summary>
    /// 職員管理画面の職員証の認証の保持（Issue #2494）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 職員の登録・変更・復元は、この画面で最初に認証が要ったときだけ職員証の認証を求め、認証した職員を画面が閉じるまで
    /// 保持する。2 回目以降は認証ダイアログを出さず、保持している職員の操作として操作ログへ記録する。
    /// </para>
    /// <para>
    /// <b>保持するのは「誰か」だけで、操作者セッションではない</b>。セッションは操作ごとに
    /// <see cref="StaffAuthResult.ReissueForNextOperation"/> で作り直した結果をその場で <c>Enter()</c> して開始し、
    /// 操作が終わったら破棄する。操作と操作の間にセッションを残すと、この画面の外の認証の無い操作が拾う（Issue #2260）。
    /// </para>
    /// <para>
    /// 画面単位で保持すると、認証した職員が席を離れた後の別の職員の操作が、認証した職員の名前で記録され得る（#2260 と
    /// 同じ取り違えを、この画面を開いている間に限って許す）。そのため次の 4 つで保持を短くする。
    /// </para>
    /// <list type="number">
    /// <item><description>画面に「認証中: 氏名」を常に表示する（<see cref="HeldAuthenticationText"/>）</description></item>
    /// <item><description>「認証を解除」で破棄できる（<see cref="ReleaseHeldAuthenticationCommand"/>）</description></item>
    /// <item><description>
    /// 無操作が <see cref="HeldAuthenticationIdleTimeoutSeconds"/> 秒続いたら破棄する（最後の操作の時刻からの経過時間で判定）。無操作とは、この画面へのキー入力・
    /// マウスのボタン・ホイールの操作（View が <see cref="NotifyUserActivity"/> を呼ぶ）と、保持した認証を使った操作が
    /// 無いこと
    /// </description></item>
    /// <item><description>画面を閉じたら破棄する（<see cref="Cleanup"/>）</description></item>
    /// </list>
    /// <para>
    /// 保持を使う前に、保持している職員が削除されていない同じ IDm の職員として登録されているかを DB で確かめ、
    /// 登録されていなければ（他のパソコンでの削除・職員証の差し替えを含む）破棄して認証を求め直す。
    /// 削除と職員証の差し替えはこの保持を使わず、変えもしない（確認の後に毎回認証を求める）。
    /// </para>
    /// </remarks>
    public partial class StaffManageViewModel
    {
        /// <summary>
        /// 無操作のまま保持した認証を破棄するまでの秒数（Issue #2494。5 分。設定にはしない）
        /// </summary>
        internal const int HeldAuthenticationIdleTimeoutSeconds = 300;

        /// <summary>認証を保持していないときの表示（登録済みの職員がいるとき）</summary>
        /// <remarks>
        /// 認証を求める契機は「保存」だけではない（職員証のタッチで始まる復元もある）ので、契機を限定しない書き方にする
        /// （レビュー 2 回目で検出）。
        /// </remarks>
        internal const string NoHeldAuthenticationText =
            "職員証の認証: なし（職員の登録・変更・復元で認証を求められたら、登録済みの職員の職員証をタッチしてください）";

        /// <summary>
        /// 認証を保持していないときの表示（登録済みの職員がいないとき。登録・復元の認証は免除される）
        /// </summary>
        internal const string NoHeldAuthenticationNoStaffText =
            "職員証の認証: なし（登録済みの職員がいないため、職員の登録・復元では認証を求めません）";

        /// <summary>
        /// 認証を保持しているときの表示を組み立てる
        /// </summary>
        /// <param name="staffName">保持している職員の氏名</param>
        internal static string BuildHeldAuthenticationText(string staffName)
            => $"認証中: {staffName}（この画面での登録・変更・復元は、{staffName} の操作として記録します。" +
               $"操作が無いまま{HeldAuthenticationIdleTimeoutSeconds / 60}分たつか、画面を閉じると解除します）";

        /// <summary>
        /// 保持している認証（最初の操作で受け取った結果。<c>Enter()</c> はせず、次の操作の結果を作るためだけに使う）
        /// </summary>
        private StaffAuthResult? _heldAuthentication;

        /// <summary>
        /// 保持している職員の職員番号（保持したときに DB から読んだもの。空は空文字）
        /// </summary>
        /// <remarks>
        /// 使う前の確認は IDm で職員を引くので、職員証が他のパソコンで差し替えられ、古い職員証が別の職員として登録し直されると、
        /// 同じ IDm の別の職員を「保持している職員」として扱ってしまう（レビュー 4 回目で検出）。職員番号が食い違えば別の職員とみなす。
        /// 比べるのは前後の空白を除いた文字列で、<b>片方だけ空のときも食い違いとみなす</b>（安全側。番号を足した・消しただけでも
        /// 認証を求め直すが、手間が 1 回増えるだけで記録は誤らない）。両方とも空なら見分けられないので IDm だけで判断する。
        /// この画面で保持している職員自身の職員番号を変えた場合は、保存の成功時に覚え直す。
        /// </remarks>
        private string _heldStaffNumber = string.Empty;

        private static string NormalizeStaffNumber(string? number) => number?.Trim() ?? string.Empty;

        /// <summary>無操作の時間を確かめるタイマー（初めて保持するときに作る。1 秒ごと）</summary>
        private ITimer? _heldAuthenticationTimer;

        /// <summary>
        /// 最後に操作があった時刻（<see cref="_clock"/> の時刻）
        /// </summary>
        /// <remarks>
        /// 無操作の時間は Tick の回数ではなく、この時刻からの経過時間で判定する。PC がスリープから復帰しても
        /// <c>DispatcherTimer</c> は取りこぼした Tick を再生しないので、回数で数えると実時間で 5 分を超えても保持が残る
        /// （レビュー 1 回目で検出）。
        /// </remarks>
        private DateTime _heldAuthenticationLastActivityAt;

        /// <summary>
        /// 画面を閉じた（<see cref="Cleanup"/> を呼んだ）か。閉じた後は保持も認証の要求もしない
        /// </summary>
        /// <remarks>
        /// 保持している職員の確認（DB の読み取り）を待つ間に画面を閉じると、確認から戻った流れが認証ダイアログへ進み、
        /// 認証すると閉じた画面のタイマーを動かし直していた（レビュー 1 回目で検出）。
        /// </remarks>
        private bool _isHeldAuthenticationClosed;

        /// <summary>
        /// 保持している職員の氏名（保持していなければ null）
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsAuthenticationHeld))]
        [NotifyCanExecuteChangedFor(nameof(ReleaseHeldAuthenticationCommand))]
        private string? _heldOperatorName;

        /// <summary>
        /// 最後に変更を通知した <see cref="HeldAuthenticationText"/>（文言が変わったときだけ通知するため）
        /// </summary>
        /// <remarks>
        /// View はこの通知で読み上げを発火する。一覧の再読込（Clear と 1 件ずつの Add）のたびに通知すると、
        /// 文言が変わらなくても「1 ＋ 職員数」回読み上げ、「登録しました」の通知も埋もれる（レビュー 3 回目で検出）。
        /// </remarks>
        private string? _notifiedHeldAuthenticationText = NoHeldAuthenticationText;

        /// <summary>一覧を読み込んでいる最中か（Clear と Add の途中の人数で表示を切り替えない）</summary>
        private bool _isLoadingStaffListForHeldText;

        /// <summary>一覧を一度でも読み込んだか（読み込む前は「職員がいない」と表示しない）</summary>
        private bool _hasLoadedStaffListForHeldText;

        /// <summary>職員証の認証を保持しているか</summary>
        public bool IsAuthenticationHeld => HeldOperatorName != null;

        /// <summary>画面に常に表示する認証の状態</summary>
        /// <remarks>
        /// 保持していない間は、一覧（登録済みの職員）が空かどうかで文言を分ける。一覧の変化（<c>OnStaffListCollectionChanged</c>）でも
        /// 変更を通知する。免除の判定そのものは保存の時点の DB（<c>RequiresOperatorAuthenticationAsync</c>）で行うので、これは案内の表示に限る。
        /// </remarks>
        public string HeldAuthenticationText =>
            HeldOperatorName is { } name
                ? BuildHeldAuthenticationText(name)
                : _hasLoadedStaffListForHeldText && StaffList.Count == 0 ? NoHeldAuthenticationNoStaffText : NoHeldAuthenticationText;

        partial void OnHeldOperatorNameChanged(string? value)
        {
            RefreshHeldAuthenticationText();
        }

        /// <summary>
        /// <see cref="HeldAuthenticationText"/> が前に通知した文言から変わっていれば、変更を通知する
        /// </summary>
        private void RefreshHeldAuthenticationText()
        {
            if (_isLoadingStaffListForHeldText)
            {
                return;
            }

            var text = HeldAuthenticationText;
            if (string.Equals(text, _notifiedHeldAuthenticationText, StringComparison.Ordinal))
            {
                return;
            }

            _notifiedHeldAuthenticationText = text;
            OnPropertyChanged(nameof(HeldAuthenticationText));
        }

        /// <summary>一覧の読み込みを始める（終わるまで表示を切り替えない）</summary>
        private void BeginStaffListLoadForHeldText()
        {
            _isLoadingStaffListForHeldText = true;
        }

        /// <summary>一覧の読み込みを終え、人数に応じて表示を（変わったときだけ）切り替える</summary>
        private void EndStaffListLoadForHeldText()
        {
            _isLoadingStaffListForHeldText = false;
            _hasLoadedStaffListForHeldText = true;
            RefreshHeldAuthenticationText();
        }

        /// <summary>
        /// この画面で職員情報を変更した職員が保持している職員なら、保持している氏名を変更後の氏名にする
        /// （次の操作まで「認証中: 旧氏名」を残さない。レビュー 3 回目で検出）
        /// </summary>
        private void UpdateHeldOperatorNameIfHeldStaff(string staffIdm, string newName, string? newNumber)
        {
            if (_heldAuthentication is { } held
                && string.Equals(held.Idm, staffIdm, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(newName))
            {
                // この画面で本人が職員番号を変えたなら覚え直す（次の操作で「別の職員」と取り違えない）
                _heldStaffNumber = NormalizeStaffNumber(newNumber);
                if (!string.Equals(newName, HeldOperatorName, StringComparison.Ordinal))
                {
                    // DB の確認で追随するときと同じく、後から見分けられるよう記録する
                    _logger.LogInformation(
                        "職員管理画面で保持している職員の氏名が変わっていたため、変更後の氏名で記録します（職員 IDm: {StaffIdm}）",
                        IdmMasker.Mask(held.Idm));
                }

                HeldOperatorName = newName;
            }
        }

        /// <summary>保持を解除した理由（アプリのログに残す）</summary>
        internal enum HeldAuthenticationReleaseReason
        {
            /// <summary>「認証を解除」ボタン</summary>
            ReleaseButton,

            /// <summary>無操作の期限</summary>
            IdleTimeout,

            /// <summary>保持している職員が登録されていない（他のパソコンでの削除・差し替えを含む）</summary>
            StaffNoLongerRegistered,

            /// <summary>同じ IDm の職員の職員番号が保持したときと違う（別の職員として登録し直された等）</summary>
            StaffNumberChanged,

            /// <summary>この画面で保持している職員を削除した</summary>
            StaffDeleted,

            /// <summary>この画面で保持している職員の職員証を差し替えた</summary>
            StaffCardReplaced,

            /// <summary>画面を閉じた</summary>
            ScreenClosed,
        }

        /// <summary>解除の理由をログへ書く表記</summary>
        internal static string DescribeReleaseReason(HeldAuthenticationReleaseReason reason) => reason switch
        {
            HeldAuthenticationReleaseReason.ReleaseButton => "「認証を解除」ボタン",
            HeldAuthenticationReleaseReason.IdleTimeout => "無操作の期限",
            HeldAuthenticationReleaseReason.StaffNoLongerRegistered => "保持している職員が登録されていない",
            HeldAuthenticationReleaseReason.StaffNumberChanged => "同じ職員証の職員の職員番号が保持したときと違う",
            HeldAuthenticationReleaseReason.StaffDeleted => "保持している職員を削除した",
            HeldAuthenticationReleaseReason.StaffCardReplaced => "保持している職員の職員証を差し替えた",
            HeldAuthenticationReleaseReason.ScreenClosed => "画面を閉じた",
            _ => reason.ToString(),
        };

        /// <summary>
        /// 保持している認証を、次の 1 操作のための認証結果として取り出す（保持していない・使えないなら null）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 返した結果は呼び出し側がその場で <c>Enter()</c> して <c>using</c> で受ける（静的検査）。
        /// </para>
        /// <para>
        /// 保持している職員が削除されていない同じ IDm の職員として登録されているかを確かめ（キャッシュを通さない読み取り）、
        /// 登録されていなければ保持を破棄して null を返す（呼び出し側は認証を求め直す）。確認の間は処理中にする
        /// （処理中の外で DB を待つと、その間に取り消したフォームの内容が書き込まれ得る。Issue #2263）。
        /// </para>
        /// </remarks>
        private async Task<StaffAuthResult?> TakeHeldAuthenticationAsync()
        {
            var held = _heldAuthentication;
            if (held == null || _isHeldAuthenticationClosed)
            {
                return null;
            }

            // 使う前に期限を確かめる。スリープ復帰直後は Tick がまだ来ておらず、期限の切れた保持が残っている
            if (ReleaseHeldAuthenticationIfIdleExpired())
            {
                return null;
            }

            Staff? current;
            using (BeginBusy("確認中..."))
            {
                current = await _staffRepository.GetByIdmAsync(held.Idm);
            }

            // 確かめている間に「認証を解除」・無操作・画面を閉じたことで破棄された、または別の認証に置き換わったなら使わない
            if (_isHeldAuthenticationClosed || !ReferenceEquals(_heldAuthentication, held))
            {
                return null;
            }

            if (current == null)
            {
                // 削除された・職員証が差し替えられた（他のパソコンの操作を含む）。認証を求め直す
                ReleaseHeldAuthenticationCore(HeldAuthenticationReleaseReason.StaffNoLongerRegistered);
                return null;
            }

            if (!string.Equals(NormalizeStaffNumber(current.Number), _heldStaffNumber, StringComparison.Ordinal))
            {
                // 同じ IDm でも職員番号が違えば別の職員（古い職員証が別の職員として登録し直された等）。認証を求め直す
                ReleaseHeldAuthenticationCore(HeldAuthenticationReleaseReason.StaffNumberChanged);
                return null;
            }

            // 確かめている間に期限が来ていたら使わない（延長しない）
            if (ReleaseHeldAuthenticationIfIdleExpired())
            {
                return null;
            }

            var name = !string.IsNullOrWhiteSpace(current.Name) ? current.Name : held.StaffName;
            if (!string.Equals(name, HeldOperatorName, StringComparison.Ordinal))
            {
                // 同じ職員（IDm と職員番号が同じ）の氏名の変更に追随する。後から見分けられるよう記録する
                _logger.LogInformation(
                    "職員管理画面で保持している職員の氏名が変わっていたため、変更後の氏名で記録します（職員 IDm: {StaffIdm}）",
                    IdmMasker.Mask(held.Idm));
            }

            HeldOperatorName = name;
            ResetHeldAuthenticationIdleCountdown();
            return held.ReissueForNextOperation(name);
        }

        /// <summary>画面を閉じた後か（閉じた後は認証ダイアログへ進まない）</summary>
        private bool IsHeldAuthenticationClosed => _isHeldAuthenticationClosed;

        /// <summary>
        /// 保持している認証があれば、認証ダイアログを出さずにその職員の操作として <paramref name="write"/> を行う
        /// </summary>
        /// <returns>保持している認証で行ったら true。保持していない・使えないなら false（何もしていない）</returns>
        /// <remarks>
        /// セッションはこのメソッドの流れで開始し、<paramref name="write"/> はその内側で実行するため、書き込みの操作ログには
        /// 保持している職員が記録される。メソッドを抜けるとセッションは終わる。
        /// </remarks>
        private async Task<bool> TryRunWithHeldAuthenticationAsync(string operation, Func<Task> write)
        {
            using var heldAuthResult = (await TakeHeldAuthenticationAsync())?.Enter();
            if (heldAuthResult == null)
            {
                return false;
            }

            // 保持した認証で記録した操作を、後から操作ログと突き合わせて見分けられるようにする（IT担当者ガイド §8.1）
            _logger.LogInformation(
                "職員管理画面で保持している職員証の認証で操作を行います（操作: {Operation}、職員 IDm: {StaffIdm}）",
                operation, IdmMasker.Mask(heldAuthResult.Idm));

            try
            {
                await write();
            }
            finally
            {
                // 操作の終わりも「操作があった」とみなす（書き込みに時間がかかっても、その直後に破棄しない）
                ResetHeldAuthenticationIdleCountdown();
            }

            return true;
        }

        /// <summary>
        /// 認証した職員を、画面が閉じるまで（無操作・「認証を解除」で破棄されるまで）保持する
        /// </summary>
        /// <param name="authResult">認証した職員</param>
        /// <param name="operation">認証した操作の名前（ログに残す）</param>
        /// <remarks>
        /// 保持する職員の職員番号を DB から読んで覚える（使う前の確認で、同じ IDm の別の職員を見分けるため）。
        /// 読めなかった・職員として引けなかったときは保持しない（この操作は認証済みなので行う）。
        /// </remarks>
        private async Task HoldAuthenticationAsync(StaffAuthResult authResult, string operation)
        {
            if (_isHeldAuthenticationClosed)
            {
                // 閉じた画面では保持しない（タイマーを動かし直すと、外したハンドラーのまま画面ごと残る）
                return;
            }

            Staff? staff;
            try
            {
                using (BeginBusy("確認中..."))
                {
                    staff = await _staffRepository.GetByIdmAsync(authResult.Idm);
                }
            }
            catch (Exception ex)
            {
                // 保持は手間を減らすための付帯機能。読めなければ保持せず、認証した操作そのものは続ける
                _logger.LogWarning(ex,
                    "職員管理画面で職員証の認証を保持できませんでした（職員を読み取れませんでした。職員 IDm: {StaffIdm}）",
                    IdmMasker.Mask(authResult.Idm));
                return;
            }

            if (_isHeldAuthenticationClosed || staff == null)
            {
                return;
            }

            _heldStaffNumber = NormalizeStaffNumber(staff.Number);
            _heldAuthentication = authResult;
            HeldOperatorName = authResult.StaffName;
            // 新しく保持するので、期限の判定を経ずに今から数える（前の保持の期限は関係ない）
            _heldAuthenticationLastActivityAt = _clock.Now;
            _logger.LogInformation(
                "職員管理画面で職員証の認証を保持しました（認証した操作: {Operation}、職員 IDm: {StaffIdm}）",
                operation, IdmMasker.Mask(authResult.Idm));

            var timer = _heldAuthenticationTimer ??= CreateHeldAuthenticationTimer();
            timer.Stop();
            timer.Start();
        }

        /// <summary>
        /// 保持している職員証の認証を破棄する（「認証を解除」。次の登録・変更・復元では認証を求める）
        /// </summary>
        [RelayCommand(CanExecute = nameof(IsAuthenticationHeld))]
        public void ReleaseHeldAuthentication()
        {
            ReleaseHeldAuthenticationCore(HeldAuthenticationReleaseReason.ReleaseButton);
        }

        /// <summary>
        /// この画面で削除・職員証の差し替えをした職員が保持している職員なら、保持を破棄する（使えなくなった保持は破棄する）
        /// </summary>
        /// <param name="staffIdm">削除した職員・差し替える前の職員証の IDm</param>
        /// <param name="reason">削除か差し替えか</param>
        /// <remarks>
        /// 削除・差し替えの認証は保持を置き換えないが、対象が保持している職員なら、成功した時点で保持は使えない（次の操作の
        /// DB の確認で破棄されるが、それまで「認証中: 氏名」が残って画面と実際の動きが食い違う。レビュー 2 回目で検出）。
        /// 対象が別の職員なら保持は変えない。
        /// </remarks>
        private void ReleaseHeldAuthenticationIfHeldStaff(string staffIdm, HeldAuthenticationReleaseReason reason)
        {
            if (_heldAuthentication is { } held && string.Equals(held.Idm, staffIdm, StringComparison.OrdinalIgnoreCase))
            {
                ReleaseHeldAuthenticationCore(reason);
            }
        }

        /// <summary>
        /// 保持を破棄する（保持していなければ何もしない）。解除の理由をアプリのログに残す
        /// </summary>
        private void ReleaseHeldAuthenticationCore(HeldAuthenticationReleaseReason reason)
        {
            var held = _heldAuthentication;
            if (held == null)
            {
                return;
            }

            _logger.LogInformation(
                "職員管理画面で保持していた職員証の認証を解除しました（理由: {Reason}、職員 IDm: {StaffIdm}）",
                DescribeReleaseReason(reason), IdmMasker.Mask(held.Idm));
            _heldAuthentication = null;
            _heldAuthenticationTimer?.Stop();
            HeldOperatorName = null;
        }

        /// <summary>
        /// この画面への操作（キー入力・マウスのボタン・ホイール）があったことを知らせる。無操作の時間を数え直す
        /// </summary>
        public void NotifyUserActivity()
        {
            ResetHeldAuthenticationIdleCountdown();
        }

        /// <summary>
        /// 操作があった時刻を記録して、無操作の時間を数え直す（保持していなければ何もしない）
        /// </summary>
        /// <remarks>
        /// 数え直す前に期限を確かめ、切れていれば延長せずに破棄する。スリープ復帰の直後（Tick が来る前）に別の職員が
        /// キーを押すと、切れているはずの保持が延びて、その職員の操作が認証した職員の名前で記録されていた
        /// （fail-open。自動のセキュリティレビューで検出）。
        /// </remarks>
        private void ResetHeldAuthenticationIdleCountdown()
        {
            if (_heldAuthentication == null || ReleaseHeldAuthenticationIfIdleExpired())
            {
                return;
            }

            _heldAuthenticationLastActivityAt = _clock.Now;
        }

        /// <summary>
        /// 無操作の期限が切れていれば保持を破棄する（期限の判定はここ 1 か所）
        /// </summary>
        /// <returns>破棄した（期限が切れていた）なら true。保持していない・期限内なら false</returns>
        /// <remarks>
        /// 最後の操作の時刻からの経過時間が <see cref="HeldAuthenticationIdleTimeoutSeconds"/> 秒以上なら期限切れ。
        /// <b>経過時間が負（時計が戻った）ときも期限切れとして扱う</b> — 経過を確かめられないまま保持を延ばさない
        /// （手動の時刻合わせ・時刻同期で戻った場合は、認証し直せば済む安全側へ倒す）。
        /// 判定は Tick・操作の記録・保持の使用のすべてで、延長や使用の前に行う。
        /// </remarks>
        private bool ReleaseHeldAuthenticationIfIdleExpired()
        {
            if (_heldAuthentication == null)
            {
                return false;
            }

            var idle = _clock.Now - _heldAuthenticationLastActivityAt;
            if (idle >= TimeSpan.Zero && idle < TimeSpan.FromSeconds(HeldAuthenticationIdleTimeoutSeconds))
            {
                return false;
            }

            ReleaseHeldAuthenticationCore(HeldAuthenticationReleaseReason.IdleTimeout);
            return true;
        }

        private ITimer CreateHeldAuthenticationTimer()
        {
            var timer = _timerFactory.Create();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += OnHeldAuthenticationTimerTick;
            return timer;
        }

        private void OnHeldAuthenticationTimerTick(object? sender, EventArgs e)
        {
            if (_heldAuthentication == null)
            {
                _heldAuthenticationTimer?.Stop();
                return;
            }

            // 経過時間で判定する（スリープからの復帰で Tick が飛んでも、最初の Tick で破棄する）
            ReleaseHeldAuthenticationIfIdleExpired();
        }

        /// <summary>
        /// 画面を閉じるときに保持を破棄し、タイマーを止める（<see cref="Cleanup"/> から呼ぶ）
        /// </summary>
        private void CleanupHeldAuthentication()
        {
            _isHeldAuthenticationClosed = true;
            ReleaseHeldAuthenticationCore(HeldAuthenticationReleaseReason.ScreenClosed);
            if (_heldAuthenticationTimer != null)
            {
                _heldAuthenticationTimer.Stop();
                _heldAuthenticationTimer.Tick -= OnHeldAuthenticationTimerTick;
            }
        }
    }
}
