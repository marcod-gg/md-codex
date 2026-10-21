; Instalador de MD Codex (Inno Setup 6). Se compila con instalador\construir.ps1, que antes publica la app.
; Archivo en UTF-8 con BOM: Inno Setup lo necesita para leer bien los acentos.

#ifndef Version
  #define Version "1.0.0"
#endif
#define Nombre "MD Codex"
#define Ejecutable "MdCodex.exe"
#define ProgId "MdCodex.Markdown"

[Setup]
; No cambiar AppId: identifica la app para actualizar y desinstalar.
AppId={{C69052E4-371C-4C8A-AA69-BC88B9DC7DDC}
AppName={#Nombre}
AppVersion={#Version}
AppVerName={#Nombre} {#Version}
AppPublisher=Marco González G.
AppPublisherURL=https://github.com/marcod-gg/md-codex
AppSupportURL=https://github.com/marcod-gg/md-codex/issues
AppCopyright=© 2026 Marco González G.
; Por usuario y sin pedir administrador; el asistente permite instalar para todos.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#Nombre}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#Ejecutable}
UninstallDisplayName={#Nombre}
SetupIconFile=..\src\MdCodex\Recursos\mdcodex.ico
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 2004 o posterior (lo exige el control WebView2 de composición).
MinVersion=10.0.19041
ChangesAssociations=yes
CloseApplications=yes
; La app se reabre desde [Code] (con /REABRIR=1), no por el reinicio de aplicaciones de Windows.
RestartApplications=no
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\publicar
OutputBaseFilename=MDCodex-{#Version}-instalador

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "asociar"; Description: "Abrir los archivos .md y .markdown con {#Nombre}"
Name: "escritorio"; Description: "Crear un acceso directo en el escritorio"; Flags: unchecked

[Files]
Source: "..\publicar\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#Nombre}"; Filename: "{app}\{#Ejecutable}"
Name: "{autodesktop}\{#Nombre}"; Filename: "{app}\{#Ejecutable}"; Tasks: escritorio

[Registry]
; HKA = HKCU al instalar por usuario, HKLM al instalar para todos.
; Tipo de archivo propio y comando para abrirlo.
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueData: "Documento Markdown"; Flags: uninsdeletekey; Tasks: asociar
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueData: "{app}\{#Ejecutable},0"; Tasks: asociar
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueData: """{app}\{#Ejecutable}"" ""%1"""; Tasks: asociar
; MD Codex aparece en "Abrir con…" para .md y .markdown. Windows no permite que una app se ponga
; sola como predeterminada: eso lo elige el usuario (al final se ofrece abrir la configuración).
Root: HKA; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: asociar
Root: HKA; Subkey: "Software\Classes\.markdown\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: asociar
Root: HKA; Subkey: "Software\Classes\Applications\{#Ejecutable}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#Nombre}"; Flags: uninsdeletekey; Tasks: asociar
Root: HKA; Subkey: "Software\Classes\Applications\{#Ejecutable}\SupportedTypes"; ValueType: string; ValueName: ".md"; ValueData: ""; Tasks: asociar
Root: HKA; Subkey: "Software\Classes\Applications\{#Ejecutable}\SupportedTypes"; ValueType: string; ValueName: ".markdown"; ValueData: ""; Tasks: asociar

[Run]
Filename: "{app}\{#Ejecutable}"; Description: "Abrir {#Nombre}"; Flags: nowait postinstall skipifsilent
Filename: "ms-settings:defaultapps"; Description: "Elegir {#Nombre} como app predeterminada para .md"; Flags: shellexec nowait postinstall skipifsilent unchecked; Tasks: asociar

[Code]
// MD Codex usa WebView2 para la vista previa. Viene con Windows 11 y con Edge en Windows 10,
// pero si falta, se avisa antes de instalar.
function WebView2Instalado(): Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);
  Result := Result and (Version <> '') and (Version <> '0.0.0.0');
end;

// Actualización desde la app: se ejecuta con /SILENT, que solo muestra la página de progreso.
// Esa ventana pasa a decir "Actualizando MD Codex", sin botón Cancelar.
function Reabrir(): Boolean;
begin
  Result := ExpandConstant('{param:REABRIR|0}') = '1';
end;

procedure InitializeWizard();
begin
  if WizardSilent() then
  begin
    WizardForm.Caption := 'Actualizando {#Nombre}';
    WizardForm.CancelButton.Visible := False;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if WizardSilent() and (CurPageID = wpInstalling) then
  begin
    WizardForm.PageNameLabel.Caption := 'Actualizando {#Nombre}';
    WizardForm.PageDescriptionLabel.Caption := 'Espera un momento mientras se instala la versión {#Version}.';
  end;
end;

// Con /REABRIR=1 (botón "Reiniciar ahora"), al terminar avisa que abre la app, la abre y deja
// el aviso un momento a la vista antes de cerrarse.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Codigo: Integer;
begin
  if (CurStep = ssPostInstall) and Reabrir() then
  begin
    WizardForm.StatusLabel.Caption := 'Abriendo {#Nombre}…';
    WizardForm.FilenameLabel.Caption := '';
    WizardForm.Refresh();
    ExecAsOriginalUser(ExpandConstant('{app}\{#Ejecutable}'), '', '', SW_SHOWNORMAL, ewNoWait, Codigo);
    Sleep(1500);
  end;
end;

function InitializeSetup(): Boolean;
var
  Codigo: Integer;
begin
  Result := True;
  if not WebView2Instalado() and not WizardSilent() then
    if MsgBox('MD Codex necesita el runtime de Microsoft Edge WebView2, que no está instalado.' + #13#10#13#10 +
              '¿Quieres abrir la página de descarga? La instalación seguirá de todos modos.',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/', '', '', SW_SHOWNORMAL, ewNoWait, Codigo);
end;
