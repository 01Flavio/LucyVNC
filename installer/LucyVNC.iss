#define AppName "LucyVNC Setup"
#define AppVersion "2.4.3"
#define AppPublisher "01Flavio"

[Setup]
AppId={{B1E4F28D-2F6E-49D7-9E44-8F3DE4386B23}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\LucyVNC
DefaultGroupName=LucyVNC
UninstallDisplayIcon={app}\LucyVNC.Manager.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir=..\artifacts
OutputBaseFilename=LucyVNC-Setup-{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes

[Types]
Name: "both"; Description: "LucyVNC Manager / Viewer and LucyVNC Server"
Name: "manager"; Description: "LucyVNC Manager / Viewer"
Name: "server"; Description: "LucyVNC Server"

[Components]
Name: "manager"; Description: "LucyVNC Manager / Viewer"; Types: manager both
Name: "server"; Description: "LucyVNC Server"; Types: server both

[Tasks]
Name: "managerdesktopicon"; Description: "Criar atalho do LucyVNC Manager na área de trabalho"; GroupDescription: "Atalhos:"; Components: manager
Name: "serverdesktopicon"; Description: "Criar atalho do LucyVNC Server na área de trabalho"; GroupDescription: "Atalhos:"; Components: server

[Files]
Source: "..\artifacts\publish\manager\*"; DestDir: "{app}"; Components: manager; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\server-worker\*"; DestDir: "{app}\Server"; Components: server; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\agent\*"; DestDir: "{app}\Server"; Components: server; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\server-ui\*"; DestDir: "{app}\Server"; Components: server; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\scripts\*.ps1"; DestDir: "{app}\Server\Scripts"; Components: server; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\LucyVNC\LucyVNC Manager"; Filename: "{app}\LucyVNC.Manager.exe"; WorkingDir: "{app}"; Components: manager
Name: "{autodesktop}\LucyVNC Manager"; Filename: "{app}\LucyVNC.Manager.exe"; WorkingDir: "{app}"; Tasks: managerdesktopicon; Components: manager
Name: "{autoprograms}\LucyVNC\LucyVNC Server"; Filename: "{app}\Server\LucyVNC.Server.exe"; WorkingDir: "{app}\Server"; Components: server
Name: "{autodesktop}\LucyVNC Server"; Filename: "{app}\Server\LucyVNC.Server.exe"; WorkingDir: "{app}\Server"; Tasks: serverdesktopicon; Components: server

[Run]
Filename: "{app}\LucyVNC.Manager.exe"; Description: "Abrir LucyVNC Manager"; Flags: postinstall nowait skipifsilent; Components: manager

[Registry]
Root: HKCR; Subkey: ".vnc\OpenWithProgids"; ValueType: none; ValueName: "LucyVNC.VncFile"; Components: manager; Flags: uninsdeletevalue
Root: HKCR; Subkey: "LucyVNC.VncFile"; ValueType: string; ValueName: ""; ValueData: "LucyVNC VNC connection profile"; Components: manager; Flags: uninsdeletekey
Root: HKCR; Subkey: "LucyVNC.VncFile\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\LucyVNC.Manager.exe,0"; Components: manager
Root: HKCR; Subkey: "LucyVNC.VncFile\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\LucyVNC.Manager.exe"" --import-vnc ""%1"""; Components: manager

[Code]
const
  PasswordEnvironmentVariable = 'LUCYVNC_SETUP_PASSWORD';
  ServiceName = 'LucyVNCService';
  AgentStartupRegistryKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
var
  ServerConfigurationPage: TInputQueryWizardPage;
  ServerOptionsPage: TWizardPage;
  NetworkModeCombo: TNewComboBox;
  AllInterfacesCheck: TNewCheckBox;
  NetworkInterfacesInfo: TNewStaticText;
  ShareEnabledCheck: TNewCheckBox;
  ServiceAutomaticCheck: TNewCheckBox;
  AgentAtLogonCheck: TNewCheckBox;
  InstallServiceCheck: TNewCheckBox;
  StartServiceCheck: TNewCheckBox;
  NetworkInterfacesPath: string;
  WildcardWarningConfirmed: Boolean;

function SetEnvironmentVariableW(Name, Value: string): Boolean;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

function IsManagerSelected: Boolean;
begin
  Result := WizardIsComponentSelected('manager');
end;

function IsServerSelected: Boolean;
begin
  Result := WizardIsComponentSelected('server');
end;

function BooleanText(Value: Boolean): string;
begin
  if Value then
    Result := 'true'
  else
    Result := 'false';
end;

function IsServiceInstalled: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query ' + ServiceName, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
    (ResultCode = 0);
end;

function GetEffectiveBindAddress: string; forward;

function RunServerSetupCommand(const Operation: string): Boolean;
var
  ResultCode: Integer;
  CommandLine: string;
begin
  CommandLine := Operation + ' ' +
    BooleanText(ShareEnabledCheck.Checked) + ' ' +
    AddQuotes(GetEffectiveBindAddress) + ' ' +
    Trim(ServerConfigurationPage.Values[1]) + ' ' +
    AddQuotes(Trim(ServerConfigurationPage.Values[2])) + ' ' +
    BooleanText(ServiceAutomaticCheck.Checked) + ' ' +
    BooleanText(AgentAtLogonCheck.Checked) + ' ' +
    BooleanText(AllInterfacesCheck.Checked and (NetworkModeCombo.ItemIndex = 2));

  if not SetEnvironmentVariableW(PasswordEnvironmentVariable, ServerConfigurationPage.Values[3]) then
  begin
    Result := False;
    Exit;
  end;
  try
    Result := Exec(
      ExpandConstant('{app}\Server\LucyVNC.Server.exe'),
      CommandLine,
      ExpandConstant('{app}\Server'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and (ResultCode = 0);
  finally
    SetEnvironmentVariableW(PasswordEnvironmentVariable, '');
  end;
end;

function GetEffectiveBindAddress: string;
begin
  case NetworkModeCombo.ItemIndex of
    0: Result := '127.0.0.1';
    1: Result := Trim(ServerConfigurationPage.Values[0]);
    2: Result := '0.0.0.0';
  else
    Result := '';
  end;
end;

procedure NetworkModeChanged(Sender: TObject);
begin
  WildcardWarningConfirmed := False;
  if NetworkModeCombo.ItemIndex <> 2 then
  begin
    AllInterfacesCheck.Checked := False;
  end;
end;

function RunPowerShell(const ScriptName, Arguments: string): Boolean;
var
  ResultCode: Integer;
  ScriptPath: string;
  CommandLine: string;
begin
  ScriptPath := ExpandConstant('{app}\Server\Scripts\' + ScriptName);
  CommandLine := '-NoProfile -ExecutionPolicy Bypass -File ' + AddQuotes(ScriptPath) + ' ' + Arguments;
  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    CommandLine,
    ExpandConstant('{app}\Server\Scripts'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);
end;

function IsHexDigit(Character: Char): Boolean;
begin
  Result := ((Character >= '0') and (Character <= '9')) or
    ((Character >= 'a') and (Character <= 'f')) or
    ((Character >= 'A') and (Character <= 'F'));
end;

function CountIPv6Groups(const Value: string; var GroupCount: Integer): Boolean;
var
  Index: Integer;
  GroupStart: Integer;
  GroupLength: Integer;
begin
  Result := False;
  GroupCount := 0;
  if Value = '' then
  begin
    Result := True;
    Exit;
  end;

  GroupStart := 1;
  for Index := 1 to Length(Value) + 1 do
  begin
    if (Index > Length(Value)) or (Value[Index] = ':') then
    begin
      GroupLength := Index - GroupStart;
      if (GroupLength < 1) or (GroupLength > 4) then
      begin
        Exit;
      end;
      while GroupStart < Index do
      begin
        if not IsHexDigit(Value[GroupStart]) then
        begin
          Exit;
        end;
        GroupStart := GroupStart + 1;
      end;
      GroupCount := GroupCount + 1;
      GroupStart := Index + 1;
    end;
  end;
  Result := True;
end;

function IsValidIPv6(const Value: string): Boolean;
var
  CompressionIndex: Integer;
  LeftGroups: Integer;
  RightGroups: Integer;
  TotalGroups: Integer;
  LeftPart: string;
  RightPart: string;
begin
  Result := False;
  if (Value = '') or (Pos('.', Value) > 0) then
  begin
    Exit;
  end;

  CompressionIndex := Pos('::', Value);
  if CompressionIndex = 0 then
  begin
    Result := CountIPv6Groups(Value, TotalGroups) and (TotalGroups = 8);
    Exit;
  end;
  if (Pos('::', Copy(Value, CompressionIndex + 2, Length(Value))) > 0) or
    (Pos(':::', Value) > 0) then
  begin
    Exit;
  end;

  LeftPart := Copy(Value, 1, CompressionIndex - 1);
  RightPart := Copy(Value, CompressionIndex + 2, Length(Value));
  if not CountIPv6Groups(LeftPart, LeftGroups) or
    not CountIPv6Groups(RightPart, RightGroups) then
  begin
    Exit;
  end;
  TotalGroups := LeftGroups + RightGroups;
  Result := TotalGroups < 8;
end;

function IsValidIPv4(const Value: string): Boolean;
var
  Index: Integer;
  PartStart: Integer;
  PartLength: Integer;
  PartValue: Integer;
  PartCount: Integer;
  PartText: string;
begin
  Result := False;
  PartStart := 1;
  PartCount := 0;
  for Index := 1 to Length(Value) + 1 do
  begin
    if (Index > Length(Value)) or (Value[Index] = '.') then
    begin
      PartLength := Index - PartStart;
      if (PartLength < 1) or (PartLength > 3) then
      begin
        Exit;
      end;
      PartText := Copy(Value, PartStart, PartLength);
      PartValue := StrToIntDef(PartText, -1);
      if (PartValue < 0) or (PartValue > 255) then
      begin
        Exit;
      end;
      PartCount := PartCount + 1;
      PartStart := Index + 1;
    end
    else if (Value[Index] < '0') or (Value[Index] > '9') then
    begin
      Exit;
    end;
  end;
  Result := PartCount = 4;
end;

function IsAllowedBindAddress(const Value: string): Boolean;
var
  First: Integer;
  Second: Integer;
  FirstDot: Integer;
  SecondDot: Integer;
begin
  Result := False;

  if Pos(':', Value) > 0 then
  begin
    if not IsValidIPv6(Value) then
    begin
      Exit;
    end;
    if CompareText(Value, '::1') = 0 then
    begin
      Result := True;
      Exit;
    end;

    Result := (CompareText(Copy(Value, 1, 2), 'fc') = 0) or
      (CompareText(Copy(Value, 1, 2), 'fd') = 0) or
      (CompareText(Copy(Value, 1, 3), 'fe8') = 0) or
      (CompareText(Copy(Value, 1, 3), 'fe9') = 0) or
      (CompareText(Copy(Value, 1, 3), 'fea') = 0) or
      (CompareText(Copy(Value, 1, 3), 'feb') = 0);
    Exit;
  end;

  if not IsValidIPv4(Value) then
    Exit;
  FirstDot := Pos('.', Value);
  SecondDot := Pos('.', Copy(Value, FirstDot + 1, Length(Value))) + FirstDot;
  First := StrToIntDef(Copy(Value, 1, FirstDot - 1), -1);
  Second := StrToIntDef(Copy(Value, FirstDot + 1, SecondDot - FirstDot - 1), -1);
  Result := (First = 127) or
    (First = 10) or
    ((First = 172) and (Second >= 16) and (Second <= 31)) or
    ((First = 192) and (Second = 168)) or
    ((First = 169) and (Second = 254)) or
    ((First = 100) and (Second >= 64) and (Second <= 127));
end;

function IsValidAccountName(const Value: string): Boolean;
var
  Index: Integer;
  Character: Char;
begin
  Result := Value <> '';
  if not Result then
    Exit;

  for Index := 1 to Length(Value) do
  begin
    Character := Value[Index];
    if not (((Character >= 'a') and (Character <= 'z')) or
      ((Character >= 'A') and (Character <= 'Z')) or
      ((Character >= '0') and (Character <= '9')) or
      (Character = '.') or (Character = '-') or (Character = '_') or
      (Character = '\') or (Character = '@')) then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function IsResolvableWindowsAccount(const AccountName: string): Boolean;
var
  ResultCode: Integer;
  CommandLine: string;
begin
  CommandLine := '-NoProfile -NonInteractive -Command "$ErrorActionPreference=''Stop''; try { $null = ([System.Security.Principal.NTAccount]::new(''' +
    AccountName + ''')).Translate([System.Security.Principal.SecurityIdentifier]); exit 0 } catch { exit 1 }"';
  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    CommandLine,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);
end;

function IsCurrentWindowsAccount(const AccountName: string): Boolean;
var
  ResultCode: Integer;
  CommandLine: string;
begin
  CommandLine := '-NoProfile -NonInteractive -Command "$ErrorActionPreference=''Stop''; try { $accountSid = ([System.Security.Principal.NTAccount]::new(''' +
    AccountName + ''')).Translate([System.Security.Principal.SecurityIdentifier]); $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User; if ($accountSid -eq $currentSid) { exit 0 } else { exit 1 } } catch { exit 1 }"';
  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    CommandLine,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);
end;

function IsSetupPortAvailable(const BindAddress: string; Port: Integer): Boolean;
var
  ResultCode: Integer;
  Script: string;
  CommandLine: string;
begin
  if IsServiceInstalled then
  begin
    Result := True;
    Exit;
  end;

  Script := '$listener = $null; try { $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse(''' +
    BindAddress + '''), ' + IntToStr(Port) + '); $listener.Start(); exit 0 } catch { exit 1 } finally { if ($null -ne $listener) { $listener.Stop() } }';
  CommandLine := '-NoProfile -NonInteractive -Command ' + AddQuotes(Script);
  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    CommandLine,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);
end;

function ReadAvailableNetworkInterfaces: string;
var
  ResultCode: Integer;
  CommandLine: string;
  Script: string;
  Lines: TArrayOfString;
  Index: Integer;
begin
  Result := 'A lista de interfaces não pôde ser consultada. Para interface específica, informe um IPv4 local privado/VPN no campo de endereço.';
  NetworkInterfacesPath := ExpandConstant('{tmp}\LucyVNC-interfaces-' +
    GetDateTimeString('yyyymmddhhnnss', '-', ':') + '.txt');
  Script := '$ErrorActionPreference=''Stop''; Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | ' +
    'Where-Object { $_.AddressState -eq ''Preferred'' } | ForEach-Object { ' +
    '$adapter = Get-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue; ' +
    '''{0} | {1} | {2}'' -f $_.IPAddress, $adapter.Name, $adapter.InterfaceDescription } | ' +
    'Set-Content -LiteralPath $env:LUCYVNC_NETWORK_INTERFACES_OUTPUT -Encoding UTF8';
  CommandLine := '-NoProfile -NonInteractive -Command ' + AddQuotes(Script);
  if SetEnvironmentVariableW('LUCYVNC_NETWORK_INTERFACES_OUTPUT', NetworkInterfacesPath) then
  begin
    try
      if Exec(
        ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
        CommandLine,
        '',
        SW_HIDE,
        ewWaitUntilTerminated,
        ResultCode) and (ResultCode = 0) and FileExists(NetworkInterfacesPath) then
      begin
        if not LoadStringsFromFile(NetworkInterfacesPath, Lines) then
        begin
          Result := 'A lista de interfaces não pôde ser lida. Informe manualmente um IPv4 local privado/VPN.';
        end;
        if GetArrayLength(Lines) > 0 then
        begin
          Result := '';
          for Index := 0 to GetArrayLength(Lines) - 1 do
          begin
            if Index > 0 then
            begin
              Result := Result + #13#10;
            end;
            Result := Result + Lines[Index];
          end;
        end;
        DeleteFile(NetworkInterfacesPath);
      end;
    finally
      SetEnvironmentVariableW('LUCYVNC_NETWORK_INTERFACES_OUTPUT', '');
    end;
  end;
  if Result = '' then
  begin
    Result := 'Nenhum endereço IPv4 disponível foi encontrado. Você pode usar loopback ou informar um endereço privado/VPN local.';
  end;
end;

procedure RemoveAgentStartupRegistryValues;
begin
  RegDeleteValue(HKEY_CURRENT_USER, AgentStartupRegistryKey, 'LucyVNC.Server');
  RegDeleteValue(HKEY_CURRENT_USER, AgentStartupRegistryKey, 'LucyVNC.ServerTray');
  RegDeleteValue(HKEY_CURRENT_USER, AgentStartupRegistryKey, 'LucyVNC.SessionAgent');
end;

procedure InitializeWizard;
begin
  ServerConfigurationPage := CreateInputQueryPage(
    wpSelectComponents,
    'LucyVNC Server Configuration',
    'Configure o LucyVNC Server independente',
    'VNC clássico usa senha ASCII de 6 a 8 caracteres; senhas maiores não são aceitas porque o protocolo ignora bytes após o oitavo. O tráfego não é criptografado; use VPN para conexões remotas.');
  ServerConfigurationPage.Add('Endereço IPv4 para modo específico (modo padrão usa 0.0.0.0)', False);
  ServerConfigurationPage.Add('Porta TCP', False);
  ServerConfigurationPage.Add('Usuário Windows autorizado para o Agent', False);
  ServerConfigurationPage.Add('Senha clássica VNC (6 a 8 caracteres ASCII)', True);
  ServerConfigurationPage.Values[0] := '0.0.0.0';
  ServerConfigurationPage.Values[1] := '5900';
  ServerConfigurationPage.Values[2] := ExpandConstant('{username}');

  ServerOptionsPage := CreateCustomPage(
    ServerConfigurationPage.ID,
    'LucyVNC Server Options',
    'O LucyVNC Server está configurado para escutar em todas as interfaces IPv4. A autenticação VNC clássica não criptografa o tráfego. Utilize redes confiáveis ou VPN e não exponha a porta diretamente à internet. Escolha um escopo de rede e confirme explicitamente antes de continuar.');

  NetworkModeCombo := TNewComboBox.Create(ServerOptionsPage);
  NetworkModeCombo.Parent := ServerOptionsPage.Surface;
  NetworkModeCombo.Left := ScaleX(0);
  NetworkModeCombo.Top := ScaleY(8);
  NetworkModeCombo.Width := ScaleX(460);
  NetworkModeCombo.Style := csDropDownList;
  NetworkModeCombo.Items.Add('Somente este computador (127.0.0.1)');
  NetworkModeCombo.Items.Add('Interface IPv4 específica (informe o endereço acima)');
  NetworkModeCombo.Items.Add('Todas as interfaces IPv4 (0.0.0.0; LAN e VPN)');
  NetworkModeCombo.ItemIndex := 2;

  NetworkInterfacesInfo := TNewStaticText.Create(ServerOptionsPage);
  NetworkInterfacesInfo.Parent := ServerOptionsPage.Surface;
  NetworkInterfacesInfo.Left := ScaleX(0);
  NetworkInterfacesInfo.Top := NetworkModeCombo.Top + ScaleY(28);
  NetworkInterfacesInfo.Width := ScaleX(570);
  NetworkInterfacesInfo.Height := ScaleY(68);
  NetworkInterfacesInfo.AutoSize := False;
  NetworkInterfacesInfo.WordWrap := True;
  NetworkInterfacesInfo.Caption := 'Interfaces IPv4 detectadas:' + #13#10 + ReadAvailableNetworkInterfaces;

  AllInterfacesCheck := TNewCheckBox.Create(ServerOptionsPage);
  AllInterfacesCheck.Parent := ServerOptionsPage.Surface;
  AllInterfacesCheck.Left := ScaleX(0);
  AllInterfacesCheck.Top := NetworkInterfacesInfo.Top + NetworkInterfacesInfo.Height + ScaleY(2);
  AllInterfacesCheck.Width := ScaleX(570);
  AllInterfacesCheck.Caption := 'Confirmo o bind em todas as interfaces IPv4. Não altera firewall, roteador ou UPnP.';
  AllInterfacesCheck.Checked := False;
  NetworkModeCombo.OnChange := @NetworkModeChanged;

  ShareEnabledCheck := TNewCheckBox.Create(ServerOptionsPage);
  ShareEnabledCheck.Parent := ServerOptionsPage.Surface;
  ShareEnabledCheck.Left := ScaleX(0);
  ShareEnabledCheck.Top := AllInterfacesCheck.Top + ScaleY(26);
  ShareEnabledCheck.Width := ScaleX(510);
  ShareEnabledCheck.Caption := 'Habilitar compartilhamento autenticado';
  ShareEnabledCheck.Checked := False;

  ServiceAutomaticCheck := TNewCheckBox.Create(ServerOptionsPage);
  ServiceAutomaticCheck.Parent := ServerOptionsPage.Surface;
  ServiceAutomaticCheck.Left := ShareEnabledCheck.Left;
  ServiceAutomaticCheck.Top := ShareEnabledCheck.Top + ScaleY(26);
  ServiceAutomaticCheck.Width := ScaleX(510);
  ServiceAutomaticCheck.Caption := 'Iniciar o serviço automaticamente com o Windows';
  ServiceAutomaticCheck.Checked := True;

  AgentAtLogonCheck := TNewCheckBox.Create(ServerOptionsPage);
  AgentAtLogonCheck.Parent := ServerOptionsPage.Surface;
  AgentAtLogonCheck.Left := ShareEnabledCheck.Left;
  AgentAtLogonCheck.Top := ServiceAutomaticCheck.Top + ScaleY(26);
  AgentAtLogonCheck.Width := ScaleX(560);
  AgentAtLogonCheck.Caption := 'Iniciar LucyVNC Server e Agent no logon do usuário autorizado';
  AgentAtLogonCheck.Checked := True;

  InstallServiceCheck := TNewCheckBox.Create(ServerOptionsPage);
  InstallServiceCheck.Parent := ServerOptionsPage.Surface;
  InstallServiceCheck.Left := ShareEnabledCheck.Left;
  InstallServiceCheck.Top := AgentAtLogonCheck.Top + ScaleY(30);
  InstallServiceCheck.Width := ScaleX(560);
  InstallServiceCheck.Caption := 'Autorizo instalar/atualizar o serviço Windows (pode solicitar elevação)';
  InstallServiceCheck.Checked := False;

  StartServiceCheck := TNewCheckBox.Create(ServerOptionsPage);
  StartServiceCheck.Parent := ServerOptionsPage.Surface;
  StartServiceCheck.Left := ShareEnabledCheck.Left;
  StartServiceCheck.Top := InstallServiceCheck.Top + ScaleY(26);
  StartServiceCheck.Width := ScaleX(560);
  StartServiceCheck.Caption := 'Iniciar o compartilhamento após concluir a instalação';
  StartServiceCheck.Checked := False;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PageID = ServerConfigurationPage.ID) or (PageID = ServerOptionsPage.ID)) and not IsServerSelected;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
  Username: string;
  Index: Integer;
  BindAddress: string;
begin
  Result := True;
  if (CurPageID = wpSelectComponents) and IsServiceInstalled and not IsServerSelected then
  begin
    MsgBox('O serviço LucyVNC Server já está instalado. Selecione LucyVNC Server para atualizá-lo ou cancele e remova-o antes de continuar.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if CurPageID = ServerOptionsPage.ID then
  begin
    BindAddress := GetEffectiveBindAddress;
    case NetworkModeCombo.ItemIndex of
      0:
        BindAddress := '127.0.0.1';
      1:
        begin
          if (Pos(':', BindAddress) > 0) or
            not IsAllowedBindAddress(BindAddress) or
            (CompareText(BindAddress, '127.0.0.1') = 0) then
          begin
            MsgBox('Informe na página anterior o IPv4 privado/VPN da interface local desejada.', mbError, MB_OK);
            Result := False;
            Exit;
          end;
        end;
      2:
        begin
          if not AllInterfacesCheck.Checked then
          begin
            MsgBox('Marque a confirmação explícita para usar 0.0.0.0. Isso não abre o firewall, não configura o roteador nem habilita UPnP.', mbError, MB_OK);
            Result := False;
            Exit;
          end;
          if not WildcardWarningConfirmed then
          begin
            if MsgBox(
                'O LucyVNC Server está configurado para escutar em todas as interfaces IPv4. A autenticação VNC clássica não criptografa o tráfego. Utilize redes confiáveis ou VPN e não exponha a porta diretamente à internet. Continuar?',
                mbConfirmation,
                MB_YESNO) <> IDYES then
            begin
              Result := False;
              Exit;
            end;
            WildcardWarningConfirmed := True;
          end;
        end;
    else
    begin
      Result := False;
      Exit;
    end;
    end;

    if not IsSetupPortAvailable(
        BindAddress,
        StrToIntDef(Trim(ServerConfigurationPage.Values[1]), 0)) then
    begin
      MsgBox('A porta TCP selecionada não está disponível nesse endereço. Escolha outra porta ou encerre o aplicativo que a está usando.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if AgentAtLogonCheck.Checked and not IsCurrentWindowsAccount(Trim(ServerConfigurationPage.Values[2])) then
    begin
      MsgBox('O início do Agent no logon exige que o usuário Windows autorizado seja a conta atual. Desmarque a opção ou informe a conta atual.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if StartServiceCheck.Checked and not ShareEnabledCheck.Checked then
    begin
      MsgBox('Para iniciar o serviço agora, primeiro habilite o compartilhamento autenticado.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if StartServiceCheck.Checked and not InstallServiceCheck.Checked then
    begin
      MsgBox('Para iniciar o compartilhamento agora, autorize primeiro a instalação do serviço Windows.', mbError, MB_OK);
      Result := False;
    end;
    Exit;
  end;
  if CurPageID <> ServerConfigurationPage.ID then
  begin
    Exit;
  end;

  if (Length(ServerConfigurationPage.Values[3]) < 6) or
    (Length(ServerConfigurationPage.Values[3]) > 8) then
  begin
    MsgBox('A senha clássica VNC precisa ter de 6 a 8 caracteres ASCII. O protocolo não utiliza bytes após o oitavo.', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  for Index := 1 to Length(ServerConfigurationPage.Values[3]) do
  begin
    if (Ord(ServerConfigurationPage.Values[3][Index]) < 32) or
      (Ord(ServerConfigurationPage.Values[3][Index]) > 126) then
    begin
      MsgBox('A senha clássica VNC precisa conter somente caracteres ASCII imprimíveis.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;

  if StrToIntDef(Trim(ServerConfigurationPage.Values[1]), 0) < 1 then
  begin
    MsgBox('Informe uma porta TCP entre 1 e 65535.', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  Port := StrToIntDef(Trim(ServerConfigurationPage.Values[1]), 0);
  if Port > 65535 then
  begin
    MsgBox('Informe uma porta TCP entre 1 e 65535.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  Username := Trim(ServerConfigurationPage.Values[2]);
  if not IsValidAccountName(Username) then
  begin
    MsgBox('Informe um nome de usuário Windows (por exemplo COMPUTER\User ou usuario@dominio).', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  if not IsResolvableWindowsAccount(Username) then
  begin
    MsgBox('O usuário Windows informado não foi encontrado neste computador/domínio.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if IsServiceInstalled and not InstallServiceCheck.Checked then
  begin
    MsgBox('O serviço já existe. Autorize sua atualização ou remova-o antes de continuar.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  StartupType: string;
  UpdateArgument: string;
  StartTrayResult: Integer;
  StartTray: Boolean;
  ProvisionSucceeded: Boolean;
begin
  if CurStep <> ssPostInstall then
  begin
    Exit;
  end;
  if not IsServerSelected then
  begin
    if IsServiceInstalled then
    begin
      RaiseException('O serviço LucyVNC Server está instalado e não pode ficar sem os arquivos do componente Server.');
    end;
    RemoveAgentStartupRegistryValues;
    if DirExists(ExpandConstant('{app}\Server')) then
    begin
      if not DelTree(ExpandConstant('{app}\Server'), True, True, True) then
      begin
        RaiseException('Não foi possível remover arquivos Server antigos. Feche os processos LucyVNC Server e tente novamente.');
      end;
    end;
    Exit;
  end;

  if not RunServerSetupCommand('--validate-setup') then
  begin
    ServerConfigurationPage.Values[3] := '';
    RaiseException('A configuração final do LucyVNC Server foi rejeitada pelo validador. Nenhuma credencial foi aplicada; revise rede, porta e senha e execute novamente.');
  end;

  ProvisionSucceeded := RunServerSetupCommand('--provision-setup');
  ServerConfigurationPage.Values[3] := '';
  if not ProvisionSucceeded then
  begin
    RaiseException('Não foi possível aplicar a configuração protegida do LucyVNC Server.');
  end;
  if not InstallServiceCheck.Checked then
  begin
    Exit;
  end;

  if ServiceAutomaticCheck.Checked then
    StartupType := 'Automatic'
  else
    StartupType := 'Manual';

  if IsServiceInstalled then
    UpdateArgument := '-UpdateExisting'
  else
    UpdateArgument := '';

  if not RunPowerShell(
    'Install-LucyServerService.ps1',
    '-ExecutablePath ' + AddQuotes(ExpandConstant('{app}\Server\LucyVNC.Server.Worker.exe')) +
    ' -StartupType ' + StartupType + ' ' + UpdateArgument) then
  begin
    RaiseException('A configuração protegida foi salva, mas não foi possível instalar/atualizar o serviço. Abra LucyVNC Server Configuration para corrigir o estado.');
  end;

  if AgentAtLogonCheck.Checked then
  begin
    StartTray := Exec(
      ExpandConstant('{app}\Server\LucyVNC.Server.exe'),
      '',
      ExpandConstant('{app}\Server'),
      SW_HIDE,
      ewNoWait,
      StartTrayResult);
    if not StartTray then
    begin
      RaiseException('O serviço foi instalado, mas não foi possível iniciar LucyVNC Server na sessão interativa.');
    end;
  end;

  if StartServiceCheck.Checked then
  begin
    if not RunPowerShell('Start-LucyServerService.ps1', '') then
    begin
      RaiseException('O serviço foi instalado, mas não foi possível iniciá-lo. Consulte os diagnósticos do LucyVNC Server.');
    end;
  end;
end;

function InitializeUninstall: Boolean;
var
  ScriptPath: string;
  CommandLine: string;
  ResultCode: Integer;
begin
  Result := True;
  if not IsServiceInstalled then
  begin
    Exit;
  end;

  if MsgBox(
    'O serviço LucyVNC Server está instalado. Removê-lo agora? A configuração protegida será preservada.',
    mbConfirmation,
    MB_YESNO) <> IDYES then
  begin
    Result := False;
    Exit;
  end;

  ScriptPath := ExpandConstant('{app}\Server\Scripts\Uninstall-LucyServerService.ps1');
  if not FileExists(ScriptPath) then
  begin
    ScriptPath := ExpandConstant('{app}\Scripts\Uninstall-LucyServerService.ps1');
  end;
  CommandLine := '-NoProfile -ExecutionPolicy Bypass -File ' + AddQuotes(ScriptPath) + ' -Force';
  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    CommandLine,
    ExtractFileDir(ScriptPath),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);

  if not Result then
  begin
    MsgBox('Não foi possível remover o serviço. O aplicativo não será desinstalado para evitar arquivos órfãos.', mbError, MB_OK);
    Exit;
  end;

  RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LucyVNC.Server');
  RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LucyVNC.ServerTray');
  RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LucyVNC.SessionAgent');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveAgentStartupRegistryValues;
  end;
end;
