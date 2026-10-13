; ICCardManager インストーラースクリプト
; Inno Setup 6.x 用
; バージョンはコマンドラインから /DMyAppVersion=x.y.z で指定可能

#define MyAppName "交通系ICカード管理システム：ピッすい"
#define MyAppNameEn "ICCardManager"
; コマンドラインでバージョンが指定されていない場合のデフォルト値
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "Your Organization"
#define MyAppExeName "ICCardManager.exe"
#define MyAppDescription "交通系ICカードの貸出管理システム"

[Setup]
; 基本設定
AppId={{F6CAE9C5-02D4-474A-A5E0-CC41150FBFC7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/kuwayamamasayuki/ICCardManager
AppSupportURL=https://github.com/kuwayamamasayuki/ICCardManager/issues
AppUpdatesURL=https://github.com/kuwayamamasayuki/ICCardManager/releases
DefaultDirName={autopf}\{#MyAppNameEn}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=ICCardManager_Setup_{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; アイコン設定（インストーラーと「設定」→「アプリ」で表示されるアイコン）
SetupIconFile=app.ico

; 日本語設定
ShowLanguageDialog=auto

; 管理者権限
PrivilegesRequired=admin

; アンインストール設定（「設定」→「アプリ」で表示されるアイコン）
UninstallDisplayIcon={app}\app.ico
UninstallDisplayName={#MyAppName}

; Issue #506: アンインストーラーのファイル名をわかりやすく
; デフォルトの unins000.exe を Uninstall_ICCardManager.exe にリネーム
; 注: 実際のリネームは [Code] セクションの CurStepChanged で実行

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Auto-start at Windows login"; GroupDescription: "{cm:AdditionalIcons}"

[Dirs]
; データディレクトリ（C:\ProgramData\ICCardManager）とその backup・Logs（Issue #2261）
; アクセス権はここ（Permissions）では指定せず、PrepareToInstall（HardenDataDirectory）が icacls で設定する。
; - 複数の職員（Windowsユーザー）が同一PCで利用するため、配下には Users の「変更」を継承で与える。
; - 「フルコントロール」（users-full）にはしない。「変更」との差はアクセス許可の変更（WRITE_DAC）と所有権の取得で、
;   どちらもアプリの動作には要らない。付与すると一般ユーザーがフォルダーの ACL や所有者を握れる。
; - users-modify も使わない。フォルダー自体へ削除（DELETE）が付き、一般ユーザーがフォルダーを改名して同名の
;   ジャンクションを作り、昇格したインストーラー／アンインストーラーの書き込み・削除を任意の場所へ向け得る。
;   Inno は既存のフォルダーにも Permissions を足す（足すだけで、旧版が付けた Users:F も外さない）ため、
;   ここで指定すると HardenDataDirectory が外した DELETE を足し戻してしまう。
Name: "{commonappdata}\ICCardManager"
Name: "{commonappdata}\ICCardManager\backup"
Name: "{commonappdata}\ICCardManager\Logs"

[InstallDelete]
; 配布物から外した DLL を、上書きインストール時に既存のフォルダーから消す（[Files] のワイルドカードは追加・上書きしかしない）。
; Issue #2165 で未使用の Microsoft.Extensions.Hosting と、それだけが使っていた依存を外した。
; ここに載せるのは「今は配布されない」DLL だけ（InstallerObsoleteAssemblyConventionTests がロックファイルと照合する）。
Type: files; Name: "{app}\Microsoft.Extensions.Hosting.dll"
Type: files; Name: "{app}\Microsoft.Extensions.Hosting.Abstractions.dll"
Type: files; Name: "{app}\Microsoft.Extensions.Configuration.CommandLine.dll"
Type: files; Name: "{app}\Microsoft.Extensions.Configuration.EnvironmentVariables.dll"
Type: files; Name: "{app}\Microsoft.Extensions.Configuration.UserSecrets.dll"
Type: files; Name: "{app}\Microsoft.Extensions.Diagnostics.dll"
Type: files; Name: "{app}\Microsoft.Extensions.Diagnostics.Abstractions.dll"
Type: files; Name: "{app}\Microsoft.Extensions.Logging.Console.dll"
Type: files; Name: "{app}\Microsoft.Extensions.Logging.EventLog.dll"
Type: files; Name: "{app}\Microsoft.Extensions.Logging.EventSource.dll"
Type: files; Name: "{app}\Tools\Microsoft.Extensions.Hosting.dll"
Type: files; Name: "{app}\Tools\Microsoft.Extensions.Hosting.Abstractions.dll"
Type: files; Name: "{app}\Tools\Microsoft.Extensions.Configuration.CommandLine.dll"
Type: files; Name: "{app}\Tools\Microsoft.Extensions.Configuration.EnvironmentVariables.dll"
Type: files; Name: "{app}\Tools\Microsoft.Extensions.Configuration.UserSecrets.dll"
Type: files; Name: "{app}\Tools\Microsoft.Extensions.Diagnostics.dll"
Type: files; Name: "{app}\Tools\Microsoft.Extensions.Diagnostics.Abstractions.dll"
Type: files; Name: "{app}\Tools\Microsoft.Extensions.Logging.Console.dll"
Type: files; Name: "{app}\Tools\Microsoft.Extensions.Logging.EventLog.dll"
Type: files; Name: "{app}\Tools\Microsoft.Extensions.Logging.EventSource.dll"

[Files]
; メインアプリケーションと依存DLL（すべてのDLL/EXE/config/pdbを含める）
Source: "..\publish\*.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\*.config"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\publish\*.json"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; x86ネイティブDLL（SQLite Interop等）
Source: "..\publish\x86\*"; DestDir: "{app}\x86"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

; アイコンファイル（「設定」→「アプリ」で表示されるアイコン）
Source: "app.ico"; DestDir: "{app}"; Flags: ignoreversion

; サウンドファイル
Source: "..\publish\Resources\Sounds\*"; DestDir: "{app}\Resources\Sounds"; Flags: ignoreversion recursesubdirs createallsubdirs

; テンプレートファイル
Source: "..\publish\Resources\Templates\*"; DestDir: "{app}\Resources\Templates"; Flags: ignoreversion recursesubdirs createallsubdirs

; ドキュメント（ユーザー向け・管理者向け）
; markdown形式
Source: "..\docs\manual\はじめに.md"; DestDir: "{app}\Docs"; Flags: ignoreversion
Source: "..\docs\manual\ユーザーマニュアル.md"; DestDir: "{app}\Docs"; Flags: ignoreversion
Source: "..\docs\manual\ユーザーマニュアル概要版.md"; DestDir: "{app}\Docs"; Flags: ignoreversion
Source: "..\docs\manual\管理者マニュアル.md"; DestDir: "{app}\Docs"; Flags: ignoreversion
Source: "..\docs\manual\IT担当者ガイド.md"; DestDir: "{app}\Docs"; Flags: ignoreversion
; docx形式
Source: "..\docs\manual\はじめに.docx"; DestDir: "{app}\Docs"; Flags: ignoreversion
Source: "..\docs\manual\ユーザーマニュアル.docx"; DestDir: "{app}\Docs"; Flags: ignoreversion
Source: "..\docs\manual\ユーザーマニュアル概要版.docx"; DestDir: "{app}\Docs"; Flags: ignoreversion
Source: "..\docs\manual\管理者マニュアル.docx"; DestDir: "{app}\Docs"; Flags: ignoreversion
Source: "..\docs\manual\IT担当者ガイド.docx"; DestDir: "{app}\Docs"; Flags: ignoreversion
; PDF形式（Issue #642）
Source: "..\docs\manual\はじめに.pdf"; DestDir: "{app}\Docs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\docs\manual\ユーザーマニュアル.pdf"; DestDir: "{app}\Docs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\docs\manual\ユーザーマニュアル概要版.pdf"; DestDir: "{app}\Docs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\docs\manual\管理者マニュアル.pdf"; DestDir: "{app}\Docs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\docs\manual\IT担当者ガイド.pdf"; DestDir: "{app}\Docs"; Flags: ignoreversion skipifsourcedoesntexist

; デバッグツール（Issue #447対応）
; Toolsフォルダにすべてのファイルを配置
Source: "..\publish\Tools\*.exe"; DestDir: "{app}\Tools"; Flags: ignoreversion
Source: "..\publish\Tools\*.dll"; DestDir: "{app}\Tools"; Flags: ignoreversion
Source: "..\publish\Tools\*.config"; DestDir: "{app}\Tools"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\publish\Tools\*.json"; DestDir: "{app}\Tools"; Flags: ignoreversion skipifsourcedoesntexist
; x86/x64ネイティブDLL（SQLite Interop等）
Source: "..\publish\Tools\x86\*"; DestDir: "{app}\Tools\x86"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\publish\Tools\x64\*"; DestDir: "{app}\Tools\x64"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"
Name: "{group}\ドキュメント"; Filename: "{app}\Docs"
Name: "{group}\デバッグツール"; Filename: "{app}\Tools\DebugDataViewer.exe"
; Issue #506: リネーム後のアンインストーラーを参照
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{app}\Uninstall_ICCardManager.exe"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon

[Registry]
; 常駐オプション（Issue #452対応）: Windowsログイン時に自動起動
; HKLM（全ユーザー向け）にRunキーを登録
; uninsdeletevalue: アンインストール時に自動削除
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppNameEn}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Issue #742: 部署選択ページ
var
  DepartmentPage: TInputOptionWizardPage;

// DB保存先選択ページ
var
  DatabasePage: TWizardPage;
  DatabaseLocalRadio: TNewRadioButton;
  DatabaseSharedRadio: TNewRadioButton;
  DatabasePathEdit: TNewEdit;
  DatabasePathLabel: TNewStaticText;
  DatabaseNoteLabel: TNewStaticText;
  DatabaseBrowseButton: TNewButton;
  // ページ遷移時に値を保存（ssPostInstall時にコントロールが無効な場合の対策）
  DatabaseUseSharedFolder: Boolean;
  DatabaseSharedPath: string;

// 帳票出力先選択ページ
var
  ReportOutputPage: TWizardPage;
  ReportOutputPathEdit: TNewEdit;
  ReportOutputPathLabel: TNewStaticText;
  ReportOutputNoteLabel: TNewStaticText;
  ReportOutputBrowseButton: TNewButton;

// マップトドライブ検出・再マッピング（Issue #1584）
var
  MappedDriveLetters: TArrayOfString;
  MappedDriveRemotePaths: TArrayOfString;
  MappedDriveCount: Integer;

// =============================================
// Issue #2261: データフォルダー（{commonappdata}\ICCardManager）の保護
// =============================================
// 一般ユーザーはこのフォルダーへ書き込める（[Dirs] の users-modify）。中身を消してフォルダーを
// ジャンクション（＋オブジェクトマネージャーのシンボリックリンク）へ差し替えられると、
// 昇格したインストーラー／アンインストーラーの書き込み・削除（設定ファイル・DelTree）が任意の場所へ向く。
// 昇格中に書き込み・削除する前に、ProgramData から対象までの各段が再解析ポイントでないことを確かめる。
const
  IccmFileAttributeDirectory = $10;      // FILE_ATTRIBUTE_DIRECTORY
  IccmFileAttributeReparsePoint = $400;  // FILE_ATTRIBUTE_REPARSE_POINT

// 戻り値は INVALID_FILE_ATTRIBUTES（$FFFFFFFF）を -1 として比べられるよう Integer で受ける
function IccmGetFileAttributes(lpFileName: string): Integer;
  external 'GetFileAttributesW@kernel32.dll stdcall';

// パス自体（最後の要素）が再解析ポイントか。存在しないパスは False。
// FileExists / DirExists はリンク先を辿るため、リンク先の無いジャンクションを「存在しない」と見誤る。
// GetFileAttributes は最後の要素を辿らずにリンク自身の属性を返すので、存在確認を前置しない。
function IsReparsePoint(const Path: string): Boolean;
var
  Attr: Integer;
begin
  Attr := IccmGetFileAttributes(Path);
  Result := (Attr <> -1) and ((Attr and IccmFileAttributeReparsePoint) <> 0);
end;

// ProgramData から Path までの各段（ProgramData・データフォルダー・その下の各要素）のどれかが再解析ポイントか。
// Path が ProgramData の配下でなければ判定できないので True（危険）へ倒す。
function DataPathHasReparsePoint(const Path: string): Boolean;
var
  Base, Rest, Current: string;
  P: Integer;
begin
  Base := ExpandConstant('{commonappdata}');
  Result := IsReparsePoint(Base);
  if Result then
    Exit;

  if CompareText(Copy(Path, 1, Length(Base) + 1), AddBackslash(Base)) <> 0 then
  begin
    Result := True;
    Exit;
  end;

  Rest := Copy(Path, Length(Base) + 2, Length(Path));
  Current := Base;
  while Rest <> '' do
  begin
    P := Pos('\', Rest);
    if P = 0 then
    begin
      Current := Current + '\' + Rest;
      Rest := '';
    end
    else
    begin
      Current := Current + '\' + Copy(Rest, 1, P - 1);
      Rest := Copy(Rest, P + 1, Length(Rest));
    end;

    if IsReparsePoint(Current) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

// Dir 自身とその配下（ファイル・サブフォルダーを再帰的に）に再解析ポイントがあるか。
// 再解析ポイントの中へは入らない（見つけた時点で True）。
// 存在するフォルダーの中身を列挙できないとき（管理者の一覧取得を拒否する ACL 等）も True（危険）へ倒す。
// 列挙できない中にジャンクションが置かれていても「リンクなし」と見誤らないため（fail-closed）。
function TreeHasReparsePoint(const Dir: string): Boolean;
var
  FindRec: TFindRec;
  Found: Boolean;
begin
  Result := IsReparsePoint(Dir);
  if Result then
    Exit;

  // 存在しないパスは検査するものが無い
  if IccmGetFileAttributes(Dir) = -1 then
    Exit;

  // 空のフォルダーでも「.」と「..」が見つかるので、見つからないのは列挙できないとき
  Found := FindFirst(AddBackslash(Dir) + '*', FindRec);
  if not Found then
  begin
    Log('フォルダーの中身を確認できないため、ジャンクション等があるものとして扱います: ' + Dir);
    Result := True;
    Exit;
  end;
  try
    while Found and (not Result) do
    begin
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
      begin
        if (FindRec.Attributes and IccmFileAttributeReparsePoint) <> 0 then
          Result := True
        else if (FindRec.Attributes and IccmFileAttributeDirectory) <> 0 then
          Result := TreeHasReparsePoint(AddBackslash(Dir) + FindRec.Name);
      end;
      if not Result then
        Found := FindNext(FindRec);
    end;
  finally
    FindClose(FindRec);
  end;
end;

// 昇格中に Path（データフォルダー配下のファイル・フォルダー）へ書き込み・削除してよいか。
// 再解析ポイントが立っていれば書き込みを見送り、ログに残す（インストール自体は PrepareToInstall が先に止める。
// ここは検査から書き込みまでの間に差し替えられた場合の多重防御）。
function IsDataPathSafe(const Path: string): Boolean;
begin
  Result := not DataPathHasReparsePoint(Path);
  if not Result then
    Log('データフォルダーの経路にジャンクションまたはシンボリックリンクがあるため、書き込み・削除を見送りました: ' + Path);
end;

// データフォルダー配下のファイルを消す。経路に再解析ポイントがあれば消さない。
// 戻り値: 消した、または元から無ければ True。
function DeleteDataFile(const Path: string): Boolean;
begin
  Result := False;
  if not IsDataPathSafe(Path) then
    Exit;
  if FileExists(Path) then
    Result := DeleteFile(Path)
  else
    Result := True;
end;

// データフォルダー配下のファイルへ書き込む。
// 既存のファイルは消してから新しく作る。SaveStringToFile は既存のファイルを開いて書くため、
// 一般ユーザーが置いたハードリンクならリンク先のファイルを書き換えてしまう（DeleteFile はリンクだけを消す）。
function WriteDataFile(const Path, Value: string): Boolean;
begin
  Result := False;
  if not DeleteDataFile(Path) then
  begin
    Log('既存のファイルを消せないため、書き込みを見送りました: ' + Path);
    Exit;
  end;
  if not IsDataPathSafe(Path) then
    Exit;
  Result := SaveStringToFile(Path, Value, False);
end;

// icacls を実行し、終了コード 0 なら True
function RunIcacls(const Params: string): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\icacls.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Result then
  begin
    Log('icacls ' + Params + ' → exit code=' + IntToStr(ResultCode));
    Result := (ResultCode = 0);
  end
  else
    Log('icacls の起動に失敗しました: ' + Params);
end;

// データフォルダーの所有者を Administrators に固定し、ACL を作り直す（Issue #2261）。
// - 旧版の [Dirs] users-full と、旧版のアプリ（ErrorDialogHelper）が Logs へ付けた Users:F は、
//   [Dirs] の Permissions（追加しかしない）では外れない。/reset で明示的な ACE を消して継承だけに戻す。
// - ProgramData は一般ユーザーもフォルダーを作れるため、初回インストール前に作られたフォルダーは
//   所有者が作った本人のまま残る（所有者は ACL を戻されても書き換えられる）。/setowner で取り上げる。
// - フォルダー自体には Users の削除（DELETE）を与えない。与えると一般ユーザーがフォルダーを改名して
//   同名のジャンクションを作れ、安全性の確認から書き込み・削除までの間に差し替えられ得る。
//   フォルダー自体へは「読み取り・実行＋ファイルの作成＋フォルダーの作成」（このフォルダーのみ）、
//   配下へは「変更」を継承だけで与える（DB・ジャーナル・設定ファイル・backup・Logs は自身の DELETE で消せる）。
//   このため [Dirs] では Permissions を指定しない（Inno は既存のフォルダーにも Permissions を足し、
//   users-modify はフォルダー自体へ DELETE を足し戻す）。新規インストールでもここでフォルダーを作る。
// 戻り値: 空文字なら続行、空でなければインストールを中止してその文言を表示する。
function HardenDataDirectory(): String;
var
  Root: string;
begin
  Result := '';
  Root := ExpandConstant('{commonappdata}\ICCardManager');

  if not IsDataPathSafe(Root) then
  begin
    Result := 'データフォルダー（' + Root + '）がジャンクションまたはシンボリックリンクになっています。' + #13#10 +
              'このままでは管理者の権限で意図しない場所のファイルを書き換え・削除するおそれがあるため、インストールを中止しました。' + #13#10 +
              'エクスプローラーでこのリンクを削除してから（データを残す場合は通常のフォルダーへ移してから）、もう一度インストールしてください。';
    Exit;
  end;

  // 新規インストール: 昇格したインストーラー（所有者 Administrators）で作る
  if not DirExists(Root) then
    CreateDir(Root);
  if (not DirExists(Root)) or DataPathHasReparsePoint(Root) then
  begin
    Result := 'データフォルダー（' + Root + '）を作成できませんでした。' + #13#10 +
              '同じ名前のファイルやリンクがあるか、C:\ProgramData へ書き込めない可能性があります。' + #13#10 +
              'エクスプローラーで C:\ProgramData の中を確認し、同じ名前のファイルやリンクを削除してから、もう一度インストールしてください。';
    Exit;
  end;

  // 1) フォルダー自体: 所有者を固定 → 明示的な ACE を消して継承へ戻す
  //    → 配下へ「変更」を継承だけで付与 → フォルダー自体へ「読み取り・実行＋ファイル／フォルダーの作成」を付与
  //    （所有者を先に固定するのは、ACL で管理者が締め出されていても所有者として ACL を書き換えられるようにするため）
  if not (RunIcacls('"' + Root + '" /setowner *S-1-5-32-544 /L /Q') and
          RunIcacls('"' + Root + '" /reset /L /Q') and
          RunIcacls('"' + Root + '" /grant:r *S-1-5-32-545:(OI)(CI)(IO)M /L /Q') and
          RunIcacls('"' + Root + '" /grant *S-1-5-32-545:(RX,WD,AD) /L /Q')) then
  begin
    Result := 'データフォルダー（' + Root + '）のアクセス権を設定し直せませんでした。' + #13#10 +
              '一般ユーザーにフルコントロールが残るおそれがあるため、インストールを中止しました。' + #13#10 +
              '管理者としてインストーラーを実行しているか確認し、ピッすいを終了してから、もう一度インストールしてください。';
    Exit;
  end;

  // 2) 配下: フォルダー自体の所有者を固定した後で改めて検査する（固定前は列挙できない ACL にされ得る）
  if TreeHasReparsePoint(Root) then
  begin
    Result := 'データフォルダー（' + Root + '）の中に、ジャンクション・シンボリックリンク、または中身を確認できないフォルダーがあります。' + #13#10 +
              'このままでは管理者の権限で意図しない場所のファイルを書き換え・削除するおそれがあるため、インストールを中止しました。' + #13#10 +
              'エクスプローラーでフォルダーの中のリンクを削除し、開けないフォルダーは所有権を取得して中身を確認してから、もう一度インストールしてください。';
    Exit;
  end;

  // 3) 配下（backup・Logs・データベース・設定ファイル）: 所有者の固定と継承への戻し。
  //    使用中のファイル等で一部が失敗しても続行し（/C）、ログに残す（フォルダー自体の ACL は 1) で確定済み）
  if not RunIcacls('"' + Root + '\*" /setowner *S-1-5-32-544 /T /C /L /Q') then
    Log('データフォルダー配下の所有者の固定に一部失敗しました（続行）');
  if not RunIcacls('"' + Root + '\*" /reset /T /C /L /Q') then
    Log('データフォルダー配下のアクセス権の作り直しに一部失敗しました（続行）');
end;

// 既存の設定ファイルを読み込む（アップグレード時のデフォルト値として使用）
// LoadStringsFromFile（TArrayOfString版）は内部でTStringList.LoadFromFileを使い、
// UTF-8 BOM を自動検出してUnicodeにデコードする。BOMなしはANSI（Shift_JIS）として読む。
function ReadConfigFile(FileName: string): string;
var
  ConfigPath: string;
  Lines: TArrayOfString;
begin
  Result := '';
  ConfigPath := ExpandConstant('{commonappdata}\ICCardManager\') + FileName;
  if FileExists(ConfigPath) then
  begin
    if LoadStringsFromFile(ConfigPath, Lines) then
    begin
      if GetArrayLength(Lines) > 0 then
        Result := Trim(Lines[0]);
    end;
  end;
end;

// パス入力時に自動的に「共有フォルダ」ラジオボタンを選択
procedure DatabasePathEditChange(Sender: TObject);
begin
  if Trim(DatabasePathEdit.Text) <> '' then
  begin
    DatabaseSharedRadio.Checked := True;
    DatabaseLocalRadio.Checked := False;
  end;
end;

// Issue #1655: 「このPCのみで使用」を選択したら共有フォルダパス欄を空にする
// （前回設定で残った共有パスが見えたままだと誤解を招く。実際の database_config.txt 削除は
//   WriteDatabaseConfig が担うが、UI 上もパスを残さないことで挙動を一致させる）
procedure DatabaseLocalRadioClick(Sender: TObject);
begin
  if (DatabaseLocalRadio <> nil) and DatabaseLocalRadio.Checked
     and (DatabasePathEdit <> nil) then
    DatabasePathEdit.Text := '';
end;

// DB保存先の「参照」ボタンクリック
procedure DatabaseBrowseButtonClick(Sender: TObject);
var
  Dir: string;
begin
  Dir := DatabasePathEdit.Text;
  if BrowseForFolder('共有フォルダを選択してください。', Dir, False) then
  begin
    DatabasePathEdit.Text := Dir;
    // 選択したら自動的に「共有フォルダ」に切替
    DatabaseSharedRadio.Checked := True;
    DatabaseLocalRadio.Checked := False;
  end;
end;

// 帳票出力先の「参照」ボタンクリック
procedure ReportOutputBrowseButtonClick(Sender: TObject);
var
  Dir: string;
begin
  Dir := ReportOutputPathEdit.Text;
  if BrowseForFolder('帳票出力先フォルダを選択してください。', Dir, False) then
  begin
    ReportOutputPathEdit.Text := Dir;
  end;
end;

// マップトドライブをレジストリ（HKCU\Network）から検出（Issue #1584）
procedure DetectMappedDrives();
var
  SubKeys: TArrayOfString;
  I: Integer;
  DriveLetter, RemotePath: string;
begin
  MappedDriveCount := 0;
  if not RegGetSubkeyNames(HKCU, 'Network', SubKeys) then
    Exit;

  SetArrayLength(MappedDriveLetters, GetArrayLength(SubKeys));
  SetArrayLength(MappedDriveRemotePaths, GetArrayLength(SubKeys));

  for I := 0 to GetArrayLength(SubKeys) - 1 do
  begin
    DriveLetter := Uppercase(SubKeys[I]);
    if RegQueryStringValue(HKCU, 'Network\' + SubKeys[I], 'RemotePath', RemotePath) then
    begin
      MappedDriveLetters[MappedDriveCount] := DriveLetter + ':';
      MappedDriveRemotePaths[MappedDriveCount] := RemotePath;
      MappedDriveCount := MappedDriveCount + 1;
    end;
  end;

  SetArrayLength(MappedDriveLetters, MappedDriveCount);
  SetArrayLength(MappedDriveRemotePaths, MappedDriveCount);
  Log('マップトドライブ検出(Method 1: HKCU\Network): ' + IntToStr(MappedDriveCount) + ' 件');
end;

// HKCU\Network にエントリがない場合のフォールバック（Issue #1584）
// GPOやログインスクリプトで割り当てたドライブ、/persistent:no のドライブは
// レジストリに記録されないため、標準ユーザーセッションで WMI クエリを実行して検出する
// wmic.exe は Windows 11 22H2 以降で非推奨（オプション機能）のため PowerShell を使用
//
// 受け渡しファイルの置き場所には「元ユーザー（非昇格トークン）が書き込め、かつ
// 昇格中のインストーラーが読める」ディレクトリが必要。{tmp} は昇格実行時に
// 保護 ACL 付きで作成され（現在ユーザーには読み取り＋実行のみ許可）、
// ExecAsOriginalUser で起動した PowerShell からの書き込みが Access Denied になる
// （v2.9.3-rc1 で検出が常に失敗していた根本原因）。そのため
// C:\Users\Public（INTERACTIVE が書き込み可）→ C:\ProgramData（Users が
// ファイル作成可）の順に試行する。
procedure DetectMappedDrivesFromSession();
var
  ExchangeDirs: TArrayOfString;
  OutFile: string;
  ResultCode: Integer;
  ExecOk, Found: Boolean;
  Lines: TArrayOfString;
  I, D: Integer;
  Line: string;
  PendingDriveLetter: string;
  DeviceIdPrefix, ProviderPrefix: string;
begin
  MappedDriveCount := 0;
  DeviceIdPrefix := 'DeviceID=';
  ProviderPrefix := 'ProviderName=';

  SetArrayLength(ExchangeDirs, 2);
  ExchangeDirs[0] := GetEnv('PUBLIC');                  // 通常 C:\Users\Public
  ExchangeDirs[1] := ExpandConstant('{commonappdata}'); // 通常 C:\ProgramData

  Found := False;
  for D := 0 to GetArrayLength(ExchangeDirs) - 1 do
  begin
    if Trim(ExchangeDirs[D]) = '' then Continue;

    // 共用ディレクトリに置くため、固定名ファイルの先回り作成（squatting）への
    // 緩和策としてランダムなファイル名を使い、実行前に同名ファイルを削除する
    OutFile := AddBackslash(ExchangeDirs[D]) +
      'iccm_mapped_drives_' + IntToStr(Random(2147483647)) + '.txt';
    DeleteFile(OutFile);

    ExecOk := ExecAsOriginalUser(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      // -Encoding UTF8: PowerShell 5.1 は BOM 付き UTF-8 で書き出す。Inno Setup の
      // LoadStringsFromFile は BOM を自動検出して Unicode にデコードするため、
      // 日本語を含む共有名（例: \\server\道路_建設推進課）も正しく扱える。
      // ASCII を指定すると non-ASCII 文字が ? に lossy 置換され、後段の net use が
      // 不正なパスで失敗する（Issue #1584）。
      '-NoProfile -ExecutionPolicy Bypass -Command "Get-CimInstance Win32_MappedLogicalDisk | ForEach-Object { ''DeviceID='' + $_.DeviceID; ''ProviderName='' + $_.ProviderName; '''' } | Out-File -FilePath ''' + OutFile + ''' -Encoding UTF8"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    if not ExecOk then
      Log('マップトドライブ検出(Method 2): PowerShell の起動に失敗 (dir=' + ExchangeDirs[D] + ')')
    else
      Log('マップトドライブ検出(Method 2): PowerShell exit code=' + IntToStr(ResultCode) +
          ' (dir=' + ExchangeDirs[D] + ')');

    if FileExists(OutFile) then
    begin
      Found := True;
      Break;
    end;
  end;

  if not Found then
  begin
    Log('マップトドライブ検出(Method 2): 受け渡しファイルが作成されず検出をスキップしました');
    Exit;
  end;

  if not LoadStringsFromFile(OutFile, Lines) then
  begin
    Log('マップトドライブ検出(Method 2): 受け渡しファイルの読み込みに失敗しました');
    DeleteFile(OutFile);
    Exit;
  end;

  SetArrayLength(MappedDriveLetters, GetArrayLength(Lines));
  SetArrayLength(MappedDriveRemotePaths, GetArrayLength(Lines));
  PendingDriveLetter := '';

  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Trim(Lines[I]);
    if Pos(DeviceIdPrefix, Line) = 1 then
      PendingDriveLetter := Trim(Copy(Line, Length(DeviceIdPrefix) + 1, Length(Line)))
    else if (Pos(ProviderPrefix, Line) = 1) and (PendingDriveLetter <> '') then
    begin
      MappedDriveLetters[MappedDriveCount] := Uppercase(PendingDriveLetter);
      MappedDriveRemotePaths[MappedDriveCount] := Trim(Copy(Line, Length(ProviderPrefix) + 1, Length(Line)));
      MappedDriveCount := MappedDriveCount + 1;
      PendingDriveLetter := '';
    end;
  end;

  SetArrayLength(MappedDriveLetters, MappedDriveCount);
  SetArrayLength(MappedDriveRemotePaths, MappedDriveCount);

  DeleteFile(OutFile);
  Log('マップトドライブ検出(Method 2): ' + IntToStr(MappedDriveCount) + ' 件');
end;

// 管理者権限で実行中はBrowseForFolderにマップトドライブが表示されないため、
// 検出したドライブを net use で昇格セッションに再マッピングする（Issue #1584）
// /persistent:no で作成したマッピングは昇格セッション終了時に自動消滅するため、
// 明示的な net use /delete は行わない。net use /delete は HKCU\Network の
// レジストリエントリも削除してしまい、元のユーザーマッピングを破壊するため。
procedure RemapDrivesForElevatedSession();
var
  I: Integer;
  ResultCode: Integer;
  NetExe: string;
begin
  if MappedDriveCount = 0 then
    Exit;

  NetExe := ExpandConstant('{sys}\net.exe');

  for I := 0 to MappedDriveCount - 1 do
  begin
    if DirExists(MappedDriveLetters[I] + '\') then
      Log('再マッピング不要（昇格セッションで既にアクセス可能）: ' + MappedDriveLetters[I])
    else
    begin
      if Exec(NetExe,
              'use ' + MappedDriveLetters[I] + ' "' + MappedDriveRemotePaths[I] + '" /persistent:no',
              '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        Log('再マッピング: net use ' + MappedDriveLetters[I] + ' "' + MappedDriveRemotePaths[I] +
            '" → exit code=' + IntToStr(ResultCode))
      else
        Log('再マッピング: net.exe の起動に失敗 (' + MappedDriveLetters[I] + ')');
    end;
  end;
end;

// インストールウィザードにページを追加
procedure InitializeWizard();
var
  ExistingDepartment: string;
  ExistingDbPath: string;
  ExistingReportOutput: string;
begin
  // マップトドライブを検出し、昇格セッションに再マッピング（Issue #1584）
  // Method 1: HKCU\Network レジストリ（永続的マッピング）
  DetectMappedDrives();
  // Method 2: レジストリに無い場合、標準ユーザーセッションの WMI クエリで検出
  if MappedDriveCount = 0 then
    DetectMappedDrivesFromSession();
  RemapDrivesForElevatedSession();

  // =============================================
  // 部署選択ページ（Issue #742）
  // =============================================
  DepartmentPage := CreateInputOptionPage(wpSelectTasks,
    '部署の選択',
    '使用する部署及び支出科目を選択してください。',
    '選択した部署に応じて、チャージ時の摘要と帳票テンプレートが切り替わります。' + #13#10 + #13#10 +
    '※ 本アプリは、現時点では、借損料（駐車場代）や自動車借上料（タクシー料金）には対応していません。' + #13#10 +
    '※ この設定は後から「設定」画面（F5）で変更できます。',
    True, False);
  DepartmentPage.Add('市長事務部局：役務費');
  DepartmentPage.Add('企業会計部局（水道局、交通局等）：旅費 - 乗車券購入費');
  DepartmentPage.SelectedValueIndex := 0;

  // 既存の部署設定を読み込んでデフォルト値として表示（アップグレード時）
  ExistingDepartment := ReadConfigFile('department_config.txt');
  if ExistingDepartment = 'enterprise_account' then
    DepartmentPage.SelectedValueIndex := 1;

  // =============================================
  // DB保存先選択ページ（部署選択の次に表示）
  // =============================================
  DatabasePage := CreateCustomPage(DepartmentPage.ID,
    'データベースの保存先',
    '複数のPCから同時に利用する場合は、共有フォルダを指定してください。' + #13#10 +
    'この設定は後から「設定」画面（F5）で変更できます。');

  DatabaseLocalRadio := TNewRadioButton.Create(DatabasePage);
  DatabaseLocalRadio.Parent := DatabasePage.Surface;
  DatabaseLocalRadio.Caption := 'このPCのみで使用';
  DatabaseLocalRadio.Top := 0;
  DatabaseLocalRadio.Left := 0;
  DatabaseLocalRadio.Width := DatabasePage.SurfaceWidth;
  DatabaseLocalRadio.Height := 20;
  DatabaseLocalRadio.Checked := True;
  DatabaseLocalRadio.OnClick := @DatabaseLocalRadioClick;  // Issue #1655: 選択時に共有パス欄を空にする

  DatabaseSharedRadio := TNewRadioButton.Create(DatabasePage);
  DatabaseSharedRadio.Parent := DatabasePage.Surface;
  DatabaseSharedRadio.Caption := '共有フォルダで複数PCから使用';
  DatabaseSharedRadio.Top := DatabaseLocalRadio.Top + DatabaseLocalRadio.Height + 8;
  DatabaseSharedRadio.Left := 0;
  DatabaseSharedRadio.Width := DatabasePage.SurfaceWidth;
  DatabaseSharedRadio.Height := 20;

  DatabasePathLabel := TNewStaticText.Create(DatabasePage);
  DatabasePathLabel.Parent := DatabasePage.Surface;
  DatabasePathLabel.Caption := '共有フォルダのパス:';
  DatabasePathLabel.Top := DatabaseSharedRadio.Top + DatabaseSharedRadio.Height + 10;
  DatabasePathLabel.Left := 20;

  DatabasePathEdit := TNewEdit.Create(DatabasePage);
  DatabasePathEdit.Parent := DatabasePage.Surface;
  DatabasePathEdit.Top := DatabasePathLabel.Top + DatabasePathLabel.Height + 4;
  DatabasePathEdit.Left := 20;
  DatabasePathEdit.Width := DatabasePage.SurfaceWidth - 100;
  DatabasePathEdit.Text := '';
  DatabasePathEdit.OnChange := @DatabasePathEditChange;

  DatabaseBrowseButton := TNewButton.Create(DatabasePage);
  DatabaseBrowseButton.Parent := DatabasePage.Surface;
  DatabaseBrowseButton.Caption := '参照...';
  DatabaseBrowseButton.Top := DatabasePathEdit.Top - 1;
  DatabaseBrowseButton.Left := DatabasePathEdit.Left + DatabasePathEdit.Width + 8;
  DatabaseBrowseButton.Width := 70;
  DatabaseBrowseButton.Height := DatabasePathEdit.Height + 2;
  DatabaseBrowseButton.OnClick := @DatabaseBrowseButtonClick;

  DatabaseNoteLabel := TNewStaticText.Create(DatabasePage);
  DatabaseNoteLabel.Parent := DatabasePage.Surface;
  DatabaseNoteLabel.Caption := '例: \\server\share\ICCardManager  D:\share\ICCardManager';
  DatabaseNoteLabel.Top := DatabasePathEdit.Top + DatabasePathEdit.Height + 4;
  DatabaseNoteLabel.Left := 20;
  DatabaseNoteLabel.Font.Color := clGray;

  // 既存のDB設定を読み込んでデフォルト値として表示（アップグレード時）
  ExistingDbPath := ReadConfigFile('database_config.txt');
  if ExistingDbPath <> '' then
  begin
    DatabasePathEdit.Text := ExtractFileDir(ExistingDbPath);
    DatabaseSharedRadio.Checked := True;
    DatabaseLocalRadio.Checked := False;
  end;

  // =============================================
  // 帳票出力先選択ページ（DB保存先の次に表示）
  // =============================================
  ReportOutputPage := CreateCustomPage(DatabasePage.ID,
    '帳票出力先フォルダ',
    '帳票（Excel）の出力先フォルダを指定してください。' + #13#10 +
    'この設定は後から帳票作成画面で変更できます。');

  ReportOutputPathLabel := TNewStaticText.Create(ReportOutputPage);
  ReportOutputPathLabel.Parent := ReportOutputPage.Surface;
  ReportOutputPathLabel.Caption := '出力先フォルダ:';
  ReportOutputPathLabel.Top := 0;
  ReportOutputPathLabel.Left := 0;

  ReportOutputPathEdit := TNewEdit.Create(ReportOutputPage);
  ReportOutputPathEdit.Parent := ReportOutputPage.Surface;
  ReportOutputPathEdit.Top := ReportOutputPathLabel.Top + ReportOutputPathLabel.Height + 4;
  ReportOutputPathEdit.Left := 0;
  ReportOutputPathEdit.Width := ReportOutputPage.SurfaceWidth - 100;

  ReportOutputBrowseButton := TNewButton.Create(ReportOutputPage);
  ReportOutputBrowseButton.Parent := ReportOutputPage.Surface;
  ReportOutputBrowseButton.Caption := '参照...';
  ReportOutputBrowseButton.Top := ReportOutputPathEdit.Top - 1;
  ReportOutputBrowseButton.Left := ReportOutputPathEdit.Left + ReportOutputPathEdit.Width + 8;
  ReportOutputBrowseButton.Width := 70;
  ReportOutputBrowseButton.Height := ReportOutputPathEdit.Height + 2;
  ReportOutputBrowseButton.OnClick := @ReportOutputBrowseButtonClick;

  ReportOutputNoteLabel := TNewStaticText.Create(ReportOutputPage);
  ReportOutputNoteLabel.Parent := ReportOutputPage.Surface;
  ReportOutputNoteLabel.Caption := '例: C:\Users\username\Documents';
  ReportOutputNoteLabel.Top := ReportOutputPathEdit.Top + ReportOutputPathEdit.Height + 4;
  ReportOutputNoteLabel.Left := 0;
  ReportOutputNoteLabel.Font.Color := clGray;

  // 既存の帳票出力先設定を読み込んでデフォルト値として表示（アップグレード時）
  // 設定がなければマイドキュメントをデフォルトにする
  ExistingReportOutput := ReadConfigFile('report_output_config.txt');
  if ExistingReportOutput <> '' then
    ReportOutputPathEdit.Text := ExistingReportOutput
  else
    ReportOutputPathEdit.Text := ExpandConstant('{userdocs}');
end;

// 部署選択結果を設定ファイルに書き出す（Issue #742）
procedure WriteDepartmentConfig();
var
  ConfigDir: string;
  ConfigFile: string;
  DepartmentValue: string;
begin
  ConfigDir := ExpandConstant('{commonappdata}\ICCardManager');
  ConfigFile := ConfigDir + '\department_config.txt';

  if DepartmentPage.SelectedValueIndex = 1 then
    DepartmentValue := 'enterprise_account'
  else
    DepartmentValue := 'mayor_office';

  // Issue #2261: 経路に再解析ポイントがあれば、フォルダーの作成も書き込みもしない
  if not IsDataPathSafe(ConfigDir) then
    Exit;
  ForceDirectories(ConfigDir);
  WriteDataFile(ConfigFile, DepartmentValue);
end;

// DB保存先選択結果を設定ファイルに書き出す
procedure WriteDatabaseConfig();
var
  ConfigDir: string;
  ConfigFile: string;
  FullDbPath: string;
  SharedPath: string;
  IsShared: Boolean;
begin
  // まず保存済み変数を使う。未保存ならコントロールから直接読む（フォールバック）
  IsShared := DatabaseUseSharedFolder;
  SharedPath := DatabaseSharedPath;

  if (not IsShared) and (DatabaseSharedRadio <> nil) then
  begin
    IsShared := DatabaseSharedRadio.Checked;
    if IsShared and (DatabasePathEdit <> nil) then
      SharedPath := Trim(DatabasePathEdit.Text);
  end;

  ConfigDir := ExpandConstant('{commonappdata}\ICCardManager');
  ConfigFile := ConfigDir + '\database_config.txt';

  // Issue #1655: 「このPCのみで使用」選択時（または共有パス未指定時）は、前回インストールで
  // 残った共有フォルダ設定を確実に削除する。削除せず Exit するだけだと、旧パス（例: V:\...）が
  // database_config.txt に残存し、ローカル運用を選んだのに起動時へ伝播して共有モードのまま
  // アクセスエラーになる（database_config.txt が無い＝ローカル既定、が正しい挙動）。
  // Issue #2261: 削除・書き込みの前に、経路に再解析ポイントが無いことを確かめる
  // （ここが昇格中の任意ファイル削除に使われ得る経路だった）
  if (not IsShared) or (SharedPath = '') then
  begin
    DeleteDataFile(ConfigFile);
    Exit;
  end;

  // 共有フォルダ: フォルダパス + ファイル名 を書き出す
  FullDbPath := AddBackslash(SharedPath) + 'iccard.db';
  if not IsDataPathSafe(ConfigDir) then
    Exit;
  ForceDirectories(ConfigDir);
  WriteDataFile(ConfigFile, FullDbPath);
end;

// 帳票出力先選択結果を設定ファイルに書き出す
procedure WriteReportOutputConfig();
var
  ConfigDir: string;
  ConfigFile: string;
  OutputPath: string;
begin
  if ReportOutputPathEdit = nil then
    Exit;

  OutputPath := Trim(ReportOutputPathEdit.Text);
  if OutputPath = '' then
    Exit;

  ConfigDir := ExpandConstant('{commonappdata}\ICCardManager');
  ConfigFile := ConfigDir + '\report_output_config.txt';

  // Issue #2261: 経路に再解析ポイントがあれば、フォルダーの作成も書き込みもしない
  if not IsDataPathSafe(ConfigDir) then
    Exit;
  ForceDirectories(ConfigDir);
  WriteDataFile(ConfigFile, OutputPath);
end;

// DB保存先・帳票出力先ページのバリデーション（「次へ」ボタン押下時に呼ばれる）
function NextButtonClick(CurPageID: Integer): Boolean;
var
  SharedPath: string;
  ReportPath: string;
begin
  Result := True;

  // 帳票出力先ページ: 入力がある場合のみ形式チェック（空欄は任意=既定動作なので許可）
  // Issue #1599: DB保存先ページと同等の形式検証（UNC または ドライブレター付き絶対パス）を行い、
  //   不正値が report_output_config.txt に書き込まれ起動時に伝播するのを防ぐ
  if CurPageID = ReportOutputPage.ID then
  begin
    ReportPath := Trim(ReportOutputPathEdit.Text);
    if ReportPath <> '' then
    begin
      if not ((Length(ReportPath) >= 3) and (((ReportPath[1] = '\') and (ReportPath[2] = '\')) or (ReportPath[2] = ':'))) then
      begin
        MsgBox('帳票の出力先フォルダを正しく入力してください。' + #13#10 +
               '例: C:\Users\username\Documents または \\server\share\reports' + #13#10 +
               '（空欄のままにすると既定の出力先が使用されます）', mbError, MB_OK);
        Result := False;
        Exit;
      end;
    end;
  end;

  if CurPageID = DatabasePage.ID then
  begin
    // 「共有フォルダ」が選択されている場合、パスが入力されているかチェック
    if DatabaseSharedRadio.Checked then
    begin
      SharedPath := Trim(DatabasePathEdit.Text);
      if SharedPath = '' then
      begin
        MsgBox('共有フォルダのパスを入力してください。', mbError, MB_OK);
        Result := False;
        Exit;
      end;

      // パス形式のチェック（UNCパスまたはドライブレター付き絶対パス）
      if not ((Length(SharedPath) >= 3) and (((SharedPath[1] = '\') and (SharedPath[2] = '\')) or (SharedPath[2] = ':'))) then
      begin
        MsgBox('共有フォルダのパスを正しく入力してください。' + #13#10 +
               '例: \\server\share\ICCardManager または D:\share\ICCardManager', mbError, MB_OK);
        Result := False;
        Exit;
      end;

      // バリデーション通過: 値を変数に保存
      DatabaseUseSharedFolder := True;
      DatabaseSharedPath := SharedPath;
    end
    else
    begin
      DatabaseUseSharedFolder := False;
      DatabaseSharedPath := '';
    end;
  end;
end;

// Issue #506: アンインストーラーのファイル名を変更
// unins000.exe/dat → Uninstall_ICCardManager.exe/dat
const
  NewUninstallName = 'Uninstall_ICCardManager';

procedure RenameUninstaller();
var
  AppDir: string;
  OldExe, NewExe: string;
  OldDat, NewDat: string;
  UninstallKey: string;
begin
  AppDir := ExpandConstant('{app}');
  OldExe := AppDir + '\unins000.exe';
  NewExe := AppDir + '\' + NewUninstallName + '.exe';
  OldDat := AppDir + '\unins000.dat';
  NewDat := AppDir + '\' + NewUninstallName + '.dat';

  // ファイルが存在する場合のみリネーム
  if FileExists(OldExe) then
  begin
    // 既存のリネーム先があれば削除
    if FileExists(NewExe) then
      DeleteFile(NewExe);
    if FileExists(NewDat) then
      DeleteFile(NewDat);

    // リネーム実行
    RenameFile(OldExe, NewExe);
    if FileExists(OldDat) then
      RenameFile(OldDat, NewDat);

    // レジストリのUninstallStringを更新
    // 注: SetupSetting("AppId") は "{{GUID}" を返す（Inno Setup の記法で {{ は { のエスケープ）
    // Pascal Script ではそのまま展開されるため、StringChange で {{ → { に変換が必要
    UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#StringChange(SetupSetting("AppId"), "{{", "{")}_is1';
    if RegKeyExists(HKLM, UninstallKey) then
    begin
      RegWriteStringValue(HKLM, UninstallKey, 'UninstallString', '"' + NewExe + '"');
      RegWriteStringValue(HKLM, UninstallKey, 'QuietUninstallString', '"' + NewExe + '" /SILENT');
    end;
  end;
end;

// インストール開始前（ファイルのコピー・[Dirs] の処理より前）に呼ばれる。
// 空でない文字列を返すとインストールを中止し、その文言を表示する。
// Issue #2261: 既存のデータフォルダーの所有者を固定して ACL を作り直し、再解析ポイントがあれば中止する
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := HardenDataDirectory();
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RenameUninstaller();
    WriteDepartmentConfig();

    // DB保存先: ssPostInstall時にコントロールから直接読み取って書き込む
    if (DatabaseSharedRadio <> nil) and DatabaseSharedRadio.Checked then
    begin
      if (DatabasePathEdit <> nil) and (Trim(DatabasePathEdit.Text) <> '') then
      begin
        DatabaseUseSharedFolder := True;
        DatabaseSharedPath := Trim(DatabasePathEdit.Text);
      end;
    end;
    WriteDatabaseConfig();
    WriteReportOutputConfig();
  end;
end;

// アンインストール時に Dir を DelTree してよいか（Issue #2261）。
// ProgramData から Dir までの経路と、Dir の配下すべてに再解析ポイントが無く、配下をすべて列挙できたときだけ True。
// 見つけたら削除せず、残したフォルダーを案内する。
function IsDataTreeSafeToDelete(const Dir: string): Boolean;
begin
  Result := not (DataPathHasReparsePoint(Dir) or TreeHasReparsePoint(Dir));
  if not Result then
  begin
    Log('ジャンクション・シンボリックリンク、または中身を確認できないフォルダーがあるため削除しませんでした: ' + Dir);
    if not UninstallSilent then
      MsgBox('次のフォルダーの中に、ジャンクション・シンボリックリンク、または中身を確認できないフォルダーがあるため、削除しませんでした。' + #13#10 +
             Dir + #13#10 + #13#10 +
             '管理者の権限でリンク先のファイルまで削除するおそれがあるためです。' + #13#10 +
             'エクスプローラーでリンクを削除し、開けないフォルダーは中身を確認してから、このフォルダーを手動で削除してください。',
             mbInformation, MB_OK);
  end;
end;

// アンインストール時にユーザーデータを削除するか確認
// TaskDialogMsgBoxを使用した3択ダイアログ
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDataPath: string;
  BackupPath: string;
  LogsPath: string;
  UserDataExists, BackupExists, LogsExists: Boolean;
  ButtonResult: Integer;
  Message: string;
  ButtonLabels: TArrayOfString;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // CommonApplicationData（C:\ProgramData）を使用
    AppDataPath := ExpandConstant('{commonappdata}\ICCardManager');
    BackupPath := AppDataPath + '\backup';
    LogsPath := AppDataPath + '\Logs';

    // 各ディレクトリの存在確認
    UserDataExists := DirExists(AppDataPath);
    BackupExists := DirExists(BackupPath);
    LogsExists := DirExists(LogsPath);

    // データが存在しない場合は何もしない
    if not UserDataExists then
      Exit;

    // メッセージを構築
    Message := '以下のデータが残っています：' + #13#10 + #13#10 +
               '・ユーザーデータ（データベース、設定）' + #13#10 +
               '  ' + AppDataPath + #13#10;

    if BackupExists then
      Message := Message + #13#10 + '・バックアップファイル' + #13#10 +
                 '  ' + BackupPath + #13#10;

    if LogsExists then
      Message := Message + #13#10 + '・ログファイル' + #13#10 +
                 '  ' + LogsPath + #13#10;

    Message := Message + #13#10 + '削除方法を選択してください。';

    // ボタンラベルを設定
    SetArrayLength(ButtonLabels, 3);
    ButtonLabels[0] := 'すべて削除';
    ButtonLabels[1] := 'データのみ残す';
    ButtonLabels[2] := '何も削除しない';

    // TaskDialogMsgBoxで3択ダイアログを表示
    // IDYES=すべて削除、IDNO=データのみ残す、IDCANCEL=何も削除しない
    ButtonResult := TaskDialogMsgBox(
      'データの削除',
      Message,
      mbConfirmation,
      MB_YESNOCANCEL,
      ButtonLabels,
      0);

    // Issue #2261: 昇格したアンインストーラーの DelTree がジャンクションを辿って任意の場所を消さないよう、
    // 削除する範囲（ProgramData からの経路と配下すべて）に再解析ポイントがあれば削除しない
    case ButtonResult of
      IDYES:
        begin
          // すべて削除
          if IsDataTreeSafeToDelete(AppDataPath) then
            DelTree(AppDataPath, True, True, True);
        end;
      IDNO:
        begin
          // バックアップとログのみ削除（ユーザーデータは残す）
          if BackupExists and IsDataTreeSafeToDelete(BackupPath) then
            DelTree(BackupPath, True, True, True);
          if LogsExists and IsDataTreeSafeToDelete(LogsPath) then
            DelTree(LogsPath, True, True, True);
        end;
      // IDCANCEL: 何も削除しない
    end;
  end;
end;
