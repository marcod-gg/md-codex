using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace MdCodex;

public partial class MainWindow : Window
{
    private enum Modo { Vista, Dividido, Editor, Pro }

    // Host de la página del editor Pro, que vive en la carpeta Pro junto al .exe. No termina en
    // ".mdcodex" para no chocar con el filtro que sirve los archivos de los documentos.
    private const string HostPro = "mdcodex.editor";

    private enum Panel { Documentos, Explorador, Esquema }

    private readonly ObservableCollection<Pestana> _pestanas = [];
    // Documento vacío que muestra el editor cuando no hay pestañas abiertas.
    private readonly TextDocument _sinDocumento = new();
    // Host virtual de cada carpeta de documento → carpeta en disco, para servir sus imágenes.
    private readonly Dictionary<string, string> _carpetasPorHost = new(StringComparer.OrdinalIgnoreCase);
    private Pestana? _activa;
    // Carpetas de todos los espacios de trabajo.
    private IEnumerable<string> _carpetas => _recientes.Espacios.SelectMany(e => e.Carpetas);
    // Nodo sobre el que se abrió el menú contextual del árbol (null: espacio vacío del panel).
    private NodoArchivo? _nodoMenu;
    // Arrastre en el árbol (un espacio de trabajo o un archivo): dónde empezó y qué nodo.
    private Point _inicioArrastre;
    private NodoArchivo? _nodoArrastrado;
    // Qué hacer con el nombre escrito en la fila de nombre (archivo nuevo, espacio nuevo o renombrado).
    private Action<string>? _alAceptarNombre;
    private Modo _modo = Modo.Vista;
    private Panel _panel = Panel.Explorador;
    // Con la barra lateral automática, si está desplegada sobre el área de trabajo.
    private bool _lateralDesplegado;
    // Si la barra lateral se muestra (o está apareciendo); al desvanecerse sigue visible un momento.
    private bool _lateralMostrado = true;
    // Release más nueva, si hay una actualización pendiente; su instalador, una vez descargado.
    private Actualizacion.Release? _actualizacion;
    private string? _instalador;
    private bool _descargando;
    // "Reiniciar ahora": al cerrar se instala la actualización y se vuelve a abrir la app.
    private bool _reabrirTrasActualizar;
    private double _anchoLateral = 280;
    private const double AnchoMinimoLateral = 240;
    private IReadOnlyList<Titulo> _titulos = [];
    private bool _vistaLista;
    private Task? _inicioVisor;
    private CoreWebView2Environment? _entorno;
    private bool _proIniciado;
    private bool _proListo;
    // Evita volver a sincronizar con el editor Pro cuando el cierre ya lo hizo.
    private bool _cierreSincronizado;
    // Al cambiar de pestaña, la vista previa vuelve al scroll que tenía esa pestaña.
    private bool _restaurarScrollVista;
    // Marcar un botón de modo vuelve a llamar a AplicarModo; esto corta la recursión.
    private bool _aplicandoModo;
    private readonly DispatcherTimer _demoraDocumento;
    // Guardado automático "al dejar de escribir": se reinicia con cada cambio.
    private readonly DispatcherTimer _guardadoDiferido;
    // Mientras se marcan los controles del panel con la configuración guardada, sus eventos no la cambian.
    private bool _mostrandoConfiguracion;

    private Configuracion Config => _recientes.Configuracion;

    public MainWindow(IReadOnlyList<string> archivosIniciales)
    {
        _recientes.AsegurarEspacio();
        InitializeComponent();

        BarraPestanas.ItemsSource = _pestanas;
        _pestanas.CollectionChanged += (_, _) => ActualizarDocumentos();
        ArbolDocumentos.MouseLeave += (_, _) =>
        {
            if (_documentosPendientes)
                ActualizarDocumentos();
        };
        Editor.Document = _sinDocumento;
        _demoraDocumento = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _demoraDocumento.Tick += (_, _) => ActualizarDocumento();

        _guardadoDiferido = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _guardadoDiferido.Tick += (_, _) =>
        {
            _guardadoDiferido.Stop();
            GuardarAutomatico(_activa);
        };

        Editor.TextChanged += (_, _) =>
        {
            Reiniciar(_demoraDocumento);
            if (Config.GuardadoAutomatico == GuardadoAutomatico.AlDejarDeEscribir)
                Reiniciar(_guardadoDiferido);
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) => ActualizarPosicion();
        Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SincronizarScroll();
        SearchPanel.Install(Editor);
        // Aire a la izquierda del texto, después de la línea que lo separa de los números.
        Editor.TextArea.TextView.Margin = new Thickness(10, 0, 0, 0);
        // Ctrl + rueda sobre el editor cambia el zoom (en la vista y en el editor por bloques lo hace WebView2).
        Editor.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control)
                return;
            e.Handled = true;
            CambiarZoom(e.Delta > 0 ? 1 : -1);
        };
        Deactivated += (_, _) =>
        {
            if (Config.GuardadoAutomatico == GuardadoAutomatico.AlCambiar)
                foreach (var p in _pestanas)
                    GuardarAutomatico(p);
        };

        Tema.Preferido = Config.Tema;
        Imagenes.Carpeta = Config.CarpetaImagenes;
        MostrarConfiguracion();
        AplicarEditor();
        AplicarZoom();

        PreviewDragEnter += Ventana_PreviewDragOver;
        PreviewDragOver += Ventana_PreviewDragOver;
        PreviewDrop += Ventana_PreviewDrop;
        PreviewMouseDown += Ventana_PreviewMouseDown;
        StateChanged += (_, _) => AjustarMaximizado();
        // Maximizada, pasar a otro monitor (Win+Mayús+flecha) cambia el marco y la escala.
        LocationChanged += (_, _) => { if (WindowState == WindowState.Maximized) AjustarMaximizado(); };
        DpiChanged += (_, _) => { if (WindowState == WindowState.Maximized) AjustarMaximizado(); };
        SourceInitialized += (_, _) =>
        {
            AjustarAPantalla();
            var ventana = new WindowInteropHelper(this).Handle;
            Nativo.RedondearEsquinas(ventana);
            HwndSource.FromHwnd(ventana).AddHook(GanchoVentana);
        };
        AreaTrabajo.SizeChanged += (_, _) => RecortarAreaTrabajo();

        AplicarTema();
        SystemEvents.UserPreferenceChanged += AlCambiarPreferencias;

        AplicarModo(ModoDeInicio());
        RefrescarArbol();
        AplicarLateral();
        Activar(null);

        Loaded += async (_, _) =>
        {
            _inicioVisor = IniciarVisorAsync();
            await _inicioVisor;
            if (archivosIniciales.Count > 0)
            {
                foreach (var archivo in archivosIniciales)
                    AbrirArchivo(archivo);
            }
            else if (Config.ReabrirSesion)
            {
                RestaurarSesion();
            }

            if (Config.BuscarActualizaciones && await Actualizacion.BuscarAsync() is { } nueva)
                MostrarActualizacion(nueva);
        };
    }

    // ---------- Recientes y sesión ----------

    private readonly Recientes _recientes = Recientes.Cargar();
    // Al restaurar la sesión no se reordenan los recientes.
    private bool _restaurando;

    private void RestaurarSesion()
    {
        if (_recientes.UltimaSesion is not { } sesion)
            return;
        _restaurando = true;
        try
        {
            foreach (var archivo in sesion.Archivos.Where(File.Exists))
                AbrirArchivo(archivo);
            if (_pestanas.FirstOrDefault(p => string.Equals(p.Ruta, sesion.Activo, StringComparison.OrdinalIgnoreCase)) is { } activa)
                Activar(activa);
            // El modo ya se eligió al iniciar (ModoDeInicio).
        }
        finally
        {
            _restaurando = false;
        }
    }

    // El de la configuración, o el de la última vez.
    private Modo ModoDeInicio() => Config.ModoInicial switch
    {
        ModoInicial.Vista => Modo.Vista,
        ModoInicial.Dividido => Modo.Dividido,
        ModoInicial.Editor => Modo.Editor,
        ModoInicial.Pro => Modo.Pro,
        _ => Enum.TryParse<Modo>(_recientes.UltimaSesion?.Modo, out var modo) ? modo : Modo.Vista,
    };

    private void GuardarSesion()
    {
        // Los documentos sin título no tienen dónde reabrirse.
        _recientes.UltimaSesion = new Sesion(
            null,
            _pestanas.Where(p => p.Ruta is not null).Select(p => p.Ruta!).ToList(),
            _activa?.Ruta,
            _modo.ToString());
        _recientes.Guardar();
    }

    private void RegistrarArchivoReciente(string ruta)
    {
        if (_restaurando)
            return;
        _recientes.AgregarArchivo(ruta);
        _recientes.Guardar();
        ActualizarDocumentos();
    }

    private void RegistrarCarpetaReciente(string ruta)
    {
        if (_restaurando)
            return;
        _recientes.AgregarCarpeta(ruta);
        _recientes.Guardar();
    }

    // Arma el submenú "Abrir reciente" cada vez que se abre el menú Archivo.
    private void RellenarRecientes()
    {
        _recientes.QuitarInexistentes();
        MenuRecientes.Items.Clear();

        // El nombre va en un TextBlock: como Header de texto, un "_" se tomaría como tecla de acceso.
        MenuItem Elemento(string ruta, string icono, Action abrir)
        {
            var item = new MenuItem
            {
                Header = new TextBlock { Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(ruta)) is { Length: > 0 } nombre ? nombre : ruta },
                Icon = new TextBlock { Text = icono, FontFamily = (FontFamily)FindResource("Iconos") },
                ToolTip = ruta,
            };
            item.Click += (_, _) => abrir();
            return item;
        }

        foreach (var archivo in _recientes.Archivos)
            MenuRecientes.Items.Add(Elemento(archivo, "", () => AbrirArchivo(archivo)));
        if (_recientes.Archivos.Count > 0 && _recientes.Carpetas.Count > 0)
            MenuRecientes.Items.Add(new Separator());
        foreach (var carpeta in _recientes.Carpetas)
            MenuRecientes.Items.Add(Elemento(carpeta, "", () =>
            {
                AbrirCarpeta(carpeta);
                RegistrarCarpetaReciente(carpeta);
                MostrarPanel(Panel.Explorador);
            }));

        if (MenuRecientes.Items.Count == 0)
        {
            MenuRecientes.Items.Add(new MenuItem { Header = "No hay elementos recientes", IsEnabled = false });
            return;
        }
        MenuRecientes.Items.Add(new Separator());
        var limpiar = new MenuItem { Header = "Limpiar recientes" };
        limpiar.Click += (_, _) =>
        {
            _recientes.Limpiar();
            _recientes.Guardar();
            ActualizarDocumentos();
        };
        MenuRecientes.Items.Add(limpiar);
    }

    // ---------- Vista previa ----------

    private async Task IniciarVisorAsync()
    {
        var datos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdCodex", "WebView2");
        var entorno = await CoreWebView2Environment.CreateAsync(userDataFolder: datos);
        _entorno = entorno;
        await Visor.EnsureCoreWebView2Async(entorno);

        var core = Visor.CoreWebView2;
        // Claro u oscuro según la configuración (las dos páginas usan prefers-color-scheme).
        core.Profile.PreferredColorScheme = EsquemaWeb();
        // El zoom guardado, y desde ahora Ctrl + rueda en la vista cambia el de toda la app.
        Visor.ZoomFactor = Config.Zoom;
        Visor.ZoomFactorChanged += (_, _) => FijarZoom(Visor.ZoomFactor);
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        // Sin atajos del navegador: F5, Ctrl+W, etc. llegan a la ventana y no recargan la plantilla.
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        // Sin la burbuja con la URL al pasar sobre un enlace; la dirección se ve como tooltip (ver la plantilla).
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += Visor_NavigationStarting;
        // Imágenes y otros archivos relativos al documento. Se sirven a mano y no con
        // SetVirtualHostNameToFolderMapping, porque esa asignación solo vale para páginas cargadas
        // después de hacerla, y la plantilla se carga una sola vez.
        core.AddWebResourceRequestedFilter("https://*.mdcodex/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => e.Response = ServirArchivo(entorno, e.Request.Uri);
        core.NavigationCompleted += (_, _) =>
        {
            _vistaLista = true;
            _restaurarScrollVista = true;
            EnviarAncho();
            ActualizarDocumento();
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            AbrirEnlace(e.Uri);
        };
        core.WebMessageReceived += Visor_WebMessageReceived;
        Visor.NavigateToString(Renderizador.Plantilla());
    }

    // Procesa el texto de la pestaña activa: esquema, palabras y, si se ve, la vista previa.
    private void ActualizarDocumento()
    {
        _demoraDocumento.Stop();
        var p = _activa;
        var resultado = p is null ? null : Renderizador.Procesar(p.Documento.Text);

        var titulos = resultado?.Titulos ?? [];
        if (!titulos.SequenceEqual(_titulos))
        {
            _titulos = titulos;
            ListaEsquema.ItemsSource = _titulos;
            AplicarLateral();
        }
        if (p is not null)
        {
            EstadoPalabras.Content = resultado!.Palabras == 1 ? "1 palabra" : $"{resultado.Palabras:N0} palabras";
            var texto = p.Documento.Text;
            EstadoFinLinea.Content = texto.Contains("\r\n") || !texto.Contains('\n') ? "CRLF" : "LF";
        }

        if (!_vistaLista || (p is not null && _modo is Modo.Editor or Modo.Pro))
            return;

        var ubicacion = p is null ? null : Ubicar(p);
        if (ubicacion is not null)
            _carpetasPorHost[ubicacion.Host] = ubicacion.Raiz;

        Enviar(new
        {
            tipo = "html",
            id = p?.Id ?? 0,
            html = resultado?.Html ?? Renderizador.Bienvenida(),
            @base = ubicacion?.Base,
            scroll = _restaurarScrollVista ? p?.ScrollVista : null,
        });
        _restaurarScrollVista = false;
        SincronizarScroll();
    }

    // En modo dividido, editor y vista se siguen. Se envía la línea que queda arriba (desde 0, con la
    // fracción ya desplazada de esa línea) y si se llegó al final; la vista interpola entre sus bloques.
    // Mientras se aplica un scroll que vino de la vista, no se devuelve (no se rebota).
    private bool _scrollDesdeVista;

    private void SincronizarScroll()
    {
        if (!_vistaLista || _modo != Modo.Dividido || _activa is null || _scrollDesdeVista || !Config.SincronizarDesplazamiento)
            return;

        var vista = Editor.TextArea.TextView;
        var y = vista.ScrollOffset.Y;
        var linea = vista.GetDocumentLineByVisualTop(y);
        var (arriba, abajo) = LimitesDeLinea(vista, linea.LineNumber);
        var fraccion = abajo > arriba ? (y - arriba) / (abajo - arriba) : 0;
        var final = y > 0 && y + vista.ActualHeight >= vista.DocumentHeight - 1;
        Enviar(new { tipo = "linea", linea = linea.LineNumber - 1 + fraccion, total = Editor.Document.LineCount, final });
    }

    // Lleva el editor a la línea (desde 0, con fracción) que la vista tiene arriba.
    private void SeguirVista(double linea, bool final)
    {
        if (_modo != Modo.Dividido || _activa is null || !Config.SincronizarDesplazamiento)
            return;

        var vista = Editor.TextArea.TextView;
        double y;
        if (final)
        {
            y = vista.DocumentHeight;
        }
        else
        {
            var numero = Math.Clamp((int)Math.Floor(linea) + 1, 1, Editor.Document.LineCount);
            var (arriba, abajo) = LimitesDeLinea(vista, numero);
            y = arriba + (abajo - arriba) * Math.Clamp(linea - Math.Floor(linea), 0, 1);
        }
        _scrollDesdeVista = true;
        Editor.ScrollToVerticalOffset(y);
        // El scroll del editor se aplica en el próximo layout; recién después se vuelve a escuchar.
        Dispatcher.BeginInvoke(() => _scrollDesdeVista = false, DispatcherPriority.Background);
    }

    // Dónde empieza y termina una línea del documento (con ajuste de línea puede ocupar varias).
    private static (double Arriba, double Abajo) LimitesDeLinea(ICSharpCode.AvalonEdit.Rendering.TextView vista, int numero)
    {
        var arriba = vista.GetVisualTopByDocumentLine(numero);
        var abajo = numero < vista.Document.LineCount ? vista.GetVisualTopByDocumentLine(numero + 1) : vista.DocumentHeight;
        return (arriba, abajo);
    }

    private void Enviar(object mensaje) => Visor.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(mensaje));

    private static readonly Dictionary<string, string> TiposMime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml", [".webp"] = "image/webp", [".bmp"] = "image/bmp", [".ico"] = "image/x-icon",
        [".avif"] = "image/avif", [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".mp3"] = "audio/mpeg",
        [".css"] = "text/css", [".js"] = "text/javascript", [".pdf"] = "application/pdf", [".txt"] = "text/plain; charset=utf-8",
    };

    private sealed record Ubicacion(string Host, string Raiz, string Base);

    // La raíz virtual de un documento es la carpeta abierta en el Explorador que lo contiene, o su
    // propia carpeta si no está en ninguna. La URL base lleva la ruta completa de la carpeta del documento
    // ("https://host/C/Users/…/"), así los enlaces relativos llegan al archivo real aunque suban con "../".
    // Solo se sirve lo que está dentro de la raíz o en la carpeta de imágenes (ver RutaLocal).
    private Ubicacion? Ubicar(Pestana p)
    {
        if (p.Carpeta is not { } carpeta)
            return null;
        var raiz = RaizDe(carpeta) ?? carpeta;
        var host = Documentos.HostPara(raiz);
        return new Ubicacion(host, raiz, $"https://{host}/{Documentos.CaminoUrl(carpeta)}/");
    }

    // Traduce una URL virtual a un archivo permitido; null si no corresponde o está fuera de lo permitido.
    private string? RutaLocal(Uri destino)
    {
        var camino = Uri.UnescapeDataString(destino.AbsolutePath.TrimStart('/'));
        // Las librerías de la vista previa viven en la carpeta Vista junto al .exe.
        if (destino.Host.Equals(Documentos.HostRecursos, StringComparison.OrdinalIgnoreCase))
        {
            var vista = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Vista")));
            var recurso = Path.GetFullPath(Path.Combine(vista, camino));
            return Documentos.EstaDentro(recurso, vista) ? recurso : null;
        }
        if (_carpetasPorHost.GetValueOrDefault(destino.Host) is not { } raiz || Documentos.RutaDeCamino(camino) is not { } enDisco)
            return null;
        // Todo pasa por GetFullPath: expande los nombres cortos (MARCOG~1), y si solo una ruta se expandiera,
        // el archivo parecería estar fuera de la carpeta y se negaría.
        var ruta = Path.GetFullPath(enDisco);
        var permitidas = new List<string> { raiz };
        if (Imagenes.CarpetaFija is { } imagenes)
            permitidas.Add(imagenes);
        return permitidas.Any(c => Documentos.EstaDentro(ruta, Path.TrimEndingDirectorySeparator(Path.GetFullPath(c)))) ? ruta : null;
    }

    private CoreWebView2WebResourceResponse ServirArchivo(CoreWebView2Environment entorno, string uri)
    {
        CoreWebView2WebResourceResponse NoEncontrado() => entorno.CreateWebResourceResponse(null, 404, "Not Found", "");

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var destino) || RutaLocal(destino) is not { } ruta || !File.Exists(ruta))
            return NoEncontrado();

        try
        {
            var flujo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var tipo = TiposMime.GetValueOrDefault(Path.GetExtension(ruta), "application/octet-stream");
            // Sin caché: si la imagen cambia en disco, la vista previa muestra la nueva al actualizarse.
            return entorno.CreateWebResourceResponse(flujo, 200, "OK", $"Content-Type: {tipo}\r\nCache-Control: no-store");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NoEncontrado();
        }
    }

    // La página avisa dónde quedó su scroll; se guarda en la pestaña a la que pertenece.
    private void Visor_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var raiz = json.RootElement;
            if (raiz.TryGetProperty("tipo", out var tipoVista) && tipoVista.GetString() == "lineaVista")
            {
                SeguirVista(raiz.GetProperty("linea").GetDouble(), raiz.GetProperty("final").GetBoolean());
                return;
            }
            if (raiz.TryGetProperty("tipo", out var tipo) && tipo.GetString() == "scroll"
                && raiz.TryGetProperty("id", out var id) && raiz.TryGetProperty("y", out var y)
                && _pestanas.FirstOrDefault(p => p.Id == id.GetInt32()) is { } pestana)
                pestana.ScrollVista = y.GetDouble();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Mensaje con otro formato: se ignora.
        }
    }

    // ---------- Modo Pro ----------

    // Muestra el editor Pro: la primera vez carga su página; después solo le envía el documento activo.
    private async Task MostrarProAsync()
    {
        if (_proIniciado)
        {
            CargarEnPro();
            return;
        }
        _proIniciado = true;
        if (_inicioVisor is not null)
            await _inicioVisor;
        if (_entorno is not { } entorno)
            return;

        await VisorPro.EnsureCoreWebView2Async(entorno);
        var core = VisorPro.CoreWebView2;
        VisorPro.ZoomFactor = Config.Zoom;
        VisorPro.ZoomFactorChanged += (_, _) => FijarZoom(VisorPro.ZoomFactor);
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // Esta asignación sí sirve aquí: se hace antes de cargar la página.
        core.SetVirtualHostNameToFolderMapping(HostPro, Path.Combine(AppContext.BaseDirectory, "Pro"), CoreWebView2HostResourceAccessKind.Allow);
        core.AddWebResourceRequestedFilter("https://*.mdcodex/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => e.Response = ServirArchivo(entorno, e.Request.Uri);
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith($"https://{HostPro}/", StringComparison.OrdinalIgnoreCase))
                return;
            e.Cancel = true;
            AbrirEnlace(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            AbrirEnlace(e.Uri);
        };
        core.WebMessageReceived += VisorPro_WebMessageReceived;
        core.Navigate($"https://{HostPro}/index.html");
    }

    private void CargarEnPro()
    {
        if (!_proListo || _activa is not { } p)
            return;
        var ubicacion = Ubicar(p);
        if (ubicacion is not null)
            _carpetasPorHost[ubicacion.Host] = ubicacion.Raiz;
        EnviarPro(new { tipo = "cargar", id = p.Id, markdown = p.Documento.Text, @base = ubicacion?.Base });
        EnviarPro(new { tipo = "enfocar" });
    }

    private void EnviarPro(object mensaje) => VisorPro.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(mensaje));

    private void VisorPro_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var raiz = json.RootElement;
            switch (raiz.GetProperty("tipo").GetString())
            {
                case "listo":
                    _proListo = true;
                    EnviarAnchoPro();
                    if (_modo == Modo.Pro)
                        CargarEnPro();
                    break;
                case "cambio":
                    AplicarDesdePro(raiz);
                    break;
                case "elegirImagen":
                    ElegirImagenes();
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // Mensaje con otro formato: se ignora.
        }
    }

    // Trae lo último escrito en el editor Pro, que avisa con un pequeño retraso. Se usa antes de
    // guardar o cerrar, para no perder las últimas pulsaciones.
    private async Task SincronizarProAsync()
    {
        if (!_proListo)
            return;
        var resultado = await VisorPro.CoreWebView2.ExecuteScriptAsync("window.mdcodex.pendiente()");
        using var json = JsonDocument.Parse(resultado);
        if (json.RootElement.ValueKind == JsonValueKind.Object)
            AplicarDesdePro(json.RootElement);
    }

    // Aplica el Markdown del editor Pro a su pestaña. Solo reemplaza el tramo que cambió,
    // así el historial de deshacer y el resto del archivo quedan intactos.
    private void AplicarDesdePro(JsonElement cambio)
    {
        var id = cambio.GetProperty("id").GetInt32();
        var markdown = cambio.GetProperty("markdown").GetString() ?? "";
        if (_pestanas.FirstOrDefault(p => p.Id == id) is not { } pestana)
            return;

        var actual = pestana.Documento.Text;
        // Conserva los fines de línea del archivo; si son mixtos, el estilo que más se usa.
        var crlf = actual.Split("\r\n").Length - 1;
        var lf = actual.Count(c => c == '\n') - crlf;
        markdown = markdown.Replace("\r\n", "\n");
        if (crlf > lf)
            markdown = markdown.Replace("\n", "\r\n");
        if (markdown == actual)
            return;

        var limite = Math.Min(actual.Length, markdown.Length);
        var inicio = 0;
        while (inicio < limite && actual[inicio] == markdown[inicio])
            inicio++;
        var fin = 0;
        while (fin < limite - inicio && actual[actual.Length - 1 - fin] == markdown[markdown.Length - 1 - fin])
            fin++;
        pestana.Documento.Replace(inicio, actual.Length - inicio - fin, markdown.Substring(inicio, markdown.Length - inicio - fin));
    }

    // ---------- Imágenes en el modo Pro ----------

    private const string AvisoSinGuardar =
        "Guarda el documento antes de agregar imágenes: se guardan junto a él, en la carpeta «imagenes».";

    private void ElegirImagenes()
    {
        if (_activa?.Carpeta is null)
        {
            Avisar(AvisoSinGuardar);
            return;
        }
        var dialogo = new OpenFileDialog { Title = "Insertar imagen", Filter = Imagenes.Filtro, Multiselect = true };
        if (dialogo.ShowDialog(this) == true)
            AgregarImagenes(dialogo.FileNames, null);
    }

    // La primera imagen va donde se soltó (si se arrastró); las demás, a continuación.
    private void AgregarImagenes(IReadOnlyList<string> archivos, Point? posicion)
    {
        if (_activa is not { Carpeta: { } carpeta } pestana)
        {
            Avisar(AvisoSinGuardar);
            return;
        }
        foreach (var archivo in archivos.Where(Imagenes.EsImagen))
        {
            try
            {
                EnviarImagen(pestana, Imagenes.Incorporar(carpeta, archivo), Path.GetFileNameWithoutExtension(archivo), posicion);
                posicion = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Avisar($"No se pudo agregar la imagen:\n{ex.Message}");
            }
        }
    }

    // Devuelve true si el portapapeles tenía una imagen (y se atendió); false para que la página pegue lo demás.
    private bool PegarImagen()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var archivos = Clipboard.GetFileDropList().Cast<string>().Where(Imagenes.EsImagen).ToList();
                if (archivos.Count == 0)
                    return false;
                AgregarImagenes(archivos, null);
                return true;
            }
            // Solo la imagen sola (una captura). Si viene con texto o HTML (copiada de una página),
            // la pega el editor, que conserva el enlace original.
            if (!Clipboard.ContainsImage() || Clipboard.ContainsText() || Clipboard.GetImage() is not { } imagen)
                return false;
            if (_activa is not { Carpeta: { } carpeta } pestana)
            {
                Avisar(AvisoSinGuardar);
                return true;
            }
            EnviarImagen(pestana, Imagenes.IncorporarPortapapeles(carpeta, imagen), "imagen pegada", null);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            Avisar($"No se pudo pegar la imagen:\n{ex.Message}");
            return true;
        }
    }

    private void EnviarImagen(Pestana pestana, string ruta, string alt, Point? posicion)
    {
        // Las coordenadas de la página son píxeles CSS: los de WPF divididos por el zoom de la vista.
        var zoom = VisorPro.ZoomFactor > 0 ? VisorPro.ZoomFactor : 1;
        EnviarPro(new { tipo = "insertarImagen", id = pestana.Id, ruta, alt, x = posicion?.X / zoom, y = posicion?.Y / zoom });
        RefrescarArbol();
    }

    // Ejecuta una acción después de traer lo pendiente del editor Pro.
    private async void TrasSincronizar(Action accion)
    {
        await SincronizarProAsync();
        accion();
    }

    // Lleva el editor y la vista previa a una línea del documento (base 0).
    private void IrALinea(int linea)
    {
        if (_activa is null)
            return;
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
        else if (destino.Host.EndsWith(".mdcodex", StringComparison.OrdinalIgnoreCase))
            local = RutaLocal(destino);

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

    // ---------- Pestañas ----------

    // Abre el archivo en una pestaña, o activa la que ya lo tiene abierto.
    // Una pestaña temporal (clic en el Explorador) reemplaza a la temporal anterior si no tiene cambios.
    private void AbrirArchivo(string ruta, bool temporal = false)
    {
        var completa = Path.GetFullPath(ruta);
        var existente = _pestanas.FirstOrDefault(p => string.Equals(p.Ruta, completa, StringComparison.OrdinalIgnoreCase));
        if (existente is not null)
        {
            if (!temporal)
                existente.EsTemporal = false;
            Activar(existente);
            RegistrarArchivoReciente(completa);
            return;
        }

        Pestana nueva;
        try
        {
            nueva = Pestana.Abrir(completa);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Avisar($"No se pudo abrir el archivo:\n{ex.Message}");
            return;
        }
        nueva.EsTemporal = temporal;

        // También se reemplaza un "Sin título" recién creado que sigue vacío.
        var reemplazable = (temporal ? _pestanas.FirstOrDefault(p => p.EsTemporal && !p.Modificado) : null)
            ?? (_activa is { Ruta: null, Modificado: false, Documento.TextLength: 0 } ? _activa : null);
        Agregar(nueva, reemplazable);
        RegistrarArchivoReciente(completa);

    }

    private void Agregar(Pestana nueva, Pestana? reemplazar = null)
    {
        nueva.CambioEnDisco += AlCambiarEnDisco;
        nueva.PropertyChanged += (s, e) =>
        {
            if (s == _activa && e.PropertyName is nameof(Pestana.Modificado) or nameof(Pestana.Nombre))
                ActualizarTitulo();
        };

        var indice = reemplazar is not null ? _pestanas.IndexOf(reemplazar)
            : _activa is not null ? _pestanas.IndexOf(_activa) + 1
            : _pestanas.Count;
        _pestanas.Insert(indice, nueva);
        if (reemplazar is not null)
        {
            _pestanas.Remove(reemplazar);
            reemplazar.Dispose();
        }
        Activar(nueva);
    }

    private void Activar(Pestana? pestana)
    {
        if (_activa is not null)
        {
            if (_activa == pestana)
                return;
            if (Config.GuardadoAutomatico == GuardadoAutomatico.AlCambiar)
                GuardarAutomatico(_activa);
            _activa.Cursor = Editor.CaretOffset;
            _activa.ScrollEditor = Editor.VerticalOffset;
            _activa.EsActiva = false;
        }

        _activa = pestana;
        _restaurarScrollVista = true;
        if (pestana is not null)
        {
            pestana.EsActiva = true;
            Editor.Document = pestana.Documento;
            Editor.CaretOffset = Math.Min(pestana.Cursor, pestana.Documento.TextLength);
            var scroll = pestana.ScrollEditor;
            Dispatcher.BeginInvoke(() => Editor.ScrollToVerticalOffset(scroll), DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(() =>
                (BarraPestanas.ItemContainerGenerator.ContainerFromItem(pestana) as FrameworkElement)?.BringIntoView(),
                DispatcherPriority.Loaded);
        }
        else
        {
            Editor.Document = _sinDocumento;
        }

        AplicarDisposicion();
        RevelarActual();
        ActualizarTitulo();
        ActualizarPosicion();
        ActualizarCodificacion();
        ActualizarDocumento();
        if (pestana is not null && _modo is Modo.Dividido or Modo.Editor)
            Dispatcher.BeginInvoke(() => Editor.TextArea.Focus(), DispatcherPriority.Input);
        if (pestana is not null && _modo == Modo.Pro)
            _ = MostrarProAsync();
        CommandManager.InvalidateRequerySuggested();
    }

    private void CerrarPestanas(IEnumerable<Pestana> cuales)
    {
        var lista = cuales.ToList();
        TrasSincronizar(() => CerrarPestanasAhora(lista));
    }

    private void CerrarPestanasAhora(List<Pestana> cerrar)
    {
        if (cerrar.Count == 0 || !ConfirmarDescartar(cerrar))
            return;

        var indiceActiva = _activa is null ? -1 : _pestanas.IndexOf(_activa);
        foreach (var p in cerrar)
        {
            _pestanas.Remove(p);
            p.Dispose();
        }

        if (_activa is not null && !_pestanas.Contains(_activa))
        {
            _activa = null; // ya no existe: no hay que guardar su posición
            Activar(_pestanas.Count == 0 ? null : _pestanas[Math.Clamp(indiceActiva, 0, _pestanas.Count - 1)]);
        }
    }

    private void Ciclar(int direccion)
    {
        if (_activa is null || _pestanas.Count < 2)
            return;
        var indice = (_pestanas.IndexOf(_activa) + direccion + _pestanas.Count) % _pestanas.Count;
        Activar(_pestanas[indice]);
    }

    // ---------- Archivos ----------

    private bool Guardar(Pestana p) => p.Ruta is null ? GuardarComo(p) : Escribir(p, p.Ruta);

    // Guardado automático: en silencio y solo si el documento ya tiene archivo y cambió. En el modo por
    // bloques, primero se trae lo último escrito ahí.
    private void GuardarAutomatico(Pestana? p)
    {
        if (p?.Ruta is null || !_pestanas.Contains(p))
            return;
        TrasSincronizar(() =>
        {
            if (p.Modificado && p.Ruta is { } ruta && _pestanas.Contains(p))
                Escribir(p, ruta);
        });
    }

    private bool GuardarComo(Pestana p)
    {
        var dialogo = new SaveFileDialog
        {
            Title = "Guardar como",
            Filter = "Markdown (*.md)|*.md|Texto (*.txt)|*.txt|Todos los archivos (*.*)|*.*",
            DefaultExt = ".md",
            FileName = p.Ruta is null ? $"{p.Nombre}.md" : p.Nombre,
        };
        if ((p.Carpeta ?? _carpetas.FirstOrDefault()) is { } carpetaInicial)
            dialogo.InitialDirectory = carpetaInicial;

        if (dialogo.ShowDialog(this) != true || !Escribir(p, dialogo.FileName))
            return false;

        RegistrarArchivoReciente(p.Ruta!);
        RefrescarArbol();
        RevelarActual();
        ActualizarDocumento();
        return true;
    }

    private bool Escribir(Pestana p, string ruta)
    {
        try
        {
            p.Escribir(ruta);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Avisar($"No se pudo guardar {p.Nombre}:\n{ex.Message}");
            return false;
        }
    }

    // Pregunta una sola vez por todas las pestañas con cambios. Devuelve false si el usuario cancela.
    private bool ConfirmarDescartar(IReadOnlyCollection<Pestana> pestanas)
    {
        var modificadas = pestanas.Where(p => p.Modificado).ToList();
        if (modificadas.Count == 0)
            return true;

        var pregunta = modificadas.Count == 1
            ? $"¿Quieres guardar los cambios en {modificadas[0].Nombre}?"
            : $"Hay {modificadas.Count} archivos con cambios sin guardar:\n\n{string.Join("\n", modificadas.Select(p => "• " + p.Nombre))}\n\n¿Quieres guardarlos?";
        return MessageBox.Show(this, pregunta, "MD Codex", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning) switch
        {
            MessageBoxResult.Yes => modificadas.All(Guardar),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private void AlCambiarEnDisco(object? sender, EventArgs e)
    {
        if (sender is not Pestana { Ruta: { } ruta } p || !_pestanas.Contains(p))
            return;

        string texto;
        try
        {
            (texto, _) = Pestana.LeerTexto(ruta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // borrado o bloqueado: se conserva lo que hay en el editor
        }

        // Incluye nuestro propio guardado, que también dispara el aviso.
        if (texto == p.Documento.Text || texto == p.CambioRechazado)
            return;

        // Se pregunta si hay cambios sin guardar, o siempre si así se configuró.
        var pregunta = p.Modificado
            ? $"{p.Nombre} cambió fuera de MD Codex.\n¿Quieres recargarlo? Perderás los cambios que no guardaste."
            : $"{p.Nombre} cambió fuera de MD Codex.\n¿Quieres recargarlo?";
        if ((p.Modificado || Config.CambioExterno == CambioExterno.Preguntar)
            && MessageBox.Show(this, pregunta, "MD Codex", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            p.CambioRechazado = texto;
            return;
        }

        Recargar(p, texto);
    }

    private void Recargar(Pestana p, string texto)
    {
        var posicion = p == _activa ? Editor.CaretOffset : p.Cursor;
        p.Recargar(texto);
        p.Cursor = Math.Min(posicion, texto.Length);
        if (p == _activa)
            Editor.CaretOffset = p.Cursor;
        if (p == _activa && _modo == Modo.Pro)
            CargarEnPro();
    }

    // ---------- Espacios de trabajo ----------

    // Agrega la carpeta a un espacio de trabajo (por omisión, el de lo seleccionado en el árbol).
    private void AbrirCarpeta(string carpeta, EspacioTrabajo? espacio = null)
    {
        espacio ??= EspacioDe(Arbol.SelectedItem as NodoArchivo);
        carpeta = Path.TrimEndingDirectorySeparator(Path.GetFullPath(carpeta));
        if (!espacio.Carpetas.Contains(carpeta, StringComparer.OrdinalIgnoreCase))
        {
            espacio.Carpetas.Add(carpeta);
            espacio.Expandido = true;
            _recientes.Guardar();
        }
        RefrescarArbol();
        AplicarLateral();
    }

    private void AgregarCarpetas(EspacioTrabajo espacio)
    {
        var dialogo = new OpenFolderDialog { Title = $"Agregar carpetas a \"{espacio.Nombre}\"", Multiselect = true };
        if ((espacio.Carpetas.FirstOrDefault() ?? _carpetas.FirstOrDefault()) is { } carpetaInicial)
            dialogo.InitialDirectory = carpetaInicial;
        if (dialogo.ShowDialog(this) != true)
            return;
        foreach (var carpeta in dialogo.FolderNames)
        {
            AbrirCarpeta(carpeta, espacio);
            RegistrarCarpetaReciente(carpeta);
        }
        MostrarPanel(Panel.Explorador);
    }

    private EspacioTrabajo EspacioDe(NodoArchivo? nodo) => nodo?.EspacioDe ?? _recientes.Espacios[0];

    // Desde el menú contextual del árbol, el nodo donde se abrió; desde un botón, lo seleccionado.
    private NodoArchivo? NodoDe(object sender) => sender is MenuItem ? _nodoMenu : Arbol.SelectedItem as NodoArchivo;

    private bool NombreLibre(string nombre, EspacioTrabajo? salvo = null)
    {
        if (!_recientes.Espacios.Any(e => e != salvo && string.Equals(e.Nombre, nombre, StringComparison.CurrentCultureIgnoreCase)))
            return true;
        Avisar($"Ya hay un espacio de trabajo llamado \"{nombre}\".");
        return false;
    }

    private void NuevoEspacio_Click(object sender, RoutedEventArgs e) =>
        PedirNombre(null, "Nombre del espacio de trabajo", "", nombre =>
        {
            if (!NombreLibre(nombre))
                return;
            // Los nuevos quedan arriba; luego se reordenan arrastrándolos.
            _recientes.Espacios.Insert(0, new EspacioTrabajo { Nombre = nombre, Icono = _iconoElegido! });
            _recientes.Guardar();
            RefrescarArbol();
        }, icono: EspacioTrabajo.IconoPorDefecto);

    private void RenombrarEspacio_Click(object sender, RoutedEventArgs e)
    {
        var espacio = EspacioDe(NodoDe(sender));
        PedirNombre(null, "Nombre del espacio de trabajo", espacio.Nombre, nombre =>
        {
            if (!NombreLibre(nombre, espacio))
                return;
            espacio.Nombre = nombre;
            espacio.Icono = _iconoElegido!;
            _recientes.Guardar();
            RefrescarArbol();
        }, icono: espacio.Icono);
    }

    // Solo quita el espacio de la lista: las carpetas y sus archivos no se tocan.
    private void EliminarEspacio_Click(object sender, RoutedEventArgs e)
    {
        var espacio = EspacioDe(NodoDe(sender));
        if (_recientes.Espacios.Count <= 1 || MessageBox.Show(this,
                $"¿Quieres eliminar el espacio de trabajo \"{espacio.Nombre}\"? Las carpetas y sus archivos no se borran.",
                "MD Codex", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _recientes.Espacios.Remove(espacio);
        _recientes.Guardar();
        RefrescarArbol();
    }

    private void AgregarCarpeta_Click(object sender, RoutedEventArgs e) => AgregarCarpetas(EspacioDe(NodoDe(sender)));

    // Quita del espacio la carpeta que contiene el nodo; no borra nada del disco.
    private void QuitarCarpeta_Click(object sender, RoutedEventArgs e)
    {
        var nodo = NodoDe(sender);
        while (nodo is { EsRaiz: false })
            nodo = nodo.Padre;
        if (nodo is null)
            return;
        nodo.EspacioDe!.Carpetas.RemoveAll(c => string.Equals(c, nodo.Ruta, StringComparison.OrdinalIgnoreCase));
        _recientes.Guardar();
        RefrescarArbol();
        AplicarLateral();
    }

    // ---------- Fila de nombre ----------

    // Íconos que se pueden elegir para un espacio de trabajo (Segoe Fluent Icons); el primero es el de omisión.
    private static readonly string[] IconosEspacio =
    [
        EspacioTrabajo.IconoPorDefecto, "", "", "", "", "", "", "",
        "", "", "", "", "", "", "", "",
    ];

    // Ícono marcado en la fila de nombre, al pedir el de un espacio de trabajo.
    private string? _iconoElegido;

    // Con un espacio de trabajo, un clic fuera también acepta lo escrito; con un archivo, cancela.
    private bool _aceptarAlSalir;

    // Pide un nombre en una fila del panel: Enter acepta, Esc cancela. La etiqueta es opcional; el
    // marcador se ve mientras la caja está vacía. Con un ícono (espacio de trabajo), a la izquierda
    // va el botón para elegirlo.
    private void PedirNombre(string? etiqueta, string marcador, string inicial, Action<string> aceptar, string? detalle = null, string? icono = null)
    {
        _alAceptarNombre = aceptar;
        _aceptarAlSalir = icono is not null;
        EtiquetaNombre.Text = etiqueta;
        EtiquetaNombre.ToolTip = detalle;
        EtiquetaNombre.Visibility = Visible(etiqueta is not null);
        MarcadorNombre.Text = marcador;
        CajaNombre.Text = inicial;

        _iconoElegido = icono;
        BotonElegirIcono.Content = icono;
        BotonElegirIcono.Visibility = Visible(icono is not null);
        SelectorIconos.IsOpen = false;
        if (icono is not null)
        {
            if (PanelIconos.Children.Count == 0)
                foreach (var glifo in IconosEspacio)
                {
                    var boton = new RadioButton { Content = glifo, Tag = glifo, GroupName = "IconoEspacio", Style = (Style)FindResource("AlternarIcono") };
                    boton.Click += (_, _) =>
                    {
                        _iconoElegido = glifo;
                        BotonElegirIcono.Content = glifo;
                        SelectorIconos.IsOpen = false;
                    };
                    PanelIconos.Children.Add(boton);
                }
            foreach (RadioButton boton in PanelIconos.Children)
                boton.IsChecked = (string)boton.Tag == icono;
        }

        FilaNombre.Visibility = Visibility.Visible;
        var aparicion = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        EscalaFilaNombre.BeginAnimation(ScaleTransform.ScaleYProperty, aparicion);
        FilaNombre.BeginAnimation(OpacityProperty, aparicion);
        // Recién mostrada aún no puede recibir el foco: se espera al siguiente pase de diseño.
        Dispatcher.BeginInvoke(() =>
        {
            CajaNombre.Focus();
            CajaNombre.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OcultarNombre()
    {
        _alAceptarNombre = null;
        FilaNombre.Visibility = Visibility.Collapsed;
    }

    private void AceptarNombre()
    {
        var aceptar = _alAceptarNombre;
        var nombre = CajaNombre.Text.Trim();
        OcultarNombre();
        if (nombre.Length > 0)
            aceptar?.Invoke(nombre);
    }

    private void CajaNombre_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AceptarNombre();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OcultarNombre();
        }
    }

    // Tras Enter o Esc ya no hay nada pendiente. Aceptar va diferido: un aviso (nombre repetido) no
    // debe abrirse en pleno cambio de foco.
    private void CajaNombre_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_alAceptarNombre is null)
            return;
        if (_aceptarAlSalir)
            Dispatcher.BeginInvoke(AceptarNombre);
        else
            OcultarNombre();
    }

    // Si ya estaba abierto, el clic en el botón lo cerró antes de llegar aquí (StaysOpen="False").
    private void BotonElegirIcono_Click(object sender, RoutedEventArgs e) => SelectorIconos.IsOpen = true;

    private string? RaizDe(string ruta) => _carpetas.FirstOrDefault(c => Documentos.EstaDentro(ruta, c));

    // Panel Documentos: los abiertos que no están en ningún espacio de trabajo y los recientes, agrupados.
    // Se rehace solo (sin tocar el árbol de espacios) al abrir o cerrar pestañas y al cambiar los recientes.
    private readonly NodoArchivo _documentos = NodoArchivo.DeDocumentos();
    // Cambios que esperan a que el mouse salga del panel (ver ActualizarDocumentos).
    private bool _documentosPendientes;

    private void ActualizarDocumentos()
    {
        // Abrir un documento lo sube en Recientes. Si el panel se rehiciera bajo el mouse, el segundo clic de
        // un doble clic caería en otra fila (u otro documento) y la pestaña quedaría temporal: se espera a que
        // el mouse salga del panel.
        if (ArbolDocumentos.IsMouseOver)
        {
            _documentosPendientes = true;
            RevelarActual();
            return;
        }
        _documentosPendientes = false;
        var abiertos = _pestanas.Where(p => p.Ruta is not null && RaizDe(p.Ruta) is null).Select(p => p.Ruta!);
        _documentos.LlenarDocumentos(abiertos, _recientes.Archivos);
        ArbolDocumentos.ItemsSource ??= _documentos.Hijos;
        DocumentosVacio.Visibility = Visible(_documentos.Hijos.Count == 0);
        RevelarActual();
    }

    // Rearma el árbol (espacios, y debajo sus carpetas) conservando lo expandido; las carpetas recién
    // agregadas se abren.
    private void RefrescarArbol()
    {
        var actuales = Arbol.ItemsSource as IEnumerable<NodoArchivo> ?? [];
        var expandidas = NodoArchivo.Expandidas(actuales);
        expandidas.UnionWith(_carpetas.Except(actuales.SelectMany(n => n.Hijos).Select(n => n.Ruta), StringComparer.OrdinalIgnoreCase));

        var espacios = _recientes.Espacios.Select(NodoArchivo.DeEspacio).ToList();
        foreach (var espacio in espacios)
            NodoArchivo.Expandir(espacio.Hijos, expandidas);
        Arbol.ItemsSource = espacios;
        ActualizarDocumentos();

        var carpetas = _carpetas.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        EstadoCarpeta.Text = carpetas.Count switch
        {
            0 => "Sin carpeta",
            1 => NodoArchivo.NombreDeCarpeta(carpetas[0]),
            _ => $"{carpetas.Count} carpetas",
        };
    }

    // Carpeta donde crear un archivo según el nodo: la carpeta misma, la del archivo, o la primera del
    // espacio. Sin nodo, la del documento activo si está en algún espacio, o la primera carpeta.
    private string? CarpetaDestino(NodoArchivo? nodo) => nodo switch
    {
        { EnDocumentos: true } => null,
        { Espacio: { } espacio } => espacio.Carpetas.FirstOrDefault(),
        { EsCarpeta: true } => nodo.Ruta,
        { } => Path.GetDirectoryName(nodo.Ruta),
        null => _activa?.Carpeta is { } activa && RaizDe(activa) is not null ? activa : _carpetas.FirstOrDefault(),
    };

    private void NuevoArchivo_Click(object sender, RoutedEventArgs e)
    {
        if (CarpetaDestino(NodoDe(sender)) is not { } destino)
            return;
        var raiz = RaizDe(destino);
        var donde = raiz is null
            ? destino
            : Path.Combine(NodoArchivo.NombreDeCarpeta(raiz), Path.GetRelativePath(raiz, destino)).TrimEnd('.', Path.DirectorySeparatorChar);
        PedirNombre($"Nuevo archivo en {donde}", "nombre.md", "", nombre => CrearArchivo(destino, nombre), destino);
    }

    // Crea el archivo vacío (".md" si no se escribe extensión) y lo abre.
    private void CrearArchivo(string destino, string nombre)
    {
        if (nombre.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Avisar("El nombre no puede contener ninguno de estos caracteres: \\ / : * ? \" < > |");
            return;
        }
        if (!Path.HasExtension(nombre))
            nombre += ".md";

        var ruta = Path.Combine(destino, nombre);
        try
        {
            // CreateNew falla si ya existe: nunca se pisa un archivo.
            using (new FileStream(ruta, FileMode.CreateNew)) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Avisar(File.Exists(ruta) ? $"Ya existe un archivo con ese nombre:\n{ruta}" : $"No se pudo crear el archivo:\n{ex.Message}");
            return;
        }
        RefrescarArbol();
        AbrirArchivo(ruta);
    }

    private void RevelarActual()
    {
        if (Arbol.ItemsSource is IEnumerable<NodoArchivo> nodos)
            NodoArchivo.Revelar(nodos, _activa?.Ruta);
        NodoArchivo.Revelar(_documentos.Hijos, _activa?.Ruta);
    }

    // ---------- Barra lateral ----------

    private void MostrarPanel(Panel panel)
    {
        _panel = panel;
        _lateralDesplegado = true;
        AplicarLateral();
    }

    private void AplicarLateral()
    {
        var fija = _recientes.LateralFijo;
        var visible = fija || _lateralDesplegado;
        // Solo la barra fija ocupa la columna; su ancho (ajustable con el divisor) se reusa al desplegarla.
        if (ColumnaLateral.ActualWidth > 0)
            _anchoLateral = ColumnaLateral.ActualWidth;

        ColumnaLateral.Width = new GridLength(fija ? _anchoLateral : 0);
        // Mínimo para que el título y los botones del encabezado no se monten.
        ColumnaLateral.MinWidth = fija ? AnchoMinimoLateral : 0;
        Grid.SetColumnSpan(Lateral, fija ? 1 : 3);
        Lateral.Width = fija ? double.NaN : _anchoLateral;
        Lateral.HorizontalAlignment = fija ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        if (visible != _lateralMostrado)
            DeslizarLateral(visible);
        DivisorLateral.Visibility = Visible(fija);
        BotonFijarLateral.IsChecked = fija;
        BotonFijarLateral.ToolTip = fija
            ? "Barra lateral fija. Clic para ocultarla hasta tocar un ícono (Ctrl+Mayús+B)"
            : "Barra lateral automática. Clic para dejarla siempre abierta (Ctrl+Mayús+B)";
        ActividadExplorador.IsChecked = visible && _panel == Panel.Explorador;
        ActividadEsquema.IsChecked = visible && _panel == Panel.Esquema;
        ActividadDocumentos.IsChecked = visible && _panel == Panel.Documentos;

        var explorador = _panel == Panel.Explorador;
        var esquema = _panel == Panel.Esquema;
        TituloLateral.Text = _panel switch
        {
            Panel.Documentos => "DOCUMENTOS",
            Panel.Explorador => "ESPACIOS DE TRABAJO",
            _ => "ESQUEMA",
        };
        AccionesExplorador.Visibility = Visible(explorador);
        PanelExplorador.Visibility = Visible(explorador);
        PanelDocumentos.Visibility = Visible(_panel == Panel.Documentos);
        ListaEsquema.Visibility = Visible(esquema && _titulos.Count > 0);
        EsquemaVacio.Visibility = Visible(esquema && _titulos.Count == 0);
        EsquemaVacio.Text = _activa is null ? "No hay ningún documento abierto." : "El documento no tiene títulos.";
    }

    // Entra y sale deslizándose por la izquierda, por debajo de la barra de actividad.
    // Antes de cargar la ventana, sin animación.
    private void DeslizarLateral(bool mostrar)
    {
        _lateralMostrado = mostrar;
        // Mientras sale no debe tapar los clics al área de trabajo.
        Lateral.IsHitTestVisible = mostrar;
        if (!IsLoaded)
        {
            Lateral.Visibility = Visible(mostrar);
            return;
        }

        var oculta = -_anchoLateral;
        var deslizamiento = new DoubleAnimation(mostrar ? 0 : oculta, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (mostrar && Lateral.Visibility != Visibility.Visible)
        {
            deslizamiento.From = oculta;
            Lateral.Visibility = Visibility.Visible;
        }
        else if (!mostrar)
        {
            deslizamiento.Completed += (_, _) =>
            {
                if (!_lateralMostrado)
                    Lateral.Visibility = Visibility.Collapsed;
            };
        }
        DesplazamientoLateral.BeginAnimation(TranslateTransform.XProperty, deslizamiento);
    }

    // ---------- Modos y edición ----------

    private void AplicarModo(Modo modo)
    {
        if (_aplicandoModo)
            return;
        _aplicandoModo = true;
        try
        {
            // Al salir del modo Pro se trae lo último escrito, para que el editor y la vista lo muestren.
            if (_modo == Modo.Pro && modo != Modo.Pro)
                _ = SincronizarProAsync();

            _modo = modo;
            BotonVista.IsChecked = modo == Modo.Vista;
            BotonDividido.IsChecked = modo == Modo.Dividido;
            BotonEditor.IsChecked = modo == Modo.Editor;
            BotonPro.IsChecked = modo == Modo.Pro;
            EstadoModo.Content = modo.ToString();
            AplicarDisposicion();

            if (_activa is not null && modo is Modo.Dividido or Modo.Editor)
                Dispatcher.BeginInvoke(() => Editor.TextArea.Focus(), DispatcherPriority.Input);
            if (_activa is not null && modo == Modo.Pro)
                _ = MostrarProAsync();
            ActualizarDocumento();
            CommandManager.InvalidateRequerySuggested();
        }
        finally
        {
            _aplicandoModo = false;
        }
    }

    // Muestra editor y vista según el modo; sin pestañas solo queda la bienvenida.
    private void AplicarDisposicion()
    {
        var hayDocumento = _activa is not null;
        var conEditor = hayDocumento && _modo is Modo.Dividido or Modo.Editor;
        var conVista = !hayDocumento || _modo is Modo.Vista or Modo.Dividido;
        var conPro = hayDocumento && _modo == Modo.Pro;
        VisorPro.Visibility = Visible(conPro);

        // El editor Pro abarca las tres columnas: alguna tiene que tener ancho.
        ColumnaEditor.Width = conEditor || conPro ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ColumnaVista.Width = conVista ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Editor.Visibility = Visible(conEditor);
        Visor.Visibility = Visible(conVista);
        Divisor.Visibility = Visible(conEditor && conVista);
        DesplazadorPestanas.Visibility = Visible(hayDocumento);
        // Con pestañas, la esquina superior izquierda queda recta para que la primera pestaña calce como en una carpeta.
        Tarjeta.CornerRadius = hayDocumento ? new CornerRadius(0, 8, 8, 8) : new CornerRadius(8);
        RecortarAreaTrabajo();

        EstadoPosicion.Visibility = Visible(conEditor);
        EstadoPalabras.Visibility = Visible(hayDocumento);
        EstadoCodificacion.Visibility = Visible(hayDocumento);
        EstadoFinLinea.Visibility = Visible(hayDocumento);
        EstadoLenguaje.Visibility = Visible(hayDocumento);
    }

    // Border no recorta su contenido: sin esto, el editor y la vista se saldrían por las esquinas redondeadas.
    private void RecortarAreaTrabajo()
    {
        var tamano = new Size(AreaTrabajo.ActualWidth, AreaTrabajo.ActualHeight);
        var r = Tarjeta.CornerRadius;
        var grosor = Tarjeta.BorderThickness.Left;
        AreaTrabajo.Clip = RectanguloRedondeado(tamano, Math.Max(0, r.TopLeft - grosor), Math.Max(0, r.TopRight - grosor),
            Math.Max(0, r.BottomRight - grosor), Math.Max(0, r.BottomLeft - grosor));
    }

    private static Geometry RectanguloRedondeado(Size t, double si, double sd, double id, double ii)
    {
        var figura = new StreamGeometry();
        using (var g = figura.Open())
        {
            g.BeginFigure(new Point(si, 0), isFilled: true, isClosed: true);
            g.LineTo(new Point(t.Width - sd, 0), false, false);
            g.ArcTo(new Point(t.Width, sd), new Size(sd, sd), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(t.Width, t.Height - id), false, false);
            g.ArcTo(new Point(t.Width - id, t.Height), new Size(id, id), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(ii, t.Height), false, false);
            g.ArcTo(new Point(0, t.Height - ii), new Size(ii, ii), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(0, si), false, false);
            g.ArcTo(new Point(si, 0), new Size(si, si), 0, false, SweepDirection.Clockwise, false, false);
        }
        figura.Freeze();
        return figura;
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
        Title = _activa is null ? "MD Codex" : $"{(_activa.Modificado ? "● " : "")}{_activa.Nombre} — MD Codex";
        TituloVentana.ToolTip = _activa?.Ruta;
    }

    private void ActualizarPosicion()
    {
        var cursor = Editor.TextArea.Caret;
        EstadoPosicion.Content = $"Lín. {cursor.Line}, col. {cursor.Column}";
    }

    private void ActualizarCodificacion()
    {
        EstadoCodificacion.Content = _activa?.Codificacion switch
        {
            null => "",
            System.Text.UTF8Encoding utf8 => utf8.GetPreamble().Length > 0 ? "UTF-8 con BOM" : "UTF-8",
            System.Text.UnicodeEncoding { CodePage: 1201 } => "UTF-16 BE",
            System.Text.UnicodeEncoding => "UTF-16 LE",
            var otra => otra.WebName.ToUpperInvariant(),
        };
    }

    // ---------- Ventana y tema ----------

    // Snap Layouts de Windows 11: el menú de diseños aparece cuando la ventana dice que el cursor
    // está sobre "su" botón de maximizar (HTMAXBUTTON). Con la barra de título propia hay que decirlo
    // a mano, y entonces el clic y el resaltado del botón también corren por cuenta de la app.
    private IntPtr GanchoVentana(IntPtr ventana, int mensaje, IntPtr wParam, IntPtr lParam, ref bool atendido)
    {
        switch (mensaje)
        {
            case Nativo.WmNcHitTest:
                var sobre = SobreMaximizar(lParam);
                ResaltarMaximizar(ventana, sobre);
                if (sobre)
                {
                    atendido = true;
                    return Nativo.HtMaxButton;
                }
                break;
            case Nativo.WmNcLButtonDown when wParam == Nativo.HtMaxButton:
                atendido = true; // sin esto, Windows dibuja su propio botón clásico encima
                break;
            case Nativo.WmNcLButtonUp when wParam == Nativo.HtMaxButton:
                atendido = true;
                Maximizar_Click(BotonMaximizar, new RoutedEventArgs());
                break;
            case Nativo.WmNcMouseLeave:
                ResaltarMaximizar(ventana, false);
                break;
        }
        return IntPtr.Zero;
    }

    private bool SobreMaximizar(IntPtr lParam)
    {
        if (!BotonMaximizar.IsVisible)
            return false;
        var (x, y) = Nativo.PuntoDe(lParam);
        var punto = BotonMaximizar.PointFromScreen(new Point(x, y));
        return punto.X >= 0 && punto.Y >= 0 && punto.X < BotonMaximizar.ActualWidth && punto.Y < BotonMaximizar.ActualHeight;
    }

    // WPF no ve el mouse sobre el botón (Windows lo trata como parte del marco), así que el resaltado es manual.
    private bool _maximizarResaltado;
    private void ResaltarMaximizar(IntPtr ventana, bool resaltar)
    {
        if (_maximizarResaltado == resaltar)
            return;
        _maximizarResaltado = resaltar;
        if (resaltar)
        {
            BotonMaximizar.SetResourceReference(BackgroundProperty, "Chrome.Hover");
            Nativo.SeguirSalidaNoCliente(ventana);
        }
        else
        {
            BotonMaximizar.ClearValue(BackgroundProperty);
        }
    }

    // Con WindowChrome, una ventana maximizada se sale del área de trabajo por el grosor del marco,
    // que cambia con la escala del monitor. El margen se mide (ventana contra área de trabajo) para
    // que el contenido calce justo y la barra de estado no quede bajo la barra de tareas.
    private void AjustarMaximizado()
    {
        var maximizada = WindowState == WindowState.Maximized;
        Raiz.Margin = maximizada ? MargenMaximizada() : new Thickness(0);
        BotonMaximizar.Content = maximizada ? "\uE923" : "\uE922";
        BotonMaximizar.ToolTip = maximizada ? "Restaurar" : "Maximizar";
    }

    private Thickness MargenMaximizada()
    {
        var ventana = new WindowInteropHelper(this).Handle;
        var marco = Nativo.RectanguloVentana(ventana);
        var trabajo = Nativo.AreaDeTrabajo(ventana);
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Thickness(
            Math.Max(0, trabajo.Izquierda - marco.Izquierda) / dpi.DpiScaleX,
            Math.Max(0, trabajo.Arriba - marco.Arriba) / dpi.DpiScaleY,
            Math.Max(0, marco.Derecha - trabajo.Derecha) / dpi.DpiScaleX,
            Math.Max(0, marco.Abajo - trabajo.Abajo) / dpi.DpiScaleY);
    }

    // En pantallas chicas el tamaño inicial no cabe: la ventana se achica al área de trabajo
    // (con un margen) y se centra en ella, así la barra de título queda a la vista.
    private void AjustarAPantalla()
    {
        const double margen = 24;
        var trabajo = Nativo.AreaDeTrabajo(new WindowInteropHelper(this).Handle);
        var dpi = VisualTreeHelper.GetDpi(this);
        var ancho = trabajo.Ancho / dpi.DpiScaleX;
        var alto = trabajo.Alto / dpi.DpiScaleY;
        if (Width <= ancho - 2 * margen && Height <= alto - 2 * margen)
            return;
        Width = Math.Max(MinWidth, Math.Min(Width, ancho - 2 * margen));
        Height = Math.Max(MinHeight, Math.Min(Height, alto - 2 * margen));
        Left = trabajo.Izquierda / dpi.DpiScaleX + (ancho - Width) / 2;
        Top = trabajo.Arriba / dpi.DpiScaleY + (alto - Height) / 2;
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
        recursos["Editor.Fondo"] = Tema.Pincel(p.Fondo);
        recursos["Lateral.Fondo"] = Tema.Pincel(p.Lateral);

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
        VisorPro.DefaultBackgroundColor = Visor.DefaultBackgroundColor;
        // La vista y el editor por bloques comparten el perfil de WebView2: basta con uno.
        if (Visor.CoreWebView2 is { } core)
            core.Profile.PreferredColorScheme = EsquemaWeb();
    }

    private static CoreWebView2PreferredColorScheme EsquemaWeb() => Tema.Preferido switch
    {
        TemaPreferido.Claro => CoreWebView2PreferredColorScheme.Light,
        TemaPreferido.Oscuro => CoreWebView2PreferredColorScheme.Dark,
        _ => CoreWebView2PreferredColorScheme.Auto,
    };

    private void AlCambiarPreferencias(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(AplicarTema);
    }

    // ---------- Utilidades ----------

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

    private static bool Contiene(DependencyObject contenedor, object? origen)
    {
        for (var actual = origen as DependencyObject; actual is not null;
             actual = actual is Visual ? VisualTreeHelper.GetParent(actual) : LogicalTreeHelper.GetParent(actual))
            if (actual == contenedor)
                return true;
        return false;
    }

    private static Pestana? PestanaDe(object sender) => (sender as FrameworkElement)?.DataContext as Pestana;

    private void Avisar(string mensaje) =>
        MessageBox.Show(this, mensaje, "MD Codex", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ---------- Eventos ----------

    // Ctrl+Tab se atiende antes de que la navegación por teclado de WPF lo use para mover el foco.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Ciclar(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
            return;
        }

        // Pegar una imagen en el modo Pro: la guarda la app junto al documento (la página no puede escribir en disco).
        if (VisorPro.IsKeyboardFocusWithin && e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && PegarImagen())
        {
            e.Handled = true;
            return;
        }

        // Las vistas web se quedan con las teclas: los atajos de la ventana se atienden aquí antes.
        // Los que no aplican (Ctrl+B o Ctrl+I fuera del editor de texto) siguen hacia la página.
        if (Visor.IsKeyboardFocusWithin || VisorPro.IsKeyboardFocusWithin)
        {
            foreach (var atajo in InputBindings.OfType<KeyBinding>())
            {
                if (atajo.Gesture.Matches(this, e) && atajo.Command is RoutedCommand comando && comando.CanExecute(null, this))
                {
                    comando.Execute(null, this);
                    e.Handled = true;
                    return;
                }
            }
        }
        base.OnPreviewKeyDown(e);
    }

    private void Ventana_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    // Las carpetas arrastradas se abren en el Explorador; los archivos, cada uno en su pestaña.
    // Las imágenes soltadas sobre el editor Pro se insertan en el documento, donde se soltaron.
    private void Ventana_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } rutas)
            return;
        e.Handled = true;
        if (_modo == Modo.Pro && VisorPro.IsVisible && rutas.All(Imagenes.EsImagen))
        {
            var punto = e.GetPosition(VisorPro);
            if (punto.X >= 0 && punto.Y >= 0 && punto.X < VisorPro.ActualWidth && punto.Y < VisorPro.ActualHeight)
            {
                AgregarImagenes(rutas, punto);
                return;
            }
        }
        foreach (var ruta in rutas)
        {
            if (Directory.Exists(ruta))
            {
                AbrirCarpeta(ruta);
                RegistrarCarpetaReciente(Path.GetFullPath(ruta));
            }
            else
            {
                AbrirArchivo(ruta);
            }
        }
    }

    private void Pestana_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (PestanaDe(sender) is not { } p)
            return;
        if (e.ChangedButton == MouseButton.Middle)
        {
            CerrarPestanas([p]);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            Activar(p);
            if (e.ClickCount == 2)
                p.EsTemporal = false;
        }
    }

    private void CerrarPestana_Click(object sender, RoutedEventArgs e)
    {
        if (PestanaDe(sender) is { } p)
            CerrarPestanas([p]);
    }

    private void DesplazadorPestanas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        DesplazadorPestanas.ScrollToHorizontalOffset(DesplazadorPestanas.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void MenuCerrar_Click(object sender, RoutedEventArgs e)
    {
        if (PestanaDe(sender) is { } p)
            CerrarPestanas([p]);
    }

    private void MenuCerrarOtras_Click(object sender, RoutedEventArgs e)
    {
        if (PestanaDe(sender) is { } p)
            CerrarPestanas(_pestanas.Where(x => x != p));
    }

    private void MenuCerrarDerecha_Click(object sender, RoutedEventArgs e)
    {
        if (PestanaDe(sender) is { } p)
            CerrarPestanas(_pestanas.Skip(_pestanas.IndexOf(p) + 1));
    }

    private void MenuCerrarTodas_Click(object sender, RoutedEventArgs e) => CerrarPestanas(_pestanas);

    private void MenuCopiarRuta_Click(object sender, RoutedEventArgs e)
    {
        if (PestanaDe(sender)?.Ruta is { } ruta)
            Clipboard.SetText(ruta);
    }

    private void MenuMostrarEnCarpeta_Click(object sender, RoutedEventArgs e)
    {
        if (PestanaDe(sender)?.Ruta is { } ruta)
            Process.Start("explorer.exe", $"/select,\"{ruta}\"");
    }

    private void Arbol_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // La flecha de expandir ya hace lo suyo.
        if (Ancestro<ToggleButton>(e.OriginalSource) is not null)
            return;
        if (Ancestro<TreeViewItem>(e.OriginalSource)?.DataContext is not NodoArchivo nodo)
            return;

        if (nodo.EsCarpeta || nodo.EsEspacio)
            nodo.Expandido = !nodo.Expandido;
        else
            AbrirArchivo(nodo.Ruta, temporal: true);
    }

    // Doble clic en el Explorador fija la pestaña temporal.
    private void Arbol_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Ancestro<TreeViewItem>(e.OriginalSource)?.DataContext is NodoArchivo { EsCarpeta: false, EsEspacio: false } nodo)
            AbrirArchivo(nodo.Ruta);
    }

    private void Arbol_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ((TreeView)sender).SelectedItem is NodoArchivo { EsCarpeta: false, EsEspacio: false } nodo)
        {
            e.Handled = true;
            AbrirArchivo(nodo.Ruta);
        }
    }

    private void ListaEsquema_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Ancestro<ListBoxItem>(e.OriginalSource)?.DataContext is not Titulo titulo)
            return;
        if (_modo == Modo.Pro && _proListo)
            EnviarPro(new { tipo = "titulo", indice = _titulos.ToList().IndexOf(titulo) });
        else
            IrALinea(titulo.Linea);
    }

    private void Actividad_Click(object sender, RoutedEventArgs e)
    {
        var panel = Enum.Parse<Panel>((string)((FrameworkElement)sender).Tag);
        // Automática: tocar el panel que ya está desplegado la vuelve a ocultar. Fija: queda abierta.
        if (!_recientes.LateralFijo && _lateralDesplegado && _panel == panel)
        {
            _lateralDesplegado = false;
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

    private void EstadoModo_Click(object sender, RoutedEventArgs e) => AplicarModo((Modo)(((int)_modo + 1) % 4));

    private void RefrescarArbol_Click(object sender, RoutedEventArgs e) => RefrescarArbol();

    private void AbrirMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } boton)
            return;
        // El menú vive fuera del árbol de la ventana: sin destino, sus comandos no encuentran quién los atienda.
        // Desde el editor suben hasta la ventana, así sirven tanto Deshacer/Copiar como los comandos propios.
        // Si el editor no está visible (o nunca se mostró, y aún no es parte de la ventana), el destino es la ventana.
        IInputElement destino = Editor.IsVisible ? Editor.TextArea : this;
        foreach (var item in menu.Items.OfType<MenuItem>())
            item.CommandTarget = destino;
        if (menu.Items.Contains(MenuRecientes))
            RellenarRecientes();
        menu.PlacementTarget = boton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void AcercaDe_Click(object sender, RoutedEventArgs e) => new AcercaDe { Owner = this }.ShowDialog();

    private void Repositorio_Click(object sender, RoutedEventArgs e) => Marca.Abrir(Marca.Repositorio);

    private void InformarProblema_Click(object sender, RoutedEventArgs e) => Marca.Abrir(Marca.NuevoProblema);

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
        Agregar(Pestana.Nueva());
        if (_modo == Modo.Vista)
            AplicarModo(Modo.Dividido);
    }

    private void Abrir_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dialogo = new OpenFileDialog
        {
            Title = "Abrir archivo Markdown",
            Filter = "Markdown (*.md;*.markdown)|*.md;*.markdown;*.mdown;*.mkd|Texto (*.txt)|*.txt|Todos los archivos (*.*)|*.*",
            Multiselect = true,
        };
        if ((_activa?.Carpeta ?? _carpetas.FirstOrDefault()) is { } carpetaInicial)
            dialogo.InitialDirectory = carpetaInicial;

        if (dialogo.ShowDialog(this) == true)
            foreach (var archivo in dialogo.FileNames)
                AbrirArchivo(archivo);
    }

    private void AbrirCarpeta_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        AgregarCarpetas(EspacioDe(Arbol.SelectedItem as NodoArchivo));
    }

    // El clic derecho también selecciona el elemento; el menú actúa sobre él (o, en un hueco, sin nodo).
    private void Arbol_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = Ancestro<TreeViewItem>(e.OriginalSource);
        _nodoMenu = item?.DataContext as NodoArchivo;
        if (item is not null)
            item.IsSelected = true;
    }

    private void Arbol_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Documentos no es un lugar del disco ni un espacio de trabajo: sin menú.
        if (_nodoMenu is { EnDocumentos: true })
        {
            e.Handled = true;
            return;
        }
        MenuNuevoArchivoArbol.IsEnabled = CarpetaDestino(_nodoMenu) is not null;
        MenuQuitarCarpeta.IsEnabled = _nodoMenu is { EsEspacio: false };
        MenuRenombrarEspacio.IsEnabled = _nodoMenu is not null;
        MenuEliminarEspacio.IsEnabled = _nodoMenu is not null && _recientes.Espacios.Count > 1;
    }

    private void Arbol_Expandido(object sender, RoutedEventArgs e) => AnimarHijos(e.OriginalSource, expandir: true);

    private void Arbol_Plegado(object sender, RoutedEventArgs e) => AnimarHijos(e.OriginalSource, expandir: false);

    // Los hijos se descubren o se recogen animando la altura (recortada, sin deformar el texto): empieza
    // rápido y termina lento. La opacidad va más pausada, así los elementos aparecen y se van
    // desvanecidos. Solo con el elemento ya cargado: al rearmar el árbol, lo expandido aparece tal cual.
    // Las animaciones terminan en Stop: al acabar manda de nuevo la plantilla (altura automática, y
    // los hijos visibles u ocultos según IsExpanded).
    private static void AnimarHijos(object origen, bool expandir)
    {
        if (origen is not TreeViewItem { IsLoaded: true } item || item.Template.FindName("Hijos", item) is not ItemsPresenter hijos)
            return;
        var suave = new CubicEase { EasingMode = EasingMode.EaseOut };
        // Se lee antes de animar nada: iniciar una animación puede recalcular el diseño, y la plantilla
        // ya los da por ocultos (altura 0).
        var alto = hijos.ActualHeight;

        if (!expandir)
        {
            var plegado = TimeSpan.FromMilliseconds(220);
            // La plantilla los oculta al instante: se mantienen visibles mientras dura la animación.
            var mantener = new ObjectAnimationUsingKeyFrames { Duration = plegado, FillBehavior = FillBehavior.Stop };
            mantener.KeyFrames.Add(new DiscreteObjectKeyFrame(Visibility.Visible, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            hijos.BeginAnimation(VisibilityProperty, mantener);
            hijos.BeginAnimation(HeightProperty, new DoubleAnimation(alto, 0, plegado) { EasingFunction = suave, FillBehavior = FillBehavior.Stop });
            hijos.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, plegado) { EasingFunction = suave, FillBehavior = FillBehavior.Stop });
            return;
        }

        // Hasta medir la altura final (los hijos recién cargados aún no tienen contenedores), se dejan
        // en cero y transparentes para que no asomen un instante.
        // Con origen explícito: sin él, se partiría de la altura automática (NaN) y WPF lanza una excepción.
        hijos.BeginAnimation(HeightProperty, new DoubleAnimation(0, 0, TimeSpan.Zero));
        hijos.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0, TimeSpan.Zero));
        hijos.Dispatcher.BeginInvoke(() =>
        {
            if (VisualTreeHelper.GetChildrenCount(hijos) == 0 || VisualTreeHelper.GetChild(hijos, 0) is not UIElement panel)
            {
                // Sin panel que medir, sin animación: se quita lo que los dejaba en cero.
                hijos.BeginAnimation(HeightProperty, null);
                hijos.BeginAnimation(OpacityProperty, null);
                return;
            }
            panel.Measure(new Size(hijos.ActualWidth, double.PositiveInfinity));
            hijos.BeginAnimation(HeightProperty, new DoubleAnimation(0, panel.DesiredSize.Height, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = suave,
                FillBehavior = FillBehavior.Stop,
            });
            hijos.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop,
            });
        }, DispatcherPriority.Loaded);
    }

    // Se arrastra la fila (no la flecha) de un espacio de trabajo, para reordenarlo, o de un archivo,
    // para moverlo a otra carpeta.
    private void Arbol_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _inicioArrastre = e.GetPosition(Arbol);
        _nodoArrastrado = Ancestro<ToggleButton>(e.OriginalSource) is null
            && Ancestro<TreeViewItem>(e.OriginalSource)?.DataContext is NodoArchivo nodo && (nodo.EsEspacio || !nodo.EsCarpeta)
            ? nodo : null;
    }

    private void Arbol_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_nodoArrastrado is not { } nodo || e.LeftButton != MouseButtonState.Pressed)
            return;
        var distancia = e.GetPosition(Arbol) - _inicioArrastre;
        if (Math.Abs(distancia.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(distancia.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _nodoArrastrado = null;
        var datos = nodo.Espacio is { } espacio ? new DataObject(typeof(EspacioTrabajo), espacio) : new DataObject(typeof(NodoArchivo), nodo);
        DragDrop.DoDragDrop(Arbol, datos, DragDropEffects.Move);
    }

    // Carpeta donde caería un archivo soltado sobre esa fila: la carpeta misma o la del archivo.
    private static string? CarpetaBajo(DragEventArgs e) => Ancestro<TreeViewItem>(e.OriginalSource)?.DataContext switch
    {
        NodoArchivo { EsEspacio: true } or NodoArchivo { EnDocumentos: true } or null => null,
        NodoArchivo { EsCarpeta: true } carpeta => carpeta.Ruta,
        NodoArchivo archivo => Path.GetDirectoryName(archivo.Ruta),
        _ => null,
    };

    private void Arbol_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(NodoArchivo)) is NodoArchivo archivo)
            e.Effects = CarpetaBajo(e) is { } carpeta && !string.Equals(carpeta, Path.GetDirectoryName(archivo.Ruta), StringComparison.OrdinalIgnoreCase)
                ? DragDropEffects.Move : DragDropEffects.None;
        else if (e.Data.GetDataPresent(typeof(EspacioTrabajo)))
            e.Effects = Ancestro<TreeViewItem>(e.OriginalSource) is null ? DragDropEffects.None : DragDropEffects.Move;
        else
            return;
        e.Handled = true;
    }

    private void Arbol_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(NodoArchivo)) is NodoArchivo archivo)
        {
            e.Handled = true;
            if (CarpetaBajo(e) is { } carpeta)
                MoverArchivo(archivo.Ruta, carpeta);
            return;
        }

        // Un espacio soltado sobre cualquier fila de otro: hacia arriba queda antes de él, hacia abajo, después.
        if (e.Data.GetData(typeof(EspacioTrabajo)) is not EspacioTrabajo espacio
            || Ancestro<TreeViewItem>(e.OriginalSource)?.DataContext is not NodoArchivo { EspacioDe: { } destino }
            || destino == espacio)
            return;
        e.Handled = true;
        var indice = _recientes.Espacios.IndexOf(destino);
        _recientes.Espacios.Remove(espacio);
        _recientes.Espacios.Insert(indice, espacio);
        _recientes.Guardar();
        RefrescarArbol();
    }

    // Mueve el archivo en el disco (nunca pisa uno que ya exista) y la pestaña que lo tenga abierto lo sigue.
    private void MoverArchivo(string origen, string carpeta)
    {
        var destino = Path.Combine(carpeta, Path.GetFileName(origen));
        if (string.Equals(origen, destino, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            File.Move(origen, destino, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Avisar(File.Exists(destino) ? $"Ya existe un archivo con ese nombre en la carpeta de destino:\n{destino}" : $"No se pudo mover el archivo:\n{ex.Message}");
            return;
        }

        if (_pestanas.FirstOrDefault(p => string.Equals(p.Ruta, origen, StringComparison.OrdinalIgnoreCase)) is { } pestana)
        {
            pestana.Mover(destino);
            if (pestana == _activa)
                ActualizarDocumento(); // las rutas relativas de la vista previa cambian con la carpeta
        }
        RefrescarArbol();
    }

    private void Guardar_Executed(object sender, ExecutedRoutedEventArgs e) => TrasSincronizar(() =>
    {
        if (_activa is not null)
            Guardar(_activa);
    });

    private void GuardarComo_Executed(object sender, ExecutedRoutedEventArgs e) => TrasSincronizar(() =>
    {
        if (_activa is not null)
            GuardarComo(_activa);
    });

    private void GuardarTodo_Executed(object sender, ExecutedRoutedEventArgs e) => TrasSincronizar(() =>
    {
        foreach (var p in _pestanas.Where(p => p.Modificado).ToList())
            if (!Guardar(p))
                return;
    });

    private void CerrarPestana_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_activa is not null)
            CerrarPestanas([_activa]);
    }

    private void CerrarTodas_Executed(object sender, ExecutedRoutedEventArgs e) => CerrarPestanas(_pestanas);

    private void PestanaSiguiente_Executed(object sender, ExecutedRoutedEventArgs e) => Ciclar(1);

    private void PestanaAnterior_Executed(object sender, ExecutedRoutedEventArgs e) => Ciclar(-1);

    private void Recargar_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_activa is not { Ruta: { } ruta } p)
            return;
        if (p.Modificado && MessageBox.Show(this, "¿Quieres descartar los cambios y recargar el archivo desde el disco?",
                "MD Codex", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var (texto, codificacion) = Pestana.LeerTexto(ruta);
            p.Codificacion = codificacion;
            Recargar(p, texto);
            ActualizarCodificacion();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Avisar($"No se pudo leer el archivo:\n{ex.Message}");
        }
    }

    private void HayPestana_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _activa is not null;

    private void HayArchivo_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _activa?.Ruta is not null;

    // Negrita y cursiva del editor de texto; en el modo Pro, Ctrl+B y Ctrl+I los atiende el propio editor web.
    private void EditorVisible_CanExecute(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = _activa is not null && _modo is Modo.Dividido or Modo.Editor;

    private void ModoVista_Executed(object sender, ExecutedRoutedEventArgs e) => AplicarModo(Modo.Vista);

    private void ModoDividido_Executed(object sender, ExecutedRoutedEventArgs e) => AplicarModo(Modo.Dividido);

    private void ModoEditor_Executed(object sender, ExecutedRoutedEventArgs e) => AplicarModo(Modo.Editor);

    private void ModoPro_Executed(object sender, ExecutedRoutedEventArgs e) => AplicarModo(Modo.Pro);

    private void VerExplorador_Executed(object sender, ExecutedRoutedEventArgs e) => MostrarPanel(Panel.Explorador);

    private void VerEsquema_Executed(object sender, ExecutedRoutedEventArgs e) => MostrarPanel(Panel.Esquema);

    private void VerDocumentos_Executed(object sender, ExecutedRoutedEventArgs e) => MostrarPanel(Panel.Documentos);

    // Ctrl+, o el engranaje: abre la ventana de configuración, o la cierra si ya está abierta.
    private void VerConfiguracion_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (CapaConfiguracion.IsVisible)
            CerrarConfiguracion();
        else
            AbrirConfiguracion();
    }

    // ---------- Configuración ----------

    private void AbrirConfiguracion()
    {
        MostrarConfiguracion();
        AplicarCarpetaImagenes(); // muestra dónde quedan las imágenes (y crea la carpeta fija si falta)
        CategoriaInicial.IsChecked = true;
        CapaConfiguracion.Visibility = Visibility.Visible;
        ActividadConfiguracion.IsChecked = true;
        Dispatcher.BeginInvoke(() => CategoriaInicial.Focus(), DispatcherPriority.Input);
    }

    private void CerrarConfiguracion()
    {
        // Al ocultarse, la caja de la carpeta no pierde el foco por sí sola: lo escrito se aplica aquí.
        AplicarCarpetaImagenes();
        CapaConfiguracion.Visibility = Visibility.Collapsed;
        ActividadConfiguracion.IsChecked = false;
        if (_activa is not null && _modo is Modo.Dividido or Modo.Editor)
            Editor.TextArea.Focus();
    }

    private void CerrarConfiguracion_Click(object sender, RoutedEventArgs e) => CerrarConfiguracion();

    private void FondoConfiguracion_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CerrarConfiguracion();

    // Esc cierra (si un select está desplegado, Esc primero lo pliega y no llega hasta aquí).
    private void CapaConfiguracion_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        CerrarConfiguracion();
    }

    private void Categoria_Checked(object sender, RoutedEventArgs e)
    {
        var categoria = (RadioButton)sender;
        foreach (var seccion in new[] { SeccionGeneral, SeccionEditor, SeccionVista, SeccionBloques, SeccionImagenes })
            seccion.Visibility = Visible(seccion.Name == (string)categoria.Tag);
        TituloCategoria.Text = ((TextBlock)((StackPanel)categoria.Content).Children[1]).Text;
    }

    // Marca los controles de la ventana con lo guardado.
    private void MostrarConfiguracion()
    {
        _mostrandoConfiguracion = true;
        Seleccionar(SelectorTema, Config.Tema);
        Seleccionar(SelectorModoInicial, Config.ModoInicial);
        Seleccionar(SelectorCambioExterno, Config.CambioExterno);
        Seleccionar(SelectorGuardado, Config.GuardadoAutomatico);
        Seleccionar(SelectorAnchoVista, Config.AnchoVista);
        Seleccionar(SelectorAnchoBloques, Config.AnchoBloques);
        OpcionReabrirSesion.IsChecked = Config.ReabrirSesion;
        OpcionBuscarActualizaciones.IsChecked = Config.BuscarActualizaciones;
        OpcionAjustarLineas.IsChecked = Config.AjustarLineas;
        OpcionNumerosLinea.IsChecked = Config.NumerosLinea;
        OpcionResaltarLinea.IsChecked = Config.ResaltarLinea;
        OpcionSincronizarDesplazamiento.IsChecked = Config.SincronizarDesplazamiento;
        CajaCarpetaImagenes.Text = Config.CarpetaImagenes;
        ResultadoBusqueda.Text = "";
        _mostrandoConfiguracion = false;
    }

    private void Interruptor_Click(object sender, RoutedEventArgs e)
    {
        var activo = ((CheckBox)sender).IsChecked == true;
        switch (((FrameworkElement)sender).Name)
        {
            case nameof(OpcionReabrirSesion): Config.ReabrirSesion = activo; break;
            case nameof(OpcionBuscarActualizaciones): Config.BuscarActualizaciones = activo; break;
            case nameof(OpcionAjustarLineas): Config.AjustarLineas = activo; break;
            case nameof(OpcionNumerosLinea): Config.NumerosLinea = activo; break;
            case nameof(OpcionResaltarLinea): Config.ResaltarLinea = activo; break;
            case nameof(OpcionSincronizarDesplazamiento):
                Config.SincronizarDesplazamiento = activo;
                SincronizarScroll();
                break;
        }
        _recientes.Guardar();
        AplicarEditor();
    }

    // Cada opción de un select lleva en Tag el nombre del valor (el del enum de Configuracion).
    private static void Seleccionar<T>(ComboBox selector, T valor) where T : struct, Enum =>
        selector.SelectedItem = selector.Items.OfType<ComboBoxItem>().First(o => (string)o.Tag == valor.ToString());

    private void Selector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selector = (ComboBox)sender;
        if (_mostrandoConfiguracion || selector.SelectedItem is not ComboBoxItem { Tag: string valor })
            return;
        switch (selector.Name)
        {
            case nameof(SelectorTema):
                Config.Tema = Tema.Preferido = Enum.Parse<TemaPreferido>(valor);
                AplicarTema();
                break;
            case nameof(SelectorModoInicial):
                Config.ModoInicial = Enum.Parse<ModoInicial>(valor);
                break;
            case nameof(SelectorCambioExterno):
                Config.CambioExterno = Enum.Parse<CambioExterno>(valor);
                break;
            case nameof(SelectorGuardado):
                Config.GuardadoAutomatico = Enum.Parse<GuardadoAutomatico>(valor);
                _guardadoDiferido.Stop();
                break;
            case nameof(SelectorAnchoVista):
                Config.AnchoVista = Enum.Parse<Ancho>(valor);
                EnviarAncho();
                break;
            case nameof(SelectorAnchoBloques):
                Config.AnchoBloques = Enum.Parse<Ancho>(valor);
                EnviarAnchoPro();
                break;
        }
        _recientes.Guardar();
    }

    private void CajaCarpetaImagenes_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        AplicarCarpetaImagenes();
    }

    private void CajaCarpetaImagenes_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => AplicarCarpetaImagenes();

    // Se guarda ya validada (si no sirve, vuelve a la predeterminada y la caja lo muestra). Una carpeta fija
    // que no existe se crea ahora; una por documento, al pegar la primera imagen.
    private void AplicarCarpetaImagenes()
    {
        Config.CarpetaImagenes = Imagenes.Carpeta = Configuracion.CarpetaValida(CajaCarpetaImagenes.Text);
        CajaCarpetaImagenes.Text = Config.CarpetaImagenes;
        _recientes.Guardar();
        string? error = null;
        if (Imagenes.CarpetaFija is { } fija)
        {
            try
            {
                Directory.CreateDirectory(fija);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"No se pudo crear la carpeta: {ex.Message}";
            }
        }
        DetalleCarpetaImagenes.Text = error ?? (Imagenes.CarpetaFija is not null
            ? "Una sola carpeta para las imágenes de todos los documentos."
            : _activa?.Carpeta is { } documento
                ? $"Una carpeta junto a cada documento. Para el documento abierto: {Imagenes.CarpetaPara(documento)}"
                : "Una carpeta con ese nombre junto a cada documento.");
    }

    private void SeleccionarCarpetaImagenes_Click(object sender, RoutedEventArgs e)
    {
        var dialogo = new OpenFolderDialog { Title = "Carpeta de las imágenes" };
        if (Imagenes.CarpetaFija is { } actual && Directory.Exists(actual))
            dialogo.InitialDirectory = actual;
        if (dialogo.ShowDialog(this) != true)
            return;
        CajaCarpetaImagenes.Text = dialogo.FolderName;
        AplicarCarpetaImagenes();
    }

    private void CarpetaImagenesPredeterminada_Click(object sender, RoutedEventArgs e)
    {
        CajaCarpetaImagenes.Text = Configuracion.CarpetaImagenesPorDefecto;
        AplicarCarpetaImagenes();
    }

    // Opciones del editor de texto (y su tamaño de letra, que sigue al zoom).
    private const double TamanoEditor = 14;

    private void AplicarEditor()
    {
        Editor.WordWrap = Config.AjustarLineas;
        Editor.ShowLineNumbers = Config.NumerosLinea;
        Editor.Options.HighlightCurrentLine = Config.ResaltarLinea;
        // Al mostrarlos se crea otro margen de números: el aire hasta la línea divisoria va en cada uno.
        foreach (var margen in Editor.TextArea.LeftMargins.OfType<LineNumberMargin>())
            margen.Margin = new Thickness(0, 0, 8, 0);
    }

    // Vista previa y editor por bloques: texto centrado con un ancho cómodo, o todo el ancho.
    private void EnviarAncho()
    {
        if (_vistaLista)
            Enviar(new { tipo = "ancho", completo = Config.AnchoVista == Ancho.Completo });
    }

    private void EnviarAnchoPro()
    {
        if (_proListo)
            EnviarPro(new { tipo = "ancho", completo = Config.AnchoBloques == Ancho.Completo });
    }

    // ---------- Zoom ----------

    // Un solo nivel para el editor, la vista y el editor por bloques; se cambia desde la barra de estado,
    // con Ctrl + / Ctrl - / Ctrl 0 o con Ctrl + rueda. Los pasos son los de los navegadores.
    private static readonly double[] NivelesZoom = [0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3];

    private void CambiarZoom(int direccion)
    {
        var actual = Config.Zoom;
        var nivel = direccion > 0
            ? NivelesZoom.FirstOrDefault(n => n > actual + 0.001, NivelesZoom[^1])
            : NivelesZoom.LastOrDefault(n => n < actual - 0.001, NivelesZoom[0]);
        FijarZoom(nivel);
    }

    private void FijarZoom(double zoom)
    {
        zoom = Math.Clamp(Math.Round(zoom, 2), NivelesZoom[0], NivelesZoom[^1]);
        if (Math.Abs(zoom - Config.Zoom) < 0.001)
            return;
        Config.Zoom = zoom;
        _recientes.Guardar();
        AplicarZoom();
    }

    private void AplicarZoom()
    {
        Editor.FontSize = TamanoEditor * Config.Zoom;
        // Las vistas toman el zoom al iniciarse; después, solo si cambia (asignarlo dispara ZoomFactorChanged,
        // que vuelve a FijarZoom y ahí se corta).
        if (Visor.CoreWebView2 is not null && Math.Abs(Visor.ZoomFactor - Config.Zoom) > 0.001)
            Visor.ZoomFactor = Config.Zoom;
        if (VisorPro.CoreWebView2 is not null && Math.Abs(VisorPro.ZoomFactor - Config.Zoom) > 0.001)
            VisorPro.ZoomFactor = Config.Zoom;
        EstadoZoom.Content = $"{Config.Zoom * 100:0} %";
    }

    private void Acercar_Executed(object sender, ExecutedRoutedEventArgs e) => CambiarZoom(1);

    private void Alejar_Executed(object sender, ExecutedRoutedEventArgs e) => CambiarZoom(-1);

    private void ZoomNormal_Executed(object sender, ExecutedRoutedEventArgs e) => FijarZoom(1);

    private void AlternarLateral_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        _recientes.LateralFijo = !_recientes.LateralFijo;
        _recientes.Guardar();
        _lateralDesplegado = false;
        AplicarLateral();
    }

    // El aviso se cierra; "Actualización pendiente" sigue en la barra de estado.
    private void CerrarAviso_Click(object sender, RoutedEventArgs e) => AvisoActualizacion.Visibility = Visibility.Collapsed;

    // La tarjeta de la esquina y "Actualización pendiente" en la barra de estado. Si ya se descargó, se deja como está.
    private void MostrarActualizacion(Actualizacion.Release nueva)
    {
        if (_instalador is not null || _descargando)
            return;
        _actualizacion = nueva;
        TextoActualizacion.Text = $"MD Codex {nueva.Version} está disponible.";
        EstadoActualizacion.ToolTip = $"MD Codex {nueva.Version} está disponible. Clic para actualizar.";
        AvisoActualizacion.Visibility = Visibility.Visible;
        EstadoActualizacion.Visibility = Visibility.Visible;
    }

    // Búsqueda a pedido (Ayuda → Buscar actualizaciones, o "Buscar ahora" en la configuración): siempre
    // responde, también si no hay nada nuevo o si GitHub no contesta.
    private async void BuscarActualizaciones_Click(object sender, RoutedEventArgs e)
    {
        var enConfiguracion = sender == BotonBuscarActualizaciones;
        BotonBuscarActualizaciones.IsEnabled = false;
        ResultadoBusqueda.Text = enConfiguracion ? "Buscando…" : "";
        string mensaje;
        try
        {
            if (await Actualizacion.ConsultarAsync() is { } nueva)
            {
                MostrarActualizacion(nueva);
                mensaje = $"MD Codex {nueva.Version} está disponible. Usa «Actualizar» en el aviso de la esquina.";
            }
            else
            {
                mensaje = $"Tienes la versión más reciente ({Marca.Version}).";
            }
        }
        catch (Exception ex)
        {
            mensaje = $"No se pudo consultar GitHub: {ex.Message}";
        }
        finally
        {
            BotonBuscarActualizaciones.IsEnabled = true;
        }
        if (enConfiguracion)
            ResultadoBusqueda.Text = mensaje;
        else
            MessageBox.Show(this, mensaje, "MD Codex", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // Primero descarga el instalador en segundo plano; ya descargado, reinicia la app para instalarlo.
    private async void Actualizar_Click(object sender, RoutedEventArgs e)
    {
        AvisoActualizacion.Visibility = Visibility.Collapsed;
        if (_instalador is not null)
        {
            // El cierre normal pide guardar los cambios; si se cancela, OnClosing desmarca el reinicio.
            _reabrirTrasActualizar = true;
            Close();
            return;
        }
        if (_actualizacion!.Instalador is null)
        {
            Marca.Abrir(_actualizacion.Pagina); // release sin instalador adjunto
            return;
        }
        if (_descargando)
            return;

        _descargando = true;
        var progreso = new Progress<double>(p => TextoEstadoActualizacion.Text = $"Descargando actualización… {p:P0}");
        try
        {
            _instalador = await Actualizacion.DescargarAsync(_actualizacion, progreso);
            TextoActualizacion.Text = $"MD Codex {_actualizacion.Version} está lista. Se instalará al cerrar la app.";
            AccionActualizacion.Text = "Reiniciar ahora";
            TextoEstadoActualizacion.Text = "Reiniciar para actualizar";
            EstadoActualizacion.ToolTip = $"MD Codex {_actualizacion.Version} se instalará al cerrar la app. Clic para reiniciar ahora.";
            AvisoActualizacion.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            TextoEstadoActualizacion.Text = "Actualización pendiente";
            EstadoActualizacion.ToolTip = $"No se pudo descargar la actualización ({ex.Message}). Clic para reintentar.";
        }
        finally
        {
            _descargando = false;
        }
    }

    // Con la barra lateral automática desplegada, un clic fuera de ella (y de la barra de actividad) la oculta.
    private void Ventana_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Los clics en el selector de íconos (otra ventana, pero de la barra lateral) tampoco la ocultan.
        if (_recientes.LateralFijo || !_lateralDesplegado || SelectorIconos.IsOpen
            || Contiene(Lateral, e.OriginalSource) || Contiene(BarraActividad, e.OriginalSource))
            return;
        _lateralDesplegado = false;
        AplicarLateral();
    }

    private void Negrita_Executed(object sender, ExecutedRoutedEventArgs e) => Envolver("**");

    private void Cursiva_Executed(object sender, ExecutedRoutedEventArgs e) => Envolver("*");

    protected override void OnClosing(CancelEventArgs e)
    {
        // Primero se trae lo último escrito en el editor Pro (es asíncrono) y luego se vuelve a cerrar.
        if (_proListo && !_cierreSincronizado)
        {
            e.Cancel = true;
            TrasSincronizar(() =>
            {
                _cierreSincronizado = true;
                Close();
                _cierreSincronizado = false; // si el usuario canceló, la próxima vez se vuelve a sincronizar
            });
            return;
        }
        if (!ConfirmarDescartar(_pestanas))
        {
            e.Cancel = true;
            _reabrirTrasActualizar = false;
        }
        else
            GuardarSesion();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= AlCambiarPreferencias;
        foreach (var p in _pestanas)
            p.Dispose();
        // Con una actualización descargada, se instala ahora que la app se cierra.
        if (_instalador is not null)
            Actualizacion.Instalar(_instalador, _reabrirTrasActualizar);
        base.OnClosed(e);
    }
}
