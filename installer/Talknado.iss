; Установщик Talknado для Inno Setup 6.
; Собирается из папки публикации клиента (профиль win-x64-portable):
;   ISCC.exe /DAppVersion=2.0.0 installer\Talknado.iss
; Пути — от папки этого файла.

#ifndef AppVersion
  #error Pass the version: ISCC /DAppVersion=X.Y.Z installer\Talknado.iss
#endif
#ifndef SourceDir
  #define SourceDir "..\Talknado.Client\bin\publish\Talknado"
#endif

[Setup]
; AppId совпадает с прежним значением по умолчанию (AppName): иначе Windows
; не узнала бы уже установленную программу и поставила вторую копию.
AppId=Talknado
AppName=Talknado
AppVersion={#AppVersion}
AppPublisher=SodiumCXI
DefaultDirName={autopf}\Talknado
OutputDir=Output
OutputBaseFilename=Talknado_v{#AppVersion}_x64_setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
DisableProgramGroupPage=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
SetupIconFile=..\Talknado.Client\Resources\talknado_1.ico
UninstallDisplayIcon={app}\Talknado.Client.exe

[Files]
Source: "{#SourceDir}\Talknado.Client.exe"; DestDir: "{app}"
Source: "{#SourceDir}\*.*"; DestDir: "{app}"; Excludes: "Talknado.Client.exe"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"

[Icons]
; Только ярлык на рабочем столе (без меню Пуск)
Name: "{commondesktop}\Talknado"; Filename: "{app}\Talknado.Client.exe"; Tasks: desktopicon
