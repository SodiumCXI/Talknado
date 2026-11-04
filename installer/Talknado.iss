; Установщик Talknado для Inno Setup 7.
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
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "zh"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
en.StartMenuIcon=Add to the &Start menu and search
ru.StartMenuIcon=Добавить в меню &Пуск и поиск
zh.StartMenuIcon=添加到“开始”菜单和搜索(&S)

[Files]
Source: "{#SourceDir}\Talknado.Client.exe"; DestDir: "{app}"
Source: "{#SourceDir}\*.*"; DestDir: "{app}"; Excludes: "Talknado.Client.exe"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startmenuicon"; Description: "{cm:StartMenuIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Icons]
Name: "{commondesktop}\Talknado"; Filename: "{app}\Talknado.Client.exe"; Tasks: desktopicon
Name: "{autoprograms}\Talknado"; Filename: "{app}\Talknado.Client.exe"; Tasks: startmenuicon

[Run]
; Без skipifsilent: тихое обновление из программы тоже запускает её после установки.
Filename: "{app}\Talknado.Client.exe"; Description: "{cm:LaunchProgram,Talknado}"; Flags: nowait postinstall
