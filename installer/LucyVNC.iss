#define AppName "LucyVNC"
#define AppVersion "2.3.0"
#define AppPublisher "01Flavio"

[Setup]
AppId={{B1E4F28D-2F6E-49D7-9E44-8F3DE4386B23}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\LucyVNC
DefaultGroupName=LucyVNC
UninstallDisplayIcon={app}\LucyViewer.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
OutputDir=..\artifacts
OutputBaseFilename=LucyVNC-Setup-{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"

[Files]
Source: "..\src\Vnc.App\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\src\Vnc.Service\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\src\Vnc.Agent\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}\Agent"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\scripts\*.ps1"; DestDir: "{app}\Scripts"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\LucyVNC\LucyVNC Manager"; Filename: "{app}\LucyViewer.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\LucyVNC Manager"; Filename: "{app}\LucyViewer.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\LucyViewer.exe"; Description: "Abrir LucyVNC Manager"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v ""LucyVNC.SessionAgent"" /f"; Flags: runhidden waituntilterminated; StatusMsg: "Removendo a inicialização automática do agente para este usuário..."

[Code]
function LucyServerServiceIsInstalled(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query LucyVNCService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if LucyServerServiceIsInstalled() then
  begin
    MsgBox('O serviço LucyServer ainda está instalado. Abra LucyVNC Manager > Acessos, desinstale o serviço e tente novamente. A configuração protegida em ProgramData será preservada.', mbError, MB_OK);
    Result := False;
  end;
end;
