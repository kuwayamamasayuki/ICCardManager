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
; データディレクトリ（C:\ProgramData\ICCardManager）にUsersフルコントロールを設定
; 複数の職員（Windowsユーザー）が同一PCで利用するため全ユーザーにアクセスを許可
Name: "{commonappdata}\ICCardManager"; Permissions: users-full
Name: "{commonappdata}\ICCardManager\backup"; Permissions: users-full
Name: "{commonappdata}\ICCardManager\Logs"; Permissions: users-full

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
; docx形式
Source: "..\docs\manual\はじめに.docx"; DestDir: "{app}\Docs"; Flags: ignoreversion
Source: "..\docs\manual\ユーザーマニュアル.docx"; DestDir: "{app}\Docs"; Flags: ignoreversion
Source: "..\docs\manual\ユーザーマニュアル概要版（修正版）.docx"; DestDir: "{app}\Docs"; Flags: ignoreversion
Source: "..\docs\manual\管理者マニュアル.docx"; DestDir: "{app}\Docs"; Flags: ignoreversion
; PDF形式（Issue #642）
Source: "..\docs\manual\はじめに.pdf"; DestDir: "{app}\Docs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\docs\manual\ユーザーマニュアル.pdf"; DestDir: "{app}\Docs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\docs\manual\ユーザーマニュアル概要版.pdf"; DestDir: "{app}\Docs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\docs\manual\管理者マニュアル.pdf"; DestDir: "{app}\Docs"; Flags: ignoreversion skipifsourcedoesntexist

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

// インストールウィザードにページを追加
procedure InitializeWizard();
begin
  // 部署選択ページ（Issue #742）
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

  // DB保存先選択ページ（部署選択の次に表示）
  DatabasePage := CreateCustomPage(DepartmentPage.ID,
    'データベースの保存先',
    '複数のPCから同時に利用する場合は、共有フォルダを指定してください。');

  DatabaseLocalRadio := TNewRadioButton.Create(DatabasePage);
  DatabaseLocalRadio.Parent := DatabasePage.Surface;
  DatabaseLocalRadio.Caption := 'このPCのみで使用（従来どおり）';
  DatabaseLocalRadio.Top := 10;
  DatabaseLocalRadio.Left := 0;
  DatabaseLocalRadio.Width := DatabasePage.SurfaceWidth;
  DatabaseLocalRadio.Checked := True;
  DatabaseLocalRadio.Font.Style := [fsBold];

  DatabaseSharedRadio := TNewRadioButton.Create(DatabasePage);
  DatabaseSharedRadio.Parent := DatabasePage.Surface;
  DatabaseSharedRadio.Caption := '共有フォルダで複数のPCから使用';
  DatabaseSharedRadio.Top := DatabaseLocalRadio.Top + DatabaseLocalRadio.Height + 20;
  DatabaseSharedRadio.Left := 0;
  DatabaseSharedRadio.Width := DatabasePage.SurfaceWidth;
  DatabaseSharedRadio.Font.Style := [fsBold];

  DatabasePathLabel := TNewStaticText.Create(DatabasePage);
  DatabasePathLabel.Parent := DatabasePage.Surface;
  DatabasePathLabel.Caption := '共有フォルダのパス:';
  DatabasePathLabel.Top := DatabaseSharedRadio.Top + DatabaseSharedRadio.Height + 12;
  DatabasePathLabel.Left := 20;

  DatabasePathEdit := TNewEdit.Create(DatabasePage);
  DatabasePathEdit.Parent := DatabasePage.Surface;
  DatabasePathEdit.Top := DatabasePathLabel.Top + DatabasePathLabel.Height + 4;
  DatabasePathEdit.Left := 20;
  DatabasePathEdit.Width := DatabasePage.SurfaceWidth - 40;
  DatabasePathEdit.Text := '';

  DatabaseNoteLabel := TNewStaticText.Create(DatabasePage);
  DatabaseNoteLabel.Parent := DatabasePage.Surface;
  DatabaseNoteLabel.Caption :=
    '例: \\server\share\ICCardManager' + #13#10 +
    '' + #13#10 +
    '※ 共有フォルダは事前に作成しておく必要があります' + #13#10 +
    '※ この設定は後から「設定」画面（F5）で変更できます';
  DatabaseNoteLabel.Top := DatabasePathEdit.Top + DatabasePathEdit.Height + 10;
  DatabaseNoteLabel.Left := 20;
  DatabaseNoteLabel.Width := DatabasePage.SurfaceWidth - 40;
  DatabaseNoteLabel.AutoSize := False;
  DatabaseNoteLabel.Height := 80;
  DatabaseNoteLabel.Font.Color := clGray;
end;

// 部署選択結果を設定ファイルに書き出す（Issue #742）
procedure WriteDepartmentConfig();
var
  ConfigDir: string;
  ConfigFile: string;
  DepartmentValue: string;
begin
  ConfigDir := ExpandConstant('{commonappdata}\ICCardManager');
  ForceDirectories(ConfigDir);
  ConfigFile := ConfigDir + '\department_config.txt';

  if DepartmentPage.SelectedValueIndex = 1 then
    DepartmentValue := 'enterprise_account'
  else
    DepartmentValue := 'mayor_office';

  SaveStringToFile(ConfigFile, DepartmentValue, False);
end;

// DB保存先選択結果を設定ファイルに書き出す
procedure WriteDatabaseConfig();
var
  ConfigDir: string;
  ConfigFile: string;
  SharedPath: string;
  FullDbPath: string;
begin
  // 「このPCのみ」が選択された場合、設定ファイルは作成しない（デフォルト動作）
  if DatabaseLocalRadio.Checked then
    Exit;

  SharedPath := Trim(DatabasePathEdit.Text);
  if SharedPath = '' then
    Exit;

  // フォルダパス + ファイル名
  FullDbPath := AddBackslash(SharedPath) + 'iccard.db';

  ConfigDir := ExpandConstant('{commonappdata}\ICCardManager');
  ForceDirectories(ConfigDir);
  ConfigFile := ConfigDir + '\database_config.txt';

  SaveStringToFile(ConfigFile, FullDbPath, False);
end;

// DB保存先ページのバリデーション（「次へ」ボタン押下時に呼ばれる）
function NextButtonClick(CurPageID: Integer): Boolean;
var
  SharedPath: string;
begin
  Result := True;

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

      // UNCパス形式のチェック（\\で始まるか）
      if (Length(SharedPath) < 3) or (SharedPath[1] <> '\') or (SharedPath[2] <> '\') then
      begin
        MsgBox('共有フォルダのパスは \\サーバー名\共有名 の形式で入力してください。' + #13#10 +
               '例: \\server\share\ICCardManager', mbError, MB_OK);
        Result := False;
        Exit;
      end;
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

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RenameUninstaller();
    WriteDepartmentConfig();
    WriteDatabaseConfig();
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

    case ButtonResult of
      IDYES:
        begin
          // すべて削除
          DelTree(AppDataPath, True, True, True);
        end;
      IDNO:
        begin
          // バックアップとログのみ削除（ユーザーデータは残す）
          if BackupExists then
            DelTree(BackupPath, True, True, True);
          if LogsExists then
            DelTree(LogsPath, True, True, True);
        end;
      // IDCANCEL: 何も削除しない
    end;
  end;
end;
