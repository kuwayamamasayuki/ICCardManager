; ICCardManager インストーラースクリプト
; Inno Setup 6.x 用

#define MyAppName "交通系ICカード管理システム"
#define MyAppNameEn "ICCardManager"
#define MyAppVersion "1.0.0"
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

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

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
Source: "..\docs\manual\ユーザーマニュアル.md"; DestDir: "{app}\Docs"; Flags: ignoreversion
Source: "..\docs\manual\ユーザーマニュアル概要版.md"; DestDir: "{app}\Docs"; Flags: ignoreversion
Source: "..\docs\manual\管理者マニュアル.md"; DestDir: "{app}\Docs"; Flags: ignoreversion
; docx形式
Source: "..\docs\manual\ユーザーマニュアル.docx"; DestDir: "{app}\Docs"; Flags: ignoreversion
Source: "..\docs\manual\ユーザーマニュアル概要版（修正版）.docx"; DestDir: "{app}\Docs"; Flags: ignoreversion
Source: "..\docs\manual\管理者マニュアル.docx"; DestDir: "{app}\Docs"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"
Name: "{group}\ドキュメント"; Filename: "{app}\Docs"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
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
