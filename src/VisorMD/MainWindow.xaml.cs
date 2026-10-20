using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace VisorMD;

public partial class MainWindow : Window
{
    private enum Modo { Vista, Dividido, Editor }

    private enum Panel { Explorador, Esquema }

    private string? _archivoActual;
    // Host virtual que apunta a la carpeta del documento, para que funcionen imágenes y enlaces relativos.
    private string? _hostActual;
    private string? _carpetaRaiz;
    private Encoding _codificacion = new UTF8Encoding(false);
    private Modo _modo = Modo.Vista;
    private Panel _panel = Panel.Explorador;
    private bool _lateralVisible = true;
    private double _anchoLateral = 260;
    private IReadOnlyList<Titulo> _titulos = [];
    private bool _vistaLista;
    // Marcar un botón de modo vuelve a llamar a AplicarModo; esto corta la recursión.
    private bool _aplicandoModo;
    private FileSystemWatcher? _vigilante;
    // Versión en disco que el usuario ya decidió no recargar, para no volver a preguntar por lo mismo.
    private string? _cambioRechazado;
    private readonly DispatcherTimer _demoraDocumento;
    private readonly DispatcherTimer _demoraDisco;

    public MainWindow(string? archivoInicial)
    {
        InitializeComponent();

        _demoraDocumento = Temporizador(150, ActualizarDocumento);
        // Los editores suelen disparar varios eventos al guardar; se agrupan en una sola revisión.
        _demoraDisco = Temporizador(200, AlCambiarEnDisco);

        Editor.TextChanged += (_, _) => Reiniciar(_demoraDocumento);
        Editor.Document.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
                ActualizarTitulo();
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) => ActualizarPosicion();
        Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SincronizarScroll();
        Editor.Options.HighlightCurrentLine = true;
        SearchPanel.Install(Editor);

        PreviewDragEnter += Ventana_PreviewDragOver;
        PreviewDragOver += Ventana_PreviewDragOver;
        PreviewDrop += Ventana_PreviewDrop;
        StateChanged += (_, _) => AjustarMaximizado();

        AplicarTema();
        SystemEvents.UserPreferenceChanged += AlCambiarPreferencias;

        AplicarModo(Modo.Vista);
        AplicarLateral();
        ActualizarTitulo();
        ActualizarPosicion();
        ActualizarCodificacion();

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
            ActualizarDocumento();
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            AbrirEnlace(e.Uri);
        };
        Visor.NavigateToString(Renderizador.Plantilla());
    }

    // Procesa el texto una vez: actualiza esquema, conteo de palabras y, si se ve, la vista previa.
    private void ActualizarDocumento()
    {
        _demoraDocumento.Stop();
        var resultado = Renderizador.Procesar(Editor.Text);

        if (!resultado.Titulos.SequenceEqual(_titulos))
        {
            _titulos = resultado.Titulos;
            ListaEsquema.ItemsSource = _titulos;
            AplicarLateral();
        }
        EstadoPalabras.Content = resultado.Palabras == 1 ? "1 palabra" : $"{resultado.Palabras:N0} palabras";
        EstadoFinLinea.Content = Editor.Document.Text.Contains("\r\n") || !Editor.Document.Text.Contains('\n') ? "CRLF" : "LF";

        if (!_vistaLista || _modo == Modo.Editor)
            return;

        var bienvenida = _archivoActual is null && Editor.Document.TextLength == 0 && _modo == Modo.Vista;
        Enviar(new
        {
            tipo = "html",
            html = bienvenida ? Renderizador.Bienvenida() : resultado.Html,
            @base = _hostActual is null ? null : $"https://{_hostActual}/",
        });
        SincronizarScroll();
    }

    // En modo dividido, la vista sigue a la primera línea visible del editor.
    private void SincronizarScroll()
    {
        if (!_vistaLista || _modo != Modo.Dividido)
            return;

        var vista = Editor.TextArea.TextView;
        Enviar(new { tipo = "linea", linea = vista.GetDocumentLineByVisualTop(vista.ScrollOffset.Y).LineNumber - 1 });
    }

    private void Enviar(object mensaje) => Visor.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(mensaje));

    // Lleva el editor y la vista previa a una línea del documento (base 0).
    private void IrALinea(int linea)
    {
        if (_modo != Modo.Vista)
        {
            var numero = Math.Min(linea + 1, Editor.Document.LineCount);
            Editor.TextArea.Caret.Location = new TextLocation(numero, 1);
            Editor.ScrollToVerticalOffset(Editor.TextArea.TextView.GetVisualTopByDocumentLine(numero));
            Editor.TextArea.Focus();
        }
        if (_vistaLista && _modo != Modo.Editor)
            Enviar(new { tipo = "linea", linea });
    }

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

        if (local is not null && Directory.Exists(local))
        {
            AbrirCarpeta(local);
            return;
        }
        if (local is not null && Documentos.EsMarkdown(local))
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
        ActualizarCodificacion();
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

            // Como VS Code: si el archivo está fuera de la carpeta abierta, se abre su carpeta.
            if (_carpetaRaiz is null || !ruta.StartsWith(_carpetaRaiz + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                AbrirCarpeta(carpeta);
        }

        RevelarActual();
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
        {
            Editor.CaretOffset = Math.Min(posicion, Editor.Document.TextLength);
        }
        else
        {
            Editor.CaretOffset = 0;
            Editor.ScrollToHome();
        }
        ActualizarDocumento();
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
        var carpetaInicial = _archivoActual is null ? _carpetaRaiz : Path.GetDirectoryName(_archivoActual);
        if (carpetaInicial is not null)
            dialogo.InitialDirectory = carpetaInicial;

        if (dialogo.ShowDialog(this) != true || !Escribir(dialogo.FileName))
            return false;

        if (!string.Equals(dialogo.FileName, _archivoActual, StringComparison.OrdinalIgnoreCase))
        {
            AsignarArchivo(Path.GetFullPath(dialogo.FileName));
            RefrescarArbol();
            ActualizarDocumento();
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

    // ---------- Explorador ----------

    private void AbrirCarpeta(string carpeta)
    {
        _carpetaRaiz = Path.TrimEndingDirectorySeparator(Path.GetFullPath(carpeta));
        var nombre = Path.GetFileName(_carpetaRaiz);
        if (nombre.Length == 0)
            nombre = _carpetaRaiz; // raíz de una unidad, p. ej. "D:\"
        NombreCarpeta.Text = nombre.ToUpperInvariant();
        NombreCarpeta.ToolTip = _carpetaRaiz;
        EstadoCarpeta.Text = nombre;
        Arbol.ItemsSource = NodoArchivo.Listar(_carpetaRaiz);
        RevelarActual();
        AplicarLateral();
    }

    private void RefrescarArbol()
    {
        if (_carpetaRaiz is null || Arbol.ItemsSource is not IEnumerable<NodoArchivo> actuales)
            return;

        var expandidas = NodoArchivo.Expandidas(actuales);
        var nuevos = NodoArchivo.Listar(_carpetaRaiz);
        NodoArchivo.Expandir(nuevos, expandidas);
        Arbol.ItemsSource = nuevos;
        RevelarActual();
    }

    private void RevelarActual()
    {
        if (Arbol.ItemsSource is IEnumerable<NodoArchivo> nodos)
            NodoArchivo.Revelar(nodos, _archivoActual);
    }

    // ---------- Barra lateral ----------

    private void MostrarPanel(Panel panel)
    {
        _panel = panel;
        _lateralVisible = true;
        AplicarLateral();
    }

    private void AplicarLateral()
    {
        if (Lateral.Visibility == Visibility.Visible && ColumnaLateral.ActualWidth > 0)
            _anchoLateral = ColumnaLateral.ActualWidth;

        ColumnaLateral.Width = new GridLength(_lateralVisible ? _anchoLateral : 0);
        Lateral.Visibility = _lateralVisible ? Visibility.Visible : Visibility.Collapsed;
        DivisorLateral.Visibility = Lateral.Visibility;
        BotonLateral.IsChecked = _lateralVisible;
        ActividadExplorador.IsChecked = _lateralVisible && _panel == Panel.Explorador;
        ActividadEsquema.IsChecked = _lateralVisible && _panel == Panel.Esquema;

        var explorador = _panel == Panel.Explorador;
        TituloLateral.Text = explorador ? "EXPLORADOR" : "ESQUEMA";
        AccionesExplorador.Visibility = Visible(explorador);
        PanelExplorador.Visibility = Visible(explorador && _carpetaRaiz is not null);
        ExploradorVacio.Visibility = Visible(explorador && _carpetaRaiz is null);
        ListaEsquema.Visibility = Visible(!explorador && _titulos.Count > 0);
        EsquemaVacio.Visibility = Visible(!explorador && _titulos.Count == 0);
    }

    // ---------- Modos y edición ----------

    private void AplicarModo(Modo modo)
    {
        if (_aplicandoModo)
            return;
        _aplicandoModo = true;
        try
        {
            _modo = modo;
            var conEditor = modo != Modo.Vista;
            var conVista = modo != Modo.Editor;

            ColumnaEditor.Width = conEditor ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            ColumnaVista.Width = conVista ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Editor.Visibility = Visible(conEditor);
            Visor.Visibility = Visible(conVista);
            Divisor.Visibility = Visible(conEditor && conVista);

            BotonVista.IsChecked = modo == Modo.Vista;
            BotonDividido.IsChecked = modo == Modo.Dividido;
            BotonEditor.IsChecked = modo == Modo.Editor;
            EstadoModo.Content = modo.ToString();
            EstadoPosicion.Visibility = Visible(conEditor);

            if (conEditor)
                Dispatcher.BeginInvoke(() => Editor.TextArea.Focus(), DispatcherPriority.Input);
            ActualizarDocumento();
            CommandManager.InvalidateRequerySuggested();
        }
        finally
        {
            _aplicandoModo = false;
        }
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
        Title = $"{(Editor.IsModified ? "● " : "")}{NombreDocumento} — VisorMD";
        TituloVentana.ToolTip = _archivoActual;
    }

    private void ActualizarPosicion()
    {
        var cursor = Editor.TextArea.Caret;
        EstadoPosicion.Content = $"Lín. {cursor.Line}, col. {cursor.Column}";
    }

    private void ActualizarCodificacion()
    {
        EstadoCodificacion.Content = _codificacion switch
        {
            UTF8Encoding utf8 => utf8.GetPreamble().Length > 0 ? "UTF-8 con BOM" : "UTF-8",
            UnicodeEncoding { CodePage: 1201 } => "UTF-16 BE",
            UnicodeEncoding => "UTF-16 LE",
            var otra => otra.WebName.ToUpperInvariant(),
        };
    }

    // ---------- Ventana y tema ----------

    // Con WindowChrome, una ventana maximizada se sale de la pantalla por el grosor del borde.
    private void AjustarMaximizado()
    {
        var maximizada = WindowState == WindowState.Maximized;
        Raiz.Margin = maximizada ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        BotonMaximizar.Content = maximizada ? "\uE923" : "\uE922";
        BotonMaximizar.ToolTip = maximizada ? "Restaurar" : "Maximizar";
    }

    private void AplicarTema()
    {
        var p = Tema.Actual();
        var recursos = Application.Current.Resources;
        recursos["Chrome.Fondo"] = Tema.Pincel(p.ChromeFondo);
        recursos["Chrome.Borde"] = Tema.Pincel(p.ChromeBorde);
        recursos["Chrome.Texto"] = Tema.Pincel(p.ChromeTexto);
        recursos["Chrome.Tenue"] = Tema.Pincel(p.ChromeTenue);
        recursos["Chrome.Hover"] = Tema.Pincel(p.ChromeHover);
        recursos["Chrome.Activo"] = Tema.Pincel(p.ChromeActivo);
        recursos["Acento"] = Tema.Pincel(p.Acento);

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

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static T? Ancestro<T>(object? origen) where T : DependencyObject
    {
        var actual = origen as DependencyObject;
        while (actual is not null and not T)
            actual = actual is Visual ? VisualTreeHelper.GetParent(actual) : LogicalTreeHelper.GetParent(actual);
        return actual as T;
    }

    private void Avisar(string mensaje) =>
        MessageBox.Show(this, mensaje, "VisorMD", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ---------- Eventos ----------

    private void Ventana_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    // Una carpeta arrastrada se abre en el Explorador; un archivo, en el editor.
    private void Ventana_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } rutas)
            return;
        e.Handled = true;
        if (Directory.Exists(rutas[0]))
            AbrirCarpeta(rutas[0]);
        else
            AbrirArchivo(rutas[0]);
    }

    private void Arbol_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // La flecha de expandir ya hace lo suyo.
        if (Ancestro<ToggleButton>(e.OriginalSource) is not null)
            return;
        if (Ancestro<TreeViewItem>(e.OriginalSource)?.DataContext is not NodoArchivo nodo)
            return;

        if (nodo.EsCarpeta)
            nodo.Expandido = !nodo.Expandido;
        else if (!string.Equals(nodo.Ruta, _archivoActual, StringComparison.OrdinalIgnoreCase))
            AbrirArchivo(nodo.Ruta);
    }

    private void Arbol_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Arbol.SelectedItem is NodoArchivo { EsCarpeta: false } nodo)
        {
            e.Handled = true;
            AbrirArchivo(nodo.Ruta);
        }
    }

    private void ListaEsquema_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Ancestro<ListBoxItem>(e.OriginalSource)?.DataContext is Titulo titulo)
            IrALinea(titulo.Linea);
    }

    private void Actividad_Click(object sender, RoutedEventArgs e)
    {
        var panel = Enum.Parse<Panel>((string)((FrameworkElement)sender).Tag);
        // Como en VS Code: tocar el panel que ya está abierto oculta la barra lateral.
        if (_lateralVisible && _panel == panel)
        {
            _lateralVisible = false;
            AplicarLateral();
        }
        else
        {
            MostrarPanel(panel);
        }
    }

    private void BotonModo_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string modo } && IsInitialized)
            AplicarModo(Enum.Parse<Modo>(modo));
    }

    private void EstadoModo_Click(object sender, RoutedEventArgs e) => AplicarModo((Modo)(((int)_modo + 1) % 3));

    private void RefrescarArbol_Click(object sender, RoutedEventArgs e) => RefrescarArbol();

    private void AbrirMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } boton)
            return;
        // El menú vive fuera del árbol de la ventana: sin destino, sus comandos no encuentran quién los atienda.
        // Desde el editor suben hasta la ventana, así sirven tanto Deshacer/Copiar como los comandos propios.
        foreach (var item in menu.Items.OfType<MenuItem>())
            item.CommandTarget ??= Editor.TextArea;
        menu.PlacementTarget = boton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Minimizar_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximizar_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void Cerrar_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void Nuevo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmarDescartar())
            return;

        _codificacion = new UTF8Encoding(false);
        AsignarArchivo(null);
        CargarTexto("", conservarPosicion: false);
        ActualizarCodificacion();
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
        var carpetaInicial = _archivoActual is null ? _carpetaRaiz : Path.GetDirectoryName(_archivoActual);
        if (carpetaInicial is not null)
            dialogo.InitialDirectory = carpetaInicial;

        if (dialogo.ShowDialog(this) == true)
            AbrirArchivo(dialogo.FileName);
    }

    private void AbrirCarpeta_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dialogo = new OpenFolderDialog { Title = "Abrir carpeta" };
        if (_carpetaRaiz is not null)
            dialogo.InitialDirectory = _carpetaRaiz;

        if (dialogo.ShowDialog(this) == true)
        {
            AbrirCarpeta(dialogo.FolderName);
            MostrarPanel(Panel.Explorador);
        }
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
            ActualizarCodificacion();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Avisar($"No se pudo leer el archivo:\n{ex.Message}");
        }
    }

    private void HayArchivo_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _archivoActual is not null;

    private void EditorVisible_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _modo != Modo.Vista;

    private void ModoVista_Executed(object sender, ExecutedRoutedEventArgs e) => AplicarModo(Modo.Vista);

    private void ModoDividido_Executed(object sender, ExecutedRoutedEventArgs e) => AplicarModo(Modo.Dividido);

    private void ModoEditor_Executed(object sender, ExecutedRoutedEventArgs e) => AplicarModo(Modo.Editor);

    private void VerExplorador_Executed(object sender, ExecutedRoutedEventArgs e) => MostrarPanel(Panel.Explorador);

    private void VerEsquema_Executed(object sender, ExecutedRoutedEventArgs e) => MostrarPanel(Panel.Esquema);

    private void AlternarLateral_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        _lateralVisible = !_lateralVisible;
        AplicarLateral();
    }

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
