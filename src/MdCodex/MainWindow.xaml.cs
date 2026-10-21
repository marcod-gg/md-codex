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
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
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

    private enum Panel { Explorador, Esquema }

    private readonly ObservableCollection<Pestana> _pestanas = [];
    // Documento vacío que muestra el editor cuando no hay pestañas abiertas.
    private readonly TextDocument _sinDocumento = new();
    // Host virtual de cada carpeta de documento → carpeta en disco, para servir sus imágenes.
    private readonly Dictionary<string, string> _carpetasPorHost = new(StringComparer.OrdinalIgnoreCase);
    private Pestana? _activa;
    private string? _carpetaRaiz;
    private Modo _modo = Modo.Vista;
    private Panel _panel = Panel.Explorador;
    private bool _lateralVisible = true;
    private double _anchoLateral = 260;
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

    public MainWindow(IReadOnlyList<string> archivosIniciales)
    {
        InitializeComponent();

        BarraPestanas.ItemsSource = _pestanas;
        Editor.Document = _sinDocumento;
        _demoraDocumento = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _demoraDocumento.Tick += (_, _) => ActualizarDocumento();

        Editor.TextChanged += (_, _) => Reiniciar(_demoraDocumento);
        Editor.TextArea.Caret.PositionChanged += (_, _) => ActualizarPosicion();
        Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SincronizarScroll();
        Editor.Options.HighlightCurrentLine = true;
        SearchPanel.Install(Editor);

        PreviewDragEnter += Ventana_PreviewDragOver;
        PreviewDragOver += Ventana_PreviewDragOver;
        PreviewDrop += Ventana_PreviewDrop;
        StateChanged += (_, _) => AjustarMaximizado();
        SourceInitialized += (_, _) =>
        {
            var ventana = new WindowInteropHelper(this).Handle;
            Nativo.RedondearEsquinas(ventana);
            HwndSource.FromHwnd(ventana).AddHook(GanchoVentana);
        };
        AreaTrabajo.SizeChanged += (_, _) => RecortarAreaTrabajo();

        AplicarTema();
        SystemEvents.UserPreferenceChanged += AlCambiarPreferencias;

        AplicarModo(Modo.Vista);
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
            else
            {
                RestaurarSesion();
            }

            if (await Marca.BuscarActualizacionAsync() is { } nueva &&
                MessageBox.Show(this, $"Hay una nueva versión de MD Codex ({nueva.Version}). ¿Quieres descargarla?",
                    "MD Codex", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Marca.Abrir(nueva.Url);
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
            if (sesion.Carpeta is { } carpeta && Directory.Exists(carpeta))
                AbrirCarpeta(carpeta);
            foreach (var archivo in sesion.Archivos.Where(File.Exists))
                AbrirArchivo(archivo);
            if (_pestanas.FirstOrDefault(p => string.Equals(p.Ruta, sesion.Activo, StringComparison.OrdinalIgnoreCase)) is { } activa)
                Activar(activa);
            if (Enum.TryParse<Modo>(sesion.Modo, out var modo))
                AplicarModo(modo);
        }
        finally
        {
            _restaurando = false;
        }
    }

    private void GuardarSesion()
    {
        // Los documentos sin título no tienen dónde reabrirse.
        _recientes.UltimaSesion = new Sesion(
            _carpetaRaiz,
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

    // En modo dividido, la vista sigue a la primera línea visible del editor.
    private void SincronizarScroll()
    {
        if (!_vistaLista || _modo != Modo.Dividido || _activa is null)
            return;

        var vista = Editor.TextArea.TextView;
        Enviar(new { tipo = "linea", linea = vista.GetDocumentLineByVisualTop(vista.ScrollOffset.Y).LineNumber - 1 });
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

    // La raíz virtual de un documento es la carpeta abierta en el Explorador si el documento está dentro
    // de ella, o su propia carpeta si no. La URL base incluye la ruta del documento bajo esa raíz, así
    // funcionan rutas como ../img/x.png sin dar acceso a nada fuera de la raíz.
    private Ubicacion? Ubicar(Pestana p)
    {
        if (p.Carpeta is not { } carpeta)
            return null;
        var raiz = _carpetaRaiz is not null && Documentos.EstaDentro(carpeta, _carpetaRaiz) ? _carpetaRaiz : carpeta;
        var relativa = Path.GetRelativePath(raiz, carpeta);
        var camino = relativa == "." ? "" : string.Join('/', relativa.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString)) + "/";
        var host = Documentos.HostPara(raiz);
        return new Ubicacion(host, raiz, $"https://{host}/{camino}");
    }

    // Traduce una URL virtual a un archivo dentro de su raíz; null si no corresponde o se sale de ella.
    private string? RutaLocal(Uri destino)
    {
        // Las librerías de la vista previa viven en la carpeta Vista junto al .exe.
        var raiz = destino.Host.Equals(Documentos.HostRecursos, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(AppContext.BaseDirectory, "Vista")
            : _carpetasPorHost.GetValueOrDefault(destino.Host);
        if (raiz is null)
            return null;
        // Las dos rutas pasan por GetFullPath: expande los nombres cortos (MARCOG~1), y si solo una se
        // expandiera, el archivo parecería estar fuera de la raíz y se negaría.
        raiz = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raiz));
        var ruta = Path.GetFullPath(Path.Combine(raiz, Uri.UnescapeDataString(destino.AbsolutePath.TrimStart('/'))));
        return Documentos.EstaDentro(ruta, raiz) ? ruta : null;
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

        // Como VS Code: si el archivo está fuera de la carpeta abierta, se abre su carpeta.
        if (_carpetaRaiz is null || !completa.StartsWith(_carpetaRaiz + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            AbrirCarpeta(nueva.Carpeta!);
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

    private bool GuardarComo(Pestana p)
    {
        var dialogo = new SaveFileDialog
        {
            Title = "Guardar como",
            Filter = "Markdown (*.md)|*.md|Texto (*.txt)|*.txt|Todos los archivos (*.*)|*.*",
            DefaultExt = ".md",
            FileName = p.Ruta is null ? $"{p.Nombre}.md" : p.Nombre,
        };
        if ((p.Carpeta ?? _carpetaRaiz) is { } carpetaInicial)
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

        if (p.Modificado && MessageBox.Show(this,
                $"{p.Nombre} cambió fuera de MD Codex.\n¿Quieres recargarlo? Perderás los cambios que no guardaste.",
                "MD Codex", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
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
            NodoArchivo.Revelar(nodos, _activa?.Ruta);
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
        Lateral.Visibility = Visible(_lateralVisible);
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
        EsquemaVacio.Text = _activa is null ? "No hay ningún documento abierto." : "El documento no tiene títulos.";
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
    }

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

        if (nodo.EsCarpeta)
            nodo.Expandido = !nodo.Expandido;
        else
            AbrirArchivo(nodo.Ruta, temporal: true);
    }

    // Doble clic en el Explorador fija la pestaña temporal.
    private void Arbol_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Ancestro<TreeViewItem>(e.OriginalSource)?.DataContext is NodoArchivo { EsCarpeta: false } nodo)
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
        if ((_activa?.Carpeta ?? _carpetaRaiz) is { } carpetaInicial)
            dialogo.InitialDirectory = carpetaInicial;

        if (dialogo.ShowDialog(this) == true)
            foreach (var archivo in dialogo.FileNames)
                AbrirArchivo(archivo);
    }

    private void AbrirCarpeta_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dialogo = new OpenFolderDialog { Title = "Abrir carpeta" };
        if (_carpetaRaiz is not null)
            dialogo.InitialDirectory = _carpetaRaiz;

        if (dialogo.ShowDialog(this) == true)
        {
            AbrirCarpeta(dialogo.FolderName);
            RegistrarCarpetaReciente(dialogo.FolderName);
            MostrarPanel(Panel.Explorador);
        }
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

    private void AlternarLateral_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        _lateralVisible = !_lateralVisible;
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
            e.Cancel = true;
        else
            GuardarSesion();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= AlCambiarPreferencias;
        foreach (var p in _pestanas)
            p.Dispose();
        base.OnClosed(e);
    }
}
