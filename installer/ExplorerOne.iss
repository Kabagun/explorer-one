#define PublishedDirectory AddBackslash(GetEnv("EXPLORERONE_PUBLISHED_DIRECTORY"))
#define OutputDirectory AddBackslash(GetEnv("EXPLORERONE_OUTPUT_DIRECTORY"))
#define ProductVersion GetEnv("EXPLORERONE_VERSION")
#define RouterDllName GetEnv("EXPLORERONE_ROUTER_DLL")
#define DotNetRuntimeUrl GetEnv("EXPLORERONE_DOTNET_URL")
#define DotNetRuntimeSha256 GetEnv("EXPLORERONE_DOTNET_SHA256")
#define WindowsDesktopRuntimeUrl GetEnv("EXPLORERONE_WINDOWS_DESKTOP_URL")
#define WindowsDesktopRuntimeSha256 GetEnv("EXPLORERONE_WINDOWS_DESKTOP_SHA256")

[Setup]
AppId={{D47627FB-9A4B-49AD-8B52-444733D91F80}
AppName=ExplorerOne
AppVersion={#ProductVersion}
AppVerName=ExplorerOne {#ProductVersion}
VersionInfoVersion={#ProductVersion}.0
VersionInfoProductName=ExplorerOne
AppPublisher=ExplorerOne
AppPublisherURL=https://github.com/Kabagun/explorer-one
AppSupportURL=https://github.com/Kabagun/explorer-one/issues
AppUpdatesURL=https://github.com/Kabagun/explorer-one/releases
DefaultDirName={localappdata}\Programs\ExplorerOne
DefaultGroupName=ExplorerOne
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22621
CloseApplications=no
Uninstallable=yes
UninstallDisplayName=ExplorerOne
UninstallDisplayIcon={app}\ExplorerOne.exe
SetupIconFile=..\src\ExplorerOne\Assets\explorer-one.ico
LicenseFile=..\LICENSE
OutputDir={#OutputDirectory}
OutputBaseFilename=ExplorerOne-Setup-{#ProductVersion}-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
ArchiveExtraction=full
WizardStyle=modern
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
english.TaskGroup=Optional startup
english.EnableStartup=Start ExplorerOne when I sign in to Windows
english.StartupStatusError=ExplorerOne could not read the current startup setting. No files were changed. Close ExplorerOne if it is running and try Setup again.
english.ShutdownError=ExplorerOne could not confirm that it has stopped (exit code %1). Exit ExplorerOne from its tray menu, then run Setup again. Setup has not replaced application files.
english.ShutdownLaunchError=The ExplorerOne shutdown command could not be started. Exit ExplorerOne from its tray menu, then run Setup again.
english.StartupEnableError=ExplorerOne was installed, but Windows startup could not be enabled (exit code %1). Use the tray menu to retry.
english.StartupDisableError=ExplorerOne was installed, but the previous startup registration could not be removed (exit code %1). Use the tray menu to retry.
english.StartupRunError=ExplorerOne could not be started. Start it from the Start menu.
english.UninstallCommandError=ExplorerOne could not finish its shutdown or startup cleanup (exit code %1). The installation was kept. Exit the app from its tray menu and try uninstalling again.
english.UninstallCommandLaunchError=ExplorerOne's shutdown or startup cleanup command could not be started. The installation was kept.
english.UninstallMissingExe=ExplorerOne.exe is missing, so Setup cannot safely stop ExplorerOne or remove its startup task. Restore the application files and try uninstalling again.
english.RuntimeDownloadError=The .NET 10 runtimes could not be downloaded or verified. Check your internet connection and run Setup again. No application files were changed.
russian.RuntimeDownloadError=Не удалось скачать или проверить компоненты среды выполнения .NET 10. Проверьте подключение к Интернету и повторите запуск установщика. Файлы приложения не изменены.
english.RetainedRouterNotice=Windows is still using one or more ExplorerOne native router files. They were left in this folder and can be removed after Explorer restarts:%n%n%1
russian.TaskGroup=Необязательный автозапуск
russian.EnableStartup=Запускать ExplorerOne при входе в Windows
russian.StartupStatusError=Не удалось прочитать текущее состояние автозапуска ExplorerOne. Файлы не изменены. Закройте ExplorerOne, если он запущен, и повторите запуск установщика.
russian.ShutdownError=ExplorerOne не подтвердил завершение работы (код %1). Завершите приложение через меню в системном трее и повторите запуск установщика. Файлы приложения не заменены.
russian.ShutdownLaunchError=Не удалось запустить команду завершения работы ExplorerOne. Завершите приложение через меню в системном трее и повторите запуск установщика.
russian.StartupEnableError=ExplorerOne установлен, но автозапуск Windows включить не удалось (код %1). Повторите попытку через меню приложения в системном трее.
russian.StartupDisableError=ExplorerOne установлен, но прежнюю запись автозапуска удалить не удалось (код %1). Повторите попытку через меню приложения в системном трее.
russian.StartupRunError=Не удалось запустить ExplorerOne. Запустите приложение из меню «Пуск».
russian.UninstallCommandError=ExplorerOne не подтвердил завершение работы или очистку автозапуска (код %1). Приложение оставлено. Завершите его через меню в системном трее и повторите удаление.
russian.UninstallCommandLaunchError=Не удалось запустить команду завершения работы или очистки автозапуска ExplorerOne. Приложение оставлено.
russian.UninstallMissingExe=Файл ExplorerOne.exe отсутствует, поэтому установщик не может безопасно завершить приложение или удалить задачу автозапуска. Восстановите файлы приложения и повторите удаление.
russian.RetainedRouterNotice=Windows всё ещё использует один или несколько нативных файлов маршрутизатора ExplorerOne. Они оставлены в этой папке; удалите их после перезапуска Проводника:%n%n%1

[Tasks]
Name: "startup"; Description: "{cm:EnableStartup}"; GroupDescription: "{cm:TaskGroup}"; Flags: unchecked

[Files]
Source: "{#PublishedDirectory}*"; DestDir: "{app}"; Excludes: "ExplorerOne.NativeLaunchRouter.v1.*.dll"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishedDirectory}{#RouterDllName}"; DestDir: "{app}"; Flags: ignoreversion uninsneveruninstall onlyifdoesntexist
Source: "{code:StagedRuntimeFiles}"; DestDir: "{app}\dotnet"; Flags: external ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: IsRuntimeProvisioned

[Icons]
Name: "{group}\ExplorerOne"; Filename: "{app}\ExplorerOne.exe"; WorkingDir: "{app}"; IconFilename: "{app}\ExplorerOne.exe"
Name: "{group}\Uninstall ExplorerOne"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\ExplorerOne.exe"; Description: "{cm:LaunchProgram,ExplorerOne}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent; Check: AllowLaunch

[Code]
var
  StartupStateWasActive: Boolean;
  StartupStateCheckFailed: Boolean;
  LaunchAllowed: Boolean;
  RetainedRouterModules: TArrayOfString;
  RuntimeProvisioned: Boolean;
  TaskSelectionWasExplicit: Boolean;
  RuntimeDownloadPage: TDownloadWizardPage;

const
  RouterManifestName = 'ExplorerOne.NativeLaunchRouter.manifest';
  RouterNamePrefix = 'ExplorerOne.NativeLaunchRouter.v1.';

function SetEnvironmentVariable(lpName, lpValue: String): Boolean;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

function RunPayloadCommand(const Parameters: String; var ExitCode: Integer): Boolean;
begin
  Result := Exec(
    ExpandConstant('{tmp}\') + '{app}\ExplorerOne.exe',
    Parameters,
    ExpandConstant('{tmp}\') + '{app}',
    SW_HIDE,
    ewWaitUntilTerminated,
    ExitCode);
end;

function RunInstalledCommand(const Parameters: String; var ExitCode: Integer): Boolean;
begin
  Result := Exec(
    ExpandConstant('{app}\ExplorerOne.exe'),
    Parameters,
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ExitCode);
end;

function CopyDirectoryTree(const Source, Destination: String): Boolean;
var
  FindRec: TFindRec;
  SourcePath: String;
  DestinationPath: String;
begin
  Result := False;
  if not ForceDirectories(Destination) then
    Exit;
  if FindFirst(AddBackslash(Source) + '*', FindRec) then begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then begin
          SourcePath := AddBackslash(Source) + FindRec.Name;
          DestinationPath := AddBackslash(Destination) + FindRec.Name;
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then begin
            if not CopyDirectoryTree(SourcePath, DestinationPath) then
              Exit;
          end else if not CopyFile(SourcePath, DestinationPath, False) then
            Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  Result := True;
end;

function ProvisionRuntime: Boolean;
var
  RuntimeZip: String;
  DesktopZip: String;
  RuntimeRoot: String;
  ExitCode: Integer;
begin
  Result := False;
  RuntimeZip := ExpandConstant('{tmp}\') + 'dotnet-runtime-10.0.15-win-x64.zip';
  DesktopZip := ExpandConstant('{tmp}\') + 'windowsdesktop-runtime-10.0.15-win-x64.zip';
  RuntimeRoot := ExpandConstant('{tmp}\') + '{app}\dotnet';
  try
    RuntimeDownloadPage.Add('{#DotNetRuntimeUrl}', ExtractFileName(RuntimeZip), '{#DotNetRuntimeSha256}');
    RuntimeDownloadPage.Add('{#WindowsDesktopRuntimeUrl}', ExtractFileName(DesktopZip), '{#WindowsDesktopRuntimeSha256}');
    RuntimeDownloadPage.Show;
    RuntimeDownloadPage.Download;
    RuntimeDownloadPage.Hide;
    ForceDirectories(RuntimeRoot);
    ExtractArchive(RuntimeZip, RuntimeRoot, '', True, nil);
    ExtractArchive(DesktopZip, RuntimeRoot, '', True, nil);
  except
    RuntimeDownloadPage.Hide;
    Exit;
  end;
  if not RunPayloadCommand('--runtime-check', ExitCode) or (ExitCode <> 0) then
    Exit;
  RuntimeProvisioned := True;
  Result := True;
end;

function IsOwnedRouterName(const FileName: String): Boolean;
var
  HashText: String;
  Index: Integer;
  Character: String;
begin
  Result := False;
  if Copy(FileName, 1, Length(RouterNamePrefix)) <> RouterNamePrefix then
    Exit;
  if Length(FileName) <> Length(RouterNamePrefix) + 16 + 4 then
    Exit;
  if Copy(FileName, Length(RouterNamePrefix) + 17, 4) <> '.dll' then
    Exit;
  HashText := Copy(FileName, Length(RouterNamePrefix) + 1, 16);
  for Index := 1 to Length(HashText) do begin
    Character := Copy(HashText, Index, 1);
    if Pos(Character, '0123456789abcdef') = 0 then
      Exit;
  end;
  Result := True;
end;

function IsOwnedRetiredRouterName(const FileName: String): Boolean;
var
  BaseName: String;
  Suffix: String;
  Index: Integer;
  Character: String;
begin
  Result := False;
  Index := Pos('.retired-', FileName);
  if Index = 0 then
    Exit;
  BaseName := Copy(FileName, 1, Index - 1);
  Suffix := Copy(FileName, Index + Length('.retired-'), MaxInt);
  if not IsOwnedRouterName(BaseName) or (Suffix = '') then
    Exit;
  for Index := 1 to Length(Suffix) do begin
    Character := Copy(Suffix, Index, 1);
    if Pos(Character, '0123456789') = 0 then
      Exit;
  end;
  Result := True;
end;

function RetireRouterFile(const FilePath, FileName: String): Boolean;
var
  RetiredDirectory: String;
  Destination: String;
  Suffix: Integer;
begin
  Result := False;
  RetiredDirectory := ExpandConstant('{localappdata}\ExplorerOne\RetiredModules');
  if not ForceDirectories(RetiredDirectory) then
    Exit;
  for Suffix := 1 to 10000 do begin
    Destination := AddBackslash(RetiredDirectory) + FileName + '.retired-' + IntToStr(Suffix);
    if not FileExists(Destination) then begin
      Result := RenameFile(FilePath, Destination);
      Exit;
    end;
  end;
end;

procedure CleanupRetiredRouterFiles;
var
  FindRec: TFindRec;
  RetiredDirectory: String;
  RetiredPath: String;
begin
  RetiredDirectory := ExpandConstant('{localappdata}\ExplorerOne\RetiredModules');
  if FindFirst(AddBackslash(RetiredDirectory) + RouterNamePrefix + '*.dll.retired-*', FindRec) then begin
    try
      repeat
        if IsOwnedRetiredRouterName(FindRec.Name) then begin
          RetiredPath := AddBackslash(RetiredDirectory) + FindRec.Name;
          if FileExists(RetiredPath) then
            DeleteFile(RetiredPath);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure AddRetainedRouter(const FileName: String);
var
  Index: Integer;
begin
  for Index := 0 to GetArrayLength(RetainedRouterModules) - 1 do
    if CompareText(RetainedRouterModules[Index], FileName) = 0 then
      Exit;
  SetArrayLength(RetainedRouterModules, GetArrayLength(RetainedRouterModules) + 1);
  RetainedRouterModules[GetArrayLength(RetainedRouterModules) - 1] := FileName;
end;

procedure ReadExistingRouterManifest;
var
  Lines: TArrayOfString;
  MarkerLines: TArrayOfString;
  Index: Integer;
  CurrentMarker: String;
  MarkerPath: String;
begin
  SetArrayLength(RetainedRouterModules, 0);
  MarkerPath := ExpandConstant('{app}\' + RouterManifestName);
  if FileExists(MarkerPath) and LoadStringsFromFile(MarkerPath, Lines) then begin
    for Index := 0 to GetArrayLength(Lines) - 1 do
      if IsOwnedRouterName(Lines[Index]) then
        AddRetainedRouter(Lines[Index]);
  end else begin
    { A portable install predating this installer has no manifest. Its active
      filename is explicitly recorded by the app's current-pointer file. }
    MarkerPath := ExpandConstant('{app}\ExplorerOne.NativeLaunchRouter.current');
    if FileExists(MarkerPath) and LoadStringsFromFile(MarkerPath, MarkerLines) and
       (GetArrayLength(MarkerLines) > 0) then begin
      CurrentMarker := Trim(MarkerLines[0]);
      if IsOwnedRouterName(CurrentMarker) and FileExists(ExpandConstant('{app}\' + CurrentMarker)) then
        AddRetainedRouter(CurrentMarker);
    end;
  end;
end;

procedure CleanupCapturedRouterModules(KeepCurrent: Boolean);
var
  Index: Integer;
  FileName: String;
  FilePath: String;
  CurrentName: String;
  Remaining: TArrayOfString;
begin
  SetArrayLength(Remaining, 0);
  CurrentName := '{#RouterDllName}';
  for Index := 0 to GetArrayLength(RetainedRouterModules) - 1 do begin
    FileName := RetainedRouterModules[Index];
    FilePath := ExpandConstant('{app}\' + FileName);
    if not IsOwnedRouterName(FileName) then
      Continue;
    if KeepCurrent and (CompareText(FileName, CurrentName) = 0) then begin
      SetArrayLength(Remaining, GetArrayLength(Remaining) + 1);
      Remaining[GetArrayLength(Remaining) - 1] := FileName;
    end else if FileExists(FilePath) and not DeleteFile(FilePath) and
       not RetireRouterFile(FilePath, FileName) then begin
      { A mapped image may remain pinned inside Explorer until Explorer exits.
        Keep only filenames recorded by this installer/app in its manifest. }
      SetArrayLength(Remaining, GetArrayLength(Remaining) + 1);
      Remaining[GetArrayLength(Remaining) - 1] := FileName;
    end;
  end;
  RetainedRouterModules := Remaining;
end;

procedure PersistRouterManifest;
var
  Index: Integer;
  Text: String;
begin
  Text := '';
  for Index := 0 to GetArrayLength(RetainedRouterModules) - 1 do
    if IsOwnedRouterName(RetainedRouterModules[Index]) then
      Text := Text + RetainedRouterModules[Index] + #13#10;
  if Text = '' then
    DeleteFile(ExpandConstant('{app}\' + RouterManifestName))
  else if not SaveStringToFile(ExpandConstant('{app}\' + RouterManifestName), Text, False) then
    Log('Could not save the native-router cleanup manifest; retained router files will be left in place.');
end;

procedure SaveRouterManifest;
begin
  AddRetainedRouter('{#RouterDllName}');
  PersistRouterManifest;
end;

function HasExplicitTaskSelection: Boolean;
var
  Index: Integer;
  Argument: String;
begin
  Result := False;
  for Index := 1 to ParamCount do begin
    Argument := Lowercase(ParamStr(Index));
    if (Argument = '/tasks') or (Copy(Argument, 1, 7) = '/tasks=') then begin
      Result := True;
      Exit;
    end;
  end;
end;

procedure InitializeWizard;
var
  ExitCode: Integer;
begin
  SetEnvironmentVariable('DOTNET_DISABLE_GUI_ERRORS', '1');
  StartupStateWasActive := False;
  StartupStateCheckFailed := True;
  LaunchAllowed := True;
  RuntimeProvisioned := False;
  TaskSelectionWasExplicit := HasExplicitTaskSelection;
  RuntimeDownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgWizardPreparing), nil);
  try
    ExtractTemporaryFiles('{app}\*');
  except
    StartupStateCheckFailed := True;
    Exit;
  end;
  if DirExists(AddBackslash(WizardDirValue) + 'dotnet') then
    RuntimeProvisioned := CopyDirectoryTree(AddBackslash(WizardDirValue) + 'dotnet', ExpandConstant('{tmp}\') + '{app}\dotnet');
  if not RunPayloadCommand('--runtime-check', ExitCode) or (ExitCode <> 0) then begin
    if not ProvisionRuntime then begin
      SuppressibleMsgBox(CustomMessage('RuntimeDownloadError'), mbError, MB_OK, 0);
      StartupStateCheckFailed := True;
      Exit;
    end;
  end;
  if RunPayloadCommand('--startup-status', ExitCode) then begin
    if ExitCode = 0 then begin
      StartupStateWasActive := True;
      StartupStateCheckFailed := False;
    end else if ExitCode = 1 then begin
      StartupStateWasActive := False;
      StartupStateCheckFailed := False;
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpSelectTasks) and not TaskSelectionWasExplicit then
    WizardForm.TasksList.Checked[0] := StartupStateWasActive;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if StartupStateCheckFailed or not RunPayloadCommand('--startup-status', ExitCode) or
     ((ExitCode <> 0) and (ExitCode <> 1)) then begin
    Result := CustomMessage('StartupStatusError');
    Exit;
  end;

  if not RunPayloadCommand('--shutdown', ExitCode) then begin
    Result := CustomMessage('ShutdownLaunchError');
    Exit;
  end;
  if ExitCode <> 0 then begin
    Result := FmtMessage(CustomMessage('ShutdownError'), [IntToStr(ExitCode)]);
    Exit;
  end;

  { Capture before [Files] replaces the current-pointer file. Do cleanup only
    after all application files have been installed successfully. }
  ReadExistingRouterManifest;
  CleanupRetiredRouterFiles;

end;

function AllowLaunch: Boolean;
begin
  Result := LaunchAllowed;
end;

function IsRuntimeProvisioned: Boolean;
begin
  Result := RuntimeProvisioned;
end;

function StagedRuntimeFiles(Param: String): String;
begin
  Result := ExpandConstant('{tmp}\') + '{app}\dotnet\*';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
  Command: String;
  Started: Boolean;
begin
  if CurStep = ssPostInstall then begin
    if WizardIsTaskSelected('startup') then
      Command := '--enable-startup'
    else
      Command := '--disable-startup';
    Started := RunInstalledCommand(Command, ExitCode);
    if not Started or (ExitCode <> 0) then begin
      LaunchAllowed := False;
      if Started then begin
        if Command = '--enable-startup' then
          Log(Format('Startup enable failed with exit code %d.', [ExitCode]))
        else
          Log(Format('Startup disable failed with exit code %d.', [ExitCode]));
      end else
        Log('Could not start the startup configuration command.');
      if WizardIsTaskSelected('startup') then
        SuppressibleMsgBox(FmtMessage(CustomMessage('StartupEnableError'), [IntToStr(ExitCode)]), mbError, MB_OK, 0)
      else
        SuppressibleMsgBox(FmtMessage(CustomMessage('StartupDisableError'), [IntToStr(ExitCode)]), mbError, MB_OK, 0);
    end;
    CleanupCapturedRouterModules(True);
    SaveRouterManifest;
  end;
end;

function InitializeUninstall: Boolean;
var
  ExitCode: Integer;
begin
  Result := False;
  if not FileExists(ExpandConstant('{app}\ExplorerOne.exe')) then begin
    SuppressibleMsgBox(CustomMessage('UninstallMissingExe'), mbError, MB_OK, 0);
    Exit;
  end;
  if not RunInstalledCommand('--shutdown', ExitCode) then begin
    SuppressibleMsgBox(CustomMessage('UninstallCommandLaunchError'), mbError, MB_OK, 0);
    Exit;
  end;
  if ExitCode <> 0 then begin
    SuppressibleMsgBox(FmtMessage(CustomMessage('UninstallCommandError'), [IntToStr(ExitCode)]), mbError, MB_OK, 0);
    Exit;
  end;
  if not RunInstalledCommand('--disable-startup', ExitCode) then begin
    SuppressibleMsgBox(CustomMessage('UninstallCommandLaunchError'), mbError, MB_OK, 0);
    Exit;
  end;
  if ExitCode <> 0 then begin
    SuppressibleMsgBox(FmtMessage(CustomMessage('UninstallCommandError'), [IntToStr(ExitCode)]), mbError, MB_OK, 0);
    Exit;
  end;

  ReadExistingRouterManifest;
  CleanupRetiredRouterFiles;
  CleanupCapturedRouterModules(False);
  PersistRouterManifest;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Index: Integer;
  MessageText: String;
begin
  if CurUninstallStep = usPostUninstall then begin
    if FileExists(ExpandConstant('{app}\' + RouterManifestName)) and
       LoadStringsFromFile(ExpandConstant('{app}\' + RouterManifestName), RetainedRouterModules) and
       (GetArrayLength(RetainedRouterModules) > 0) then begin
      MessageText := '';
      for Index := 0 to GetArrayLength(RetainedRouterModules) - 1 do
        if IsOwnedRouterName(RetainedRouterModules[Index]) then
          MessageText := MessageText + ExpandConstant('{app}\' + RetainedRouterModules[Index]) + #13#10;
      if MessageText <> '' then
        SuppressibleMsgBox(FmtMessage(CustomMessage('RetainedRouterNotice'), [Trim(MessageText)]), mbInformation, MB_OK, 0);
    end else
      DeleteFile(ExpandConstant('{app}\' + RouterManifestName));
  end;
end;
