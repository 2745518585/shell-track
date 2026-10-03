#ifndef BundleDir
 #error BundleDir is required
#endif
#ifndef InstallerVersion
 #define InstallerVersion "0.3.0"
#endif

[Setup]
AppId={{A795A862-EA04-4A12-A129-98698D24C9B0}
AppName=Shell Track
AppVersion={#InstallerVersion}
AppPublisher=Shell Track
DefaultDirName={localappdata}\Programs\ShellTrack
DefaultGroupName=Shell Track
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputBaseFilename=ShellTrack-{#InstallerVersion}-win-x64-Setup
SetupIconFile={#BundleDir}\desktop\assets\shelltrack.ico
UninstallDisplayIcon={app}\desktop\ShellTrack.Desktop.exe
LicenseFile={#BundleDir}\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes
CloseApplications=yes
RestartApplications=no
SignTool=shelltrack
SignedUninstaller=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "userpath"; Description: "Add command-line tools to your user PATH"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#BundleDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Shell Track"; Filename: "{app}\desktop\ShellTrack.Desktop.exe"; AppUserModelID: "ShellTrack.Desktop"
Name: "{autodesktop}\Shell Track"; Filename: "{app}\desktop\ShellTrack.Desktop.exe"; Tasks: desktopicon; AppUserModelID: "ShellTrack.Desktop"

[Run]
Filename: "{app}\desktop\ShellTrack.Desktop.exe"; Description: "Open Shell Track"; Flags: nowait postinstall skipifsilent

[Code]
const
  OwnershipKey = 'Software\ShellTrack\Installer';

function SamePath(Left, Right: String): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(Trim(Left)), RemoveBackslashUnlessRoot(Trim(Right))) = 0;
end;

function HasPath(Value, Directory: String): Boolean;
var Items: TArrayOfString; I: Integer;
begin
  Result := False;
  Items := StringSplit(Value, [';'], stAll);
  for I := 0 to GetArrayLength(Items) - 1 do
    if SamePath(Items[I], Directory) then begin Result := True; Exit; end;
end;

procedure RemoveOwnedPath;
var Value, Directory, Updated: String; Items: TArrayOfString; I: Integer; Removed, HasItem: Boolean;
begin
  if not RegQueryStringValue(HKCU, OwnershipKey, 'AddedPath', Directory) then Exit;
  if RegQueryStringValue(HKCU, 'Environment', 'Path', Value) then begin
    Items := StringSplit(Value, [';'], stAll);
    Updated := ''; Removed := False; HasItem := False;
    for I := 0 to GetArrayLength(Items) - 1 do begin
      if (not Removed) and SamePath(Items[I], Directory) then Removed := True
      else begin
        if HasItem then Updated := Updated + ';';
        Updated := Updated + Items[I];
        HasItem := True;
      end;
    end;
    if Removed then RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Updated);
  end;
  RegDeleteValue(HKCU, OwnershipKey, 'AddedPath');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Value, Directory, Previous: String;
begin
  if CurStep <> ssPostInstall then Exit;
  Directory := ExpandConstant('{app}');
  if RegQueryStringValue(HKCU, OwnershipKey, 'AddedPath', Previous) then
    if (not WizardIsTaskSelected('userpath')) or (not SamePath(Previous, Directory)) then RemoveOwnedPath;
  if WizardIsTaskSelected('userpath') then begin
    if not RegQueryStringValue(HKCU, 'Environment', 'Path', Value) then Value := '';
    if not HasPath(Value, Directory) then begin
      { Always add our own delimiter: an existing trailing delimiter belongs to the user. }
      if Value <> '' then Value := Value + ';';
      RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Value + Directory);
      RegWriteStringValue(HKCU, OwnershipKey, 'AddedPath', Directory);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Activator, Server, Expected, ClassKey: String; I: Integer;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  RemoveOwnedPath;
  { Only remove notification registration if it still points to this installation. }
  if not RegQueryStringValue(HKCU, 'Software\Classes\AppUserModelId\ShellTrack.Desktop', 'CustomActivator', Activator) then Exit;
  if Length(Activator) = 36 then Activator := '{' + Activator + '}';
  if Length(Activator) <> 38 then Exit;
  for I := 1 to Length(Activator) do
    if Pos(Activator[I], '{}0123456789ABCDEFabcdef-') = 0 then Exit;
  ClassKey := 'Software\Classes\CLSID\' + Activator;
  if not RegQueryStringValue(HKCU, ClassKey + '\LocalServer32', '', Server) then Exit;
  Expected := '"' + ExpandConstant('{app}\desktop\ShellTrack.Desktop.exe') + '"';
  if CompareText(Copy(Server, 1, Length(Expected)), Expected) <> 0 then Exit;
  RegDeleteKeyIncludingSubkeys(HKCU, ClassKey);
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\AppUserModelId\ShellTrack.Desktop');
end;
