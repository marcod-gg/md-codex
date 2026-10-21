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
; Por usuario y sin pedir administrador. Para instalar para todos: /ALLUSERS en la línea de comandos.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
DefaultDirName={autopf}\{#Nombre}
; Pantallas propias (ver [Code]): Instalar → progreso → opciones y "Abrir MD Codex".
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#Ejecutable}
UninstallDisplayName={#Nombre}
; Ícono del lobo (skolldev) para el instalador y sus ventanas.
SetupIconFile=..\marca\skolldev\skolldev-acento.ico
; Sigue el tema claro u oscuro de Windows, con el fondo del área de trabajo de la app (Tema.cs).
; Las ventanas se rearman en [Code]: sin barra de título, compactas y con la ✕ dentro.
WizardStyle=modern dynamic windows11 hidebevels
WizardBackColor=#FFFFFF
WizardBackColorDynamicDark=#1F242B
WizardImageFile=
WizardSmallImageFile=
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 2004 o posterior (lo exige el control WebView2 de composición).
MinVersion=10.0.19041
ChangesAssociations=yes
CloseApplications=yes
; Para saber si MD Codex está abierto basta revisar los .exe; revisar los ~600 archivos tardaba varios segundos.
CloseApplicationsFilter=*.exe
; La app se reabre desde [Code], no por el reinicio de aplicaciones de Windows.
RestartApplications=no
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\publicar
OutputBaseFilename=MDCodex-{#Version}-instalador

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Messages]
ConfirmUninstall=¿Quieres desinstalar %1? Tus documentos no se tocan.
UninstalledAll=%1 se desinstaló.

[Files]
Source: "..\publicar\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Ícono de las ventanas del instalador y del desinstalador.
Source: "..\marca\mdcodex\mdcodex-128.png"; Flags: dontcopy
Source: "..\marca\mdcodex\mdcodex-128.png"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\marca\skolldev\skolldev-acento.ico"; Flags: dontcopy
Source: "..\marca\skolldev\skolldev-acento.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#Nombre}"; Filename: "{app}\{#Ejecutable}"

[UninstallDelete]
; El acceso directo del escritorio es opcional y se crea desde [Code].
Type: files; Name: "{autodesktop}\{#Nombre}.lnk"

[Code]
const
  WM_SYSCOMMAND = $0112;
  SC_DRAGMOVE = $F012;
  VK_LBUTTON = 1;
  DWMWA_NCRENDERING_POLICY = 2;
  DWMNCRP_ENABLED = 2;
  DWMWA_WINDOW_CORNER_PREFERENCE = 33;
  DWMWCP_ROUND = 2;
  SHCNE_ASSOCCHANGED = $08000000;
  WM_SETICON = $0080;
  GW_OWNER = 4;
  SM_CXICON = 11;
  SM_CXSMICON = 49;
  IMAGE_ICON = 1;
  LR_LOADFROMFILE = $10;
  ClaveDesinstalar = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{C69052E4-371C-4C8A-AA69-BC88B9DC7DDC}_is1';
  ClaveProgId = 'Software\Classes\{#ProgId}';
  ClaveAplicacion = 'Software\Classes\Applications\{#Ejecutable}';

type
  TMargenes = record
    Izquierdo, Derecho, Arriba, Abajo: Integer;
  end;
  TPunto = record
    X, Y: Longint;
  end;

function DwmSetWindowAttribute(Ventana: HWND; Atributo: DWORD; var Valor: Integer; Tamano: DWORD): Integer;
  external 'DwmSetWindowAttribute@dwmapi.dll stdcall';
function DwmExtendFrameIntoClientArea(Ventana: HWND; var Margenes: TMargenes): Integer;
  external 'DwmExtendFrameIntoClientArea@dwmapi.dll stdcall';
function GetAsyncKeyState(Tecla: Integer): SmallInt;
  external 'GetAsyncKeyState@user32.dll stdcall';
function GetCursorPos(var Punto: TPunto): BOOL;
  external 'GetCursorPos@user32.dll stdcall';
function WindowFromPoint(X, Y: Longint): HWND;
  external 'WindowFromPoint@user32.dll stdcall';
function ReleaseCapture(): BOOL;
  external 'ReleaseCapture@user32.dll stdcall';
function SetTimer(Ventana: HWND; Id, Intervalo, Funcion: LongWord): LongWord;
  external 'SetTimer@user32.dll stdcall';
procedure SHChangeNotify(Evento: Integer; Banderas: Cardinal; Item1, Item2: Integer);
  external 'SHChangeNotify@shell32.dll stdcall';
function LoadImage(Instancia: LongWord; Nombre: String; Tipo: LongWord; Ancho, Alto: Integer; Banderas: LongWord): LongWord;
  external 'LoadImageW@user32.dll stdcall';
function GetSystemMetrics(Indice: Integer): Integer;
  external 'GetSystemMetrics@user32.dll stdcall';
function GetWindow(Hwnd: HWND; Relacion: LongWord): HWND;
  external 'GetWindow@user32.dll stdcall';

var
  // Ventana rearmada (la del instalador o la del desinstalador) y sus controles propios.
  Ventana: TSetupForm;
  Titulo, Subtitulo, Aviso: TLabel;
  Cerrar: TNewStaticText;
  OpcionAsociar, OpcionEscritorio: TNewCheckBox;
  BotonAntes, AbrirAlTerminar: Boolean;
  // Ícono del lobo (.ico) para la ventana y su botón en la barra de tareas.
  ArchivoIcono: String;
  // Versión ya instalada; vacía si es una instalación nueva.
  VersionAnterior: String;

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

// Actualización desde la app: /SILENT muestra solo el progreso; con /REABRIR=1 ("Reiniciar ahora")
// vuelve a abrir la app al terminar.
function Reabrir(): Boolean;
begin
  Result := ExpandConstant('{param:REABRIR|0}') = '1';
end;

function Actualizando(): Boolean;
begin
  Result := VersionAnterior <> '';
end;

// ---------- Opciones del final: abrir .md con la app y acceso directo en el escritorio ----------

// MD Codex aparece en "Abrir con…" para .md y .markdown. Windows no permite que una app se ponga
// sola como predeterminada: eso lo elige el usuario. HKA = HKCU por usuario, HKLM para todos.
procedure Asociar();
var
  Exe: String;
begin
  Exe := ExpandConstant('{app}\{#Ejecutable}');
  RegWriteStringValue(HKA, ClaveProgId, '', 'Documento Markdown');
  RegWriteStringValue(HKA, ClaveProgId + '\DefaultIcon', '', Exe + ',0');
  RegWriteStringValue(HKA, ClaveProgId + '\shell\open\command', '', '"' + Exe + '" "%1"');
  RegWriteStringValue(HKA, 'Software\Classes\.md\OpenWithProgids', '{#ProgId}', '');
  RegWriteStringValue(HKA, 'Software\Classes\.markdown\OpenWithProgids', '{#ProgId}', '');
  RegWriteStringValue(HKA, ClaveAplicacion, 'FriendlyAppName', '{#Nombre}');
  RegWriteStringValue(HKA, ClaveAplicacion + '\SupportedTypes', '.md', '');
  RegWriteStringValue(HKA, ClaveAplicacion + '\SupportedTypes', '.markdown', '');
end;

procedure Desasociar();
begin
  RegDeleteKeyIncludingSubkeys(HKA, ClaveProgId);
  RegDeleteValue(HKA, 'Software\Classes\.md\OpenWithProgids', '{#ProgId}');
  RegDeleteValue(HKA, 'Software\Classes\.markdown\OpenWithProgids', '{#ProgId}');
  RegDeleteKeyIncludingSubkeys(HKA, ClaveAplicacion);
end;

procedure AplicarOpciones();
var
  Acceso: String;
begin
  if OpcionAsociar.Checked then
    Asociar()
  else
    Desasociar();
  SHChangeNotify(SHCNE_ASSOCCHANGED, 0, 0, 0);

  Acceso := ExpandConstant('{autodesktop}\{#Nombre}.lnk');
  if OpcionEscritorio.Checked then
    CreateShellLink(Acceso, '{#Nombre}', ExpandConstant('{app}\{#Ejecutable}'), '', ExpandConstant('{app}'), '', 0, SW_SHOWNORMAL)
  else
    DeleteFile(Acceso);
end;

// ---------- Ventana compacta, sin marco ----------

// Sin marco, la ventana queda sin ícono y la barra de tareas muestra el genérico: se le pone el lobo,
// a ella y a su ventana dueña (la que tiene el botón en la barra de tareas).
procedure PonerIcono(Hwnd: HWND);
begin
  if (Hwnd = 0) or not FileExists(ArchivoIcono) then
    Exit;
  SendMessage(Hwnd, WM_SETICON, 1, LoadImage(0, ArchivoIcono, IMAGE_ICON,
    GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CXICON), LR_LOADFROMFILE));
  SendMessage(Hwnd, WM_SETICON, 0, LoadImage(0, ArchivoIcono, IMAGE_ICON,
    GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CXSMICON), LR_LOADFROMFILE));
end;

// Windows 11: esquinas redondeadas, borde fino y sombra, como las demás ventanas. Se vuelve a aplicar
// en cada página porque Inno recrea la ventana (y su identificador) al pasar de una a otra.
procedure AplicarMarco();
var
  Valor: Integer;
  Margenes: TMargenes;
begin
  Valor := DWMWCP_ROUND;
  DwmSetWindowAttribute(Ventana.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, Valor, SizeOf(Valor));
  Valor := DWMNCRP_ENABLED;
  DwmSetWindowAttribute(Ventana.Handle, DWMWA_NCRENDERING_POLICY, Valor, SizeOf(Valor));
  Margenes.Izquierdo := 0;
  Margenes.Derecho := 0;
  Margenes.Arriba := 1;
  Margenes.Abajo := 0;
  DwmExtendFrameIntoClientArea(Ventana.Handle, Margenes);
  PonerIcono(Ventana.Handle);
  PonerIcono(GetWindow(Ventana.Handle, GW_OWNER));
end;

// Sin barra de título, la ventana se arrastra desde su fondo: Inno no expone eventos del mouse,
// así que un temporizador detecta el clic sobre el fondo y le pide a Windows moverla.
procedure Arrastrar(Hwnd: HWND; Mensaje, Id, Tiempo: LongWord);
var
  Presionado: Boolean;
  Punto: TPunto;
begin
  Presionado := GetAsyncKeyState(VK_LBUTTON) < 0;
  if Presionado and not BotonAntes then
  begin
    GetCursorPos(Punto);
    if WindowFromPoint(Punto.X, Punto.Y) = Ventana.Handle then
    begin
      ReleaseCapture();
      PostMessage(Ventana.Handle, WM_SYSCOMMAND, SC_DRAGMOVE, 0);
    end;
  end;
  BotonAntes := Presionado;
end;

procedure CerrarClick(Sender: TObject);
begin
  Ventana.Close();
end;

function NuevaEtiqueta(Texto: String; Izquierda, Arriba, Tamano: Integer; Tenue: Boolean): TLabel;
begin
  Result := TLabel.Create(Ventana);
  Result.Parent := Ventana;
  Result.Caption := Texto;
  Result.Left := Izquierda;
  Result.Top := Arriba;
  Result.Font.Size := Tamano;
  if Tenue then
  begin
    Result.StyleElements := Result.StyleElements - [seFont];
    if IsDarkInstallMode() then
      Result.Font.Color := StrToColor('#9198A1')
    else
      Result.Font.Color := StrToColor('#59636E');
  end;
end;

// Encabezado común: ícono, título y versión arriba, y la ✕ en la esquina.
procedure ArmarVentana(F: TSetupForm; Png: String);
var
  Icono: TBitmapImage;
  Margen: Integer;
begin
  Ventana := F;
  Margen := ScaleX(24);
  F.BorderStyle := bsNone;
  F.ClientWidth := ScaleX(440);

  Icono := TBitmapImage.Create(F);
  Icono.Parent := F;
  Icono.SetBounds(Margen, Margen, ScaleX(40), ScaleY(40));
  Icono.Stretch := True;
  Icono.BackColor := clNone;
  if FileExists(Png) then
    Icono.PngImage.LoadFromFile(Png);

  Titulo := NuevaEtiqueta('{#Nombre}', Margen + ScaleX(52), Margen - ScaleY(2), 13, False);
  Titulo.Font.Style := [fsBold];
  Subtitulo := NuevaEtiqueta('Versión {#Version}', Titulo.Left, Titulo.Top + Titulo.Height + ScaleY(2), 9, True);

  Cerrar := TNewStaticText.Create(F);
  Cerrar.Parent := F;
  Cerrar.Caption := #$E8BB;
  Cerrar.Font.Name := 'Segoe MDL2 Assets';
  Cerrar.Font.Size := 8;
  Cerrar.Cursor := crHand;
  Cerrar.Hint := 'Cerrar';
  Cerrar.ShowHint := True;
  Cerrar.Left := F.ClientWidth - Cerrar.Width - ScaleX(16);
  Cerrar.Top := ScaleY(16);
  Cerrar.OnClick := @CerrarClick;

  SetTimer(0, 0, 30, CreateCallback(@Arrastrar));
end;

function NuevaOpcion(Texto: String; Arriba: Integer; Marcada: Boolean): TNewCheckBox;
begin
  Result := TNewCheckBox.Create(WizardForm);
  Result.Parent := WizardForm;
  Result.Caption := Texto;
  Result.Checked := Marcada;
  Result.SetBounds(ScaleX(24), Arriba, WizardForm.ClientWidth - ScaleX(48), ScaleY(22));
  Result.Visible := False;
end;

// ---------- Instalador ----------

function InitializeSetup(): Boolean;
var
  Codigo: Integer;
begin
  Result := True;
  if not RegQueryStringValue(HKCU, ClaveDesinstalar, 'DisplayVersion', VersionAnterior) then
    RegQueryStringValue(HKLM, ClaveDesinstalar, 'DisplayVersion', VersionAnterior);
  if not WebView2Instalado() and not WizardSilent() then
    if MsgBox('MD Codex necesita el runtime de Microsoft Edge WebView2, que no está instalado.' + #13#10#13#10 +
              '¿Quieres abrir la página de descarga? La instalación seguirá de todos modos.',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/', '', '', SW_SHOWNORMAL, ewNoWait, Codigo);
end;

// Se reutilizan la barra y el estado del asistente, sacados de sus páginas, para que Inno los siga
// manejando; las páginas originales quedan ocultas.
procedure InitializeWizard();
var
  F: TWizardForm;
  Margen: Integer;
begin
  F := WizardForm;
  Margen := ScaleX(24);
  ExtractTemporaryFile('mdcodex-128.png');
  ExtractTemporaryFile('skolldev-acento.ico');
  ArchivoIcono := ExpandConstant('{tmp}\skolldev-acento.ico');
  ArmarVentana(F, ExpandConstant('{tmp}\mdcodex-128.png'));
  F.OuterNotebook.Visible := False;
  F.Bevel.Visible := False;
  F.BeveledLabel.Visible := False;
  F.FilenameLabel.Visible := False;
  if Actualizando() and (VersionAnterior <> '{#Version}') then
    Subtitulo.Caption := 'Versión ' + VersionAnterior + ' → {#Version}';
  if WizardSilent() then
  begin
    F.Caption := 'Actualizando {#Nombre}';
    Titulo.Caption := 'Actualizando {#Nombre}';
  end;

  // "Preparando para instalar": aparece si MD Codex está abierto (hay que cerrarlo) o si algo impide instalar.
  Aviso := NuevaEtiqueta('', Margen, ScaleY(88), 9, False);
  Aviso.AutoSize := False;
  Aviso.WordWrap := True;
  Aviso.Width := F.ClientWidth - 2 * Margen;
  Aviso.Height := ScaleY(48);
  Aviso.Visible := False;

  F.StatusLabel.Parent := F;
  F.StatusLabel.SetBounds(Margen, ScaleY(88), F.ClientWidth - 2 * Margen, ScaleY(20));
  F.ProgressGauge.Parent := F;
  F.ProgressGauge.SetBounds(Margen, ScaleY(116), F.ClientWidth - 2 * Margen, ScaleY(6));

  // En una actualización, las opciones del final parten como estaban.
  OpcionAsociar := NuevaOpcion('Abrir los archivos .md con {#Nombre}', ScaleY(88),
    not Actualizando() or RegKeyExists(HKA, ClaveProgId));
  OpcionEscritorio := NuevaOpcion('Crear un acceso directo en el escritorio', ScaleY(116),
    FileExists(ExpandConstant('{autodesktop}\{#Nombre}.lnk')));

  F.ClientHeight := ScaleY(150);
  F.FlipAndCenterIfNeeded(False, nil, False);
  AplicarMarco();
end;

// Cada página muestra solo lo suyo y la ventana toma el alto justo.
procedure CurPageChanged(CurPageID: Integer);
var
  F: TWizardForm;
  Propia: Boolean;
  Alto: Integer;
begin
  F := WizardForm;
  Propia := (CurPageID = wpReady) or (CurPageID = wpPreparing) or (CurPageID = wpInstalling) or (CurPageID = wpFinished);
  // Páginas que esta ventana no rearma (no deberían aparecer): se muestran tal cual.
  F.OuterNotebook.Visible := not Propia;
  F.BackButton.Visible := False;
  F.CancelButton.Visible := not Propia;
  F.NextButton.Visible := CurPageID <> wpInstalling;
  Aviso.Visible := CurPageID = wpPreparing;
  F.StatusLabel.Visible := CurPageID = wpInstalling;
  F.ProgressGauge.Visible := CurPageID = wpInstalling;
  OpcionAsociar.Visible := CurPageID = wpFinished;
  OpcionEscritorio.Visible := CurPageID = wpFinished;
  Cerrar.Visible := CurPageID <> wpInstalling;
  Alto := ScaleY(200);

  case CurPageID of
    wpReady:
      begin
        if VersionAnterior = '{#Version}' then
          F.NextButton.Caption := 'Reinstalar'
        else if Actualizando() then
          F.NextButton.Caption := 'Actualizar'
        else
          F.NextButton.Caption := 'Instalar';
        Alto := ScaleY(150);
      end;
    wpPreparing:
      // Con apps abiertas, Inno marca por defecto "cerrarlas automáticamente"; si es otro problema, se muestra su texto.
      if F.PreparingYesRadio.Visible then
      begin
        Aviso.Caption := 'MD Codex está abierto y se cerrará para continuar. Guarda tus cambios antes.';
        F.NextButton.Caption := 'Continuar';
      end
      else
        Aviso.Caption := F.PreparingLabel.Caption;
    wpInstalling:
      begin
        if Actualizando() then
          Titulo.Caption := 'Actualizando {#Nombre}'
        else
          Titulo.Caption := 'Instalando {#Nombre}';
        Alto := F.ProgressGauge.Top + F.ProgressGauge.Height + ScaleY(32);
      end;
    wpFinished:
      begin
        Titulo.Caption := '{#Nombre} está listo';
        Subtitulo.Caption := 'Versión {#Version}';
        F.NextButton.Caption := 'Abrir {#Nombre}';
      end;
  end;

  F.ClientHeight := Alto;
  F.NextButton.Width := F.CalculateButtonWidth([F.NextButton.Caption]);
  F.NextButton.Left := F.ClientWidth - F.NextButton.Width - ScaleX(24);
  F.NextButton.Top := F.ClientHeight - F.NextButton.Height - ScaleY(24);
  AplicarMarco();
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpFinished then
    AbrirAlTerminar := True;
end;

// Antes de instalar no hay nada que deshacer: la ✕ cierra sin preguntar.
procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if (CurPageID = wpReady) or (CurPageID = wpPreparing) then
    Confirm := False;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Codigo: Integer;
begin
  // Actualización desde la app con "Reiniciar ahora": avisa, abre la app y deja el aviso un momento.
  if (CurStep = ssPostInstall) and WizardSilent() and Reabrir() then
  begin
    WizardForm.StatusLabel.Caption := 'Abriendo {#Nombre}…';
    WizardForm.ProgressGauge.Position := WizardForm.ProgressGauge.Max;
    WizardForm.Refresh();
    ExecAsOriginalUser(ExpandConstant('{app}\{#Ejecutable}'), '', '', SW_SHOWNORMAL, ewNoWait, Codigo);
    Sleep(1200);
  end;
  // Instalación con ventanas: se aplican las opciones elegidas al final (las actualizaciones
  // silenciosas las dejan como estaban) y, con "Abrir MD Codex", se abre la app.
  if (CurStep = ssDone) and not WizardSilent() then
  begin
    AplicarOpciones();
    if AbrirAlTerminar then
      ExecAsOriginalUser(ExpandConstant('{app}\{#Ejecutable}'), '', '', SW_SHOWNORMAL, ewNoWait, Codigo);
  end;
end;

// ---------- Desinstalador ----------

procedure InitializeUninstallProgressForm();
var
  F: TUninstallProgressForm;
  Margen: Integer;
begin
  F := UninstallProgressForm;
  Margen := ScaleX(24);
  ArchivoIcono := ExpandConstant('{app}\skolldev-acento.ico');
  ArmarVentana(F, ExpandConstant('{app}\mdcodex-128.png'));
  F.OuterNotebook.Visible := False;
  F.Bevel.Visible := False;
  F.BeveledLabel.Visible := False;
  F.CancelButton.Visible := False;
  Cerrar.Visible := False;
  Titulo.Caption := 'Desinstalando {#Nombre}';
  F.StatusLabel.Parent := F;
  F.StatusLabel.SetBounds(Margen, ScaleY(88), F.ClientWidth - 2 * Margen, ScaleY(20));
  F.ProgressBar.Parent := F;
  F.ProgressBar.SetBounds(Margen, ScaleY(116), F.ClientWidth - 2 * Margen, ScaleY(6));
  F.ClientHeight := F.ProgressBar.Top + F.ProgressBar.Height + ScaleY(32);
  F.FlipAndCenterIfNeeded(False, nil, False);
  AplicarMarco();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usUninstall) and not UninstallSilent() then
    AplicarMarco();
  if CurUninstallStep = usPostUninstall then
  begin
    Desasociar();
    SHChangeNotify(SHCNE_ASSOCCHANGED, 0, 0, 0);
  end;
end;
