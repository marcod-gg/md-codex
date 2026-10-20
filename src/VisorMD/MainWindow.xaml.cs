using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace VisorMD;

public partial class MainWindow : Window
{
    private enum Modo { Vista, Dividido, Editor }

    private static readonly string[] Extensiones = [".md", ".markdown", ".mdown", ".mkd", ".txt"];

    private string? _archivoActual;
    // Host virtual que apunta a la carpeta del documento, para que funcionen imágenes y enlaces relativos.
    private string? _hostActual;
    private Encoding _codificacion = new UTF8Encoding(false);
    private Modo _modo = Modo.Vista;
    private bool _vistaLista;
    // Marcar un botón de modo vuelve a llamar a AplicarModo; esto corta la recursión.
    private bool _aplicandoModo;
    private FileSystemWatcher? _vigilante;
    // Versión en disco que el usuario ya decidió no recargar, para no volver a preguntar por lo mismo.
    private string? _cambioRechazado;
    private readonly DispatcherTimer _demoraVista;
    private readonly DispatcherTimer _demoraDisco;

    public MainWindow(string? archivoInicial)
    {
        InitializeComponent();

        _demoraVista = Temporizador(150, ActualizarVista);
        // Los editores suelen disparar varios eventos al guardar; se agrupan en una sola revisión.
        _demoraDisco = Temporizador(200, AlCambiarEnDisco);

        Editor.TextChanged += (_, _) => Reiniciar(_demoraVista);
        Editor.Document.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
                ActualizarTitulo();
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) => ActualizarPosicion();
        Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SincronizarScroll();
        Editor.PreviewDragEnter += Editor_PreviewDragOver;
        Editor.PreviewDragOver += Editor_PreviewDragOver;
        Editor.PreviewDrop += Editor_PreviewDrop;
        Editor.Options.HighlightCurrentLine = true;

        AplicarTema();
        SystemEvents.UserPreferenceChanged += AlCambiarPreferencias;

        AplicarModo(Modo.Vista);
        ActualizarTitulo();
        ActualizarPosicion();

        Loaded += async (_, _) =>
        {
            await IniciarVisorAsync();
            if (archivoInicial is not null)
                AbrirArchivo(archivoInicial);
        };
    }

    private string NombreDocumento => _archivoActual is null ? "Sin título" : Path.GetFileName(_archivoActual);

    // ---------- Vista previa ----------

    private async Task IniciarVisorAsync()
    {
        var datos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisorMD", "WebView2");
        var entorno = await CoreWebView2Environment.CreateAsync(userDataFolder: datos);
        await Visor.EnsureCoreWebView2Async(entorno);

        var core = Visor.CoreWebView2;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        // Sin atajos del navegador: F5, Ctrl+N, etc. llegan a la ventana y no recargan la plantilla.
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.NavigationStarting += Visor_NavigationStarting;
        core.NavigationCompleted += (_, _) =>
        {
            _vistaLista = true;
            ActualizarVista();
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            AbrirEnlace(e.Uri);
        };
        Visor.NavigateToString(Renderizador.Plantilla());
    }

    private void ActualizarVista()
    {
        _demoraVista.Stop();
        if (!_vistaLista || _modo == Modo.Editor)
            return;

        var html = _archivoActual is null && Editor.Document.TextLength == 0 && _modo == Modo.Vista
            ? Renderizador.Bienvenida()
            : Renderizador.Cuerpo(Editor.Text);
        Enviar(new { tipo = "html", html, @base = _hostActual is null ? null : $"https://{_hostActual}/" });
        SincronizarScroll();
    }

    // En modo dividido, la vista sigue a la primera línea visible del editor.
    private void SincronizarScroll()
    {
        if (!_vistaLista || _modo != Modo.Dividido)
            return;

        var vista = Editor.TextArea.TextView;
        var linea = vista.GetDocumentLineByVisualTop(vista.ScrollOffset.Y).LineNumber - 1;
        Enviar(new { tipo = "linea", linea });
    }

    private void Enviar(object mensaje) => Visor.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(mensaje));

    // Todos los clics en enlaces y los archivos arrastrados a la vista pasan por aquí.
    private void Visor_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith("data:", StringComparison.Ordinal) || e.Uri == "about:blank")
            return;

        e.Cancel = true;
        AbrirEnlace(e.Uri);
    }

    private void AbrirEnlace(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var destino))
            return;

        string? local = null;
        if (destino.IsFile)
            local = destino.LocalPath;
        else if (_archivoActual is not null && destino.Host.Equals(_hostActual, StringComparison.OrdinalIgnoreCase))
            local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_archivoActual)!, Uri.UnescapeDataString(destino.AbsolutePath.TrimStart('/'))));

        if (local is not null && EsMarkdown(local))
        {
            if (File.Exists(local))
                AbrirArchivo(local);
            else
                Avisar($"No se encontró el archivo:\n{local}");
            return;
        }

        // Cualquier otra cosa (web, correo, PDF, imágenes) se abre con el programa predeterminado de Windows.
        try
        {
            Process.Start(new ProcessStartInfo(local ?? destino.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Avisar($"No se pudo abrir el enlace:\n{ex.Message}");
        }
    }

    // ---------- Archivos ----------

    private void AbrirArchivo(string ruta)
    {
        if (!ConfirmarDescartar())
            return;

        var completa = Path.GetFullPath(ruta);
        string texto;
        try
        {
            (texto, _codificacion) = LeerTexto(completa);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Avisar($"No se pudo abrir el archivo:\n{ex.Message}");
            return;
        }

        AsignarArchivo(completa);
        CargarTexto(texto, conservarPosicion: false);
    }

    private void AsignarArchivo(string? ruta)
    {
        var core = Visor.CoreWebView2;
        if (_hostActual is not null)
            core?.ClearVirtualHostNameToFolderMapping(_hostActual);
        _vigilante?.Dispose();
        _vigilante = null;
        _cambioRechazado = null;
        _archivoActual = ruta;
        _hostActual = null;

        if (ruta is not null)
        {
            var carpeta = Path.GetDirectoryName(ruta)!;
            // Un host distinto por carpeta evita que el caché mezcle imágenes de documentos diferentes.
            _hostActual = $"d{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(carpeta.ToUpperInvariant())))[..16]}.visormd";
            core?.SetVirtualHostNameToFolderMapping(_hostActual, carpeta, CoreWebView2HostResourceAccessKind.Allow);
            Vigilar(carpeta, Path.GetFileName(ruta));
        }

        ActualizarTitulo();
        CommandManager.InvalidateRequerySuggested();
    }

    private void CargarTexto(string texto, bool conservarPosicion)
    {
        var posicion = Editor.CaretOffset;
        Editor.Document.Text = texto;
        Editor.Document.UndoStack.ClearAll();
        Editor.IsModified = false;
        if (conservarPosicion)
            Editor.CaretOffset = Math.Min(posicion, Editor.Document.TextLength);
        else
        {
            Editor.CaretOffset = 0;
            Editor.ScrollToHome();
        }
        ActualizarVista();
    }

    // Permite leer aunque otro programa tenga el archivo abierto, y recuerda la codificación para guardar igual.
    private static (string Texto, Encoding Codificacion) LeerTexto(string ruta)
    {
        using var flujo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var lector = new StreamReader(flujo, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var texto = lector.ReadToEnd();
        return (texto, lector.CurrentEncoding);
    }

    private bool Guardar() => _archivoActual is null ? GuardarComo() : Escribir(_archivoActual);

    private bool GuardarComo()
    {
        var dialogo = new SaveFileDialog
        {
            Title = "Guardar como",
            Filter = "Markdown (*.md)|*.md|Texto (*.txt)|*.txt|Todos los archivos (*.*)|*.*",
            DefaultExt = ".md",
            FileName = _archivoActual is null ? "documento.md" : Path.GetFileName(_archivoActual),
        };
        if (_archivoActual is not null)
            dialogo.InitialDirectory = Path.GetDirectoryName(_archivoActual);

        if (dialogo.ShowDialog(this) != true || !Escribir(dialogo.FileName))
            return false;

        if (!string.Equals(dialogo.FileName, _archivoActual, StringComparison.OrdinalIgnoreCase))
        {
            AsignarArchivo(Path.GetFullPath(dialogo.FileName));
            ActualizarVista();
        }
        return true;
    }

    private bool Escribir(string ruta)
    {
        try
        {
            File.WriteAllText(ruta, Editor.Text, _codificacion);
            Editor.IsModified = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Avisar($"No se pudo guardar el archivo:\n{ex.Message}");
            return false;
        }
    }

    private bool ConfirmarDescartar()
    {
        if (!Editor.IsModified)
            return true;

        return MessageBox.Show(this, $"¿Quieres guardar los cambios en {NombreDocumento}?", "VisorMD",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Warning) switch
        {
            MessageBoxResult.Yes => Guardar(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private void Vigilar(string carpeta, string nombre)
    {
        _vigilante = new FileSystemWatcher(carpeta, nombre)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler alCambiar = (_, _) => Dispatcher.BeginInvoke(() => Reiniciar(_demoraDisco));
        _vigilante.Changed += alCambiar;
        _vigilante.Created += alCambiar;
        _vigilante.Renamed += (s, e) => alCambiar(s, e);
        _vigilante.EnableRaisingEvents = true;
    }

    private void AlCambiarEnDisco()
    {
        _demoraDisco.Stop();
        if (_archivoActual is null)
            return;

        string texto;
        try
        {
            (texto, _) = LeerTexto(_archivoActual);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // borrado o bloqueado: se conserva lo que hay en el editor
        }

        // Incluye nuestro propio guardado, que también dispara el aviso.
        if (texto == Editor.Text || texto == _cambioRechazado)
            return;

        if (Editor.IsModified && MessageBox.Show(this,
                $"{NombreDocumento} cambió fuera de VisorMD.\n¿Quieres recargarlo? Perderás los cambios que no guardaste.",
                "VisorMD", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            _cambioRechazado = texto;
            return;
        }

        CargarTexto(texto, conservarPosicion: true);
    }

    private static bool EsMarkdown(string ruta) =>
        Extensiones.Contains(Path.GetExtension(ruta), StringComparer.OrdinalIgnoreCase);

    // ---------- Modos y edición ----------

    private void AplicarModo(Modo modo)
    {
        if (_aplicandoModo)
            return;
        _aplicandoModo = true;
        try
        {
            AplicarModoSinGuardia(modo);
        }
        finally
        {
            _aplicandoModo = false;
        }
    }

    private void AplicarModoSinGuardia(Modo modo)
    {
        _modo = modo;
        var conEditor = modo != Modo.Vista;
        var conVista = modo != Modo.Editor;

        ColumnaEditor.Width = conEditor ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ColumnaVista.Width = conVista ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Editor.Visibility = conEditor ? Visibility.Visible : Visibility.Collapsed;
        Visor.Visibility = conVista ? Visibility.Visible : Visibility.Collapsed;
        Divisor.Visibility = conEditor && conVista ? Visibility.Visible : Visibility.Collapsed;

        BotonVista.IsChecked = modo == Modo.Vista;
        BotonDividido.IsChecked = modo == Modo.Dividido;
        BotonEditor.IsChecked = modo == Modo.Editor;

        if (conEditor)
            Dispatcher.BeginInvoke(() => Editor.TextArea.Focus(), DispatcherPriority.Input);
        ActualizarVista();
        CommandManager.InvalidateRequerySuggested();
    }

    // Envuelve la selección con la marca (o la inserta vacía con el cursor al medio).
    private void Envolver(string marca)
    {
        var inicio = Editor.SelectionStart;
        var largo = Editor.SelectionLength;
        Editor.Document.Replace(inicio, largo, marca + Editor.SelectedText + marca);
        if (largo == 0)
            Editor.CaretOffset = inicio + marca.Length;
        else
            Editor.Select(inicio + marca.Length, largo);
        Editor.TextArea.Focus();
    }

    private void ActualizarTitulo()
    {
        Title = $"{(Editor.IsModified ? "*" : "")}{NombreDocumento} — VisorMD";
        EstadoRuta.Text = _archivoActual ?? "Sin guardar";
    }

    private void ActualizarPosicion()
    {
        var cursor = Editor.TextArea.Caret;
        EstadoPosicion.Text = $"Línea {cursor.Line}, columna {cursor.Column}";
    }

    // ---------- Tema ----------

    private void AplicarTema()
    {
        var p = Tema.Actual();
        Editor.Background = Tema.Pincel(p.Fondo);
        Editor.Foreground = Tema.Pincel(p.Texto);
        Editor.LineNumbersForeground = Tema.Pincel(p.Tenue);
        Editor.TextArea.SelectionBrush = Tema.Pincel(p.Seleccion);
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.SelectionBorder = null;
        Editor.TextArea.Caret.CaretBrush = Tema.Pincel(p.Texto);
        Editor.TextArea.TextView.CurrentLineBackground = Tema.Pincel(p.LineaActual);
        Editor.TextArea.TextView.CurrentLineBorder = null;
        Editor.TextArea.TextView.LinkTextForegroundBrush = Tema.Pincel(p.Enlace);
        Editor.SyntaxHighlighting = ResaltadoMarkdown.Crear(p);

        // Evita el destello blanco antes de que cargue la vista en modo oscuro.
        var fondo = Tema.Color(p.Fondo);
        Visor.DefaultBackgroundColor = System.Drawing.Color.FromArgb(fondo.A, fondo.R, fondo.G, fondo.B);
    }

    private void AlCambiarPreferencias(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(AplicarTema);
    }

    // ---------- Utilidades ----------

    private DispatcherTimer Temporizador(int milisegundos, Action accion)
    {
        var temporizador = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milisegundos) };
        temporizador.Tick += (_, _) => accion();
        return temporizador;
    }

    private static void Reiniciar(DispatcherTimer temporizador)
    {
        temporizador.Stop();
        temporizador.Start();
    }

    private void Avisar(string mensaje) =>
        MessageBox.Show(this, mensaje, "VisorMD", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ---------- Eventos ----------

    private void Editor_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Editor_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } archivos)
            return;
        e.Handled = true;
        AbrirArchivo(archivos[0]);
    }

    private void Nuevo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmarDescartar())
            return;

        _codificacion = new UTF8Encoding(false);
        AsignarArchivo(null);
        CargarTexto("", conservarPosicion: false);
        if (_modo == Modo.Vista)
            AplicarModo(Modo.Dividido);
    }

    private void Abrir_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dialogo = new OpenFileDialog
        {
            Title = "Abrir archivo Markdown",
            Filter = "Markdown (*.md;*.markdown)|*.md;*.markdown;*.mdown;*.mkd|Texto (*.txt)|*.txt|Todos los archivos (*.*)|*.*",
        };
        if (_archivoActual is not null)
            dialogo.InitialDirectory = Path.GetDirectoryName(_archivoActual);

        if (dialogo.ShowDialog(this) == true)
            AbrirArchivo(dialogo.FileName);
    }

    private void Guardar_Executed(object sender, ExecutedRoutedEventArgs e) => Guardar();

    private void GuardarComo_Executed(object sender, ExecutedRoutedEventArgs e) => GuardarComo();

    private void Recargar_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_archivoActual is null)
            return;
        if (Editor.IsModified && MessageBox.Show(this, "¿Quieres descartar los cambios y recargar el archivo desde el disco?",
                "VisorMD", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var (texto, codificacion) = LeerTexto(_archivoActual);
            _codificacion = codificacion;
            CargarTexto(texto, conservarPosicion: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Avisar($"No se pudo leer el archivo:\n{ex.Message}");
        }
    }

    private void HayArchivo_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _archivoActual is not null;

    private void EditorVisible_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _modo != Modo.Vista;

    private void BotonModo_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string modo } && IsInitialized)
            AplicarModo(Enum.Parse<Modo>(modo));
    }

    private void ModoVista_Executed(object sender, ExecutedRoutedEventArgs e) => AplicarModo(Modo.Vista);

    private void ModoDividido_Executed(object sender, ExecutedRoutedEventArgs e) => AplicarModo(Modo.Dividido);

    private void ModoEditor_Executed(object sender, ExecutedRoutedEventArgs e) => AplicarModo(Modo.Editor);

    private void Negrita_Executed(object sender, ExecutedRoutedEventArgs e) => Envolver("**");

    private void Cursiva_Executed(object sender, ExecutedRoutedEventArgs e) => Envolver("*");

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ConfirmarDescartar())
            e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= AlCambiarPreferencias;
        _vigilante?.Dispose();
        base.OnClosed(e);
    }
}
