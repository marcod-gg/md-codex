using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace VisorMD;

public partial class MainWindow : Window
{
    // Host virtual que apunta a la carpeta del documento, para que funcionen imágenes y enlaces relativos.
    private const string HostDocumento = "doc.visormd";

    private static readonly string[] Extensiones = [".md", ".markdown", ".mdown", ".mkd", ".txt"];

    private string? _archivoActual;
    private FileSystemWatcher? _vigilante;
    private readonly DispatcherTimer _demoraRecarga;

    public MainWindow(string? archivoInicial)
    {
        InitializeComponent();

        // Los editores suelen disparar varios eventos al guardar; se agrupan en una sola recarga.
        _demoraRecarga = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _demoraRecarga.Tick += (_, _) =>
        {
            _demoraRecarga.Stop();
            Mostrar();
        };

        Loaded += async (_, _) =>
        {
            await IniciarVisorAsync();
            if (archivoInicial is not null)
                AbrirArchivo(archivoInicial);
            else
                Visor.NavigateToString(Renderizador.Bienvenida());
        };
    }

    private async Task IniciarVisorAsync()
    {
        var datos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisorMD", "WebView2");
        var entorno = await CoreWebView2Environment.CreateAsync(userDataFolder: datos);
        await Visor.EnsureCoreWebView2Async(entorno);

        var core = Visor.CoreWebView2;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.NavigationStarting += Visor_NavigationStarting;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            AbrirEnlace(e.Uri);
        };
    }

    // Todos los clics en enlaces y los archivos arrastrados a la ventana pasan por aquí.
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

        string? local = destino switch
        {
            { IsFile: true } => destino.LocalPath,
            { Host: HostDocumento } when _archivoActual is not null =>
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_archivoActual)!, Uri.UnescapeDataString(destino.AbsolutePath.TrimStart('/')))),
            _ => null,
        };

        if (local is not null && EsMarkdown(local))
        {
            if (File.Exists(local))
                AbrirArchivo(local);
            else
                MessageBox.Show(this, $"No se encontró el archivo:\n{local}", "VisorMD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Cualquier otra cosa (web, correo, PDF, imágenes) se abre con el programa predeterminado de Windows.
        try
        {
            Process.Start(new ProcessStartInfo(local ?? destino.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo abrir el enlace:\n{ex.Message}", "VisorMD", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AbrirArchivo(string ruta)
    {
        _archivoActual = Path.GetFullPath(ruta);
        var carpeta = Path.GetDirectoryName(_archivoActual)!;

        Visor.CoreWebView2.SetVirtualHostNameToFolderMapping(HostDocumento, carpeta, CoreWebView2HostResourceAccessKind.Allow);
        Vigilar(carpeta, Path.GetFileName(_archivoActual));
        Title = $"{Path.GetFileName(_archivoActual)} — VisorMD";
        Mostrar();
        CommandManager.InvalidateRequerySuggested();
    }

    private void Mostrar()
    {
        if (_archivoActual is null)
            return;

        string html;
        try
        {
            html = Renderizador.Documento(LeerTexto(_archivoActual), Path.GetFileName(_archivoActual), $"https://{HostDocumento}/");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            html = Renderizador.Error(ex.Message);
        }
        Visor.NavigateToString(html);
    }

    // Permite leer aunque el editor tenga el archivo abierto.
    private static string LeerTexto(string ruta)
    {
        using var flujo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var lector = new StreamReader(flujo, detectEncodingFromByteOrderMarks: true);
        return lector.ReadToEnd();
    }

    private void Vigilar(string carpeta, string nombre)
    {
        _vigilante?.Dispose();
        _vigilante = new FileSystemWatcher(carpeta, nombre)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler alCambiar = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _demoraRecarga.Stop();
            _demoraRecarga.Start();
        });
        _vigilante.Changed += alCambiar;
        _vigilante.Created += alCambiar;
        _vigilante.Renamed += (s, e) => alCambiar(s, e);
        _vigilante.EnableRaisingEvents = true;
    }

    private static bool EsMarkdown(string ruta) =>
        Extensiones.Contains(Path.GetExtension(ruta), StringComparer.OrdinalIgnoreCase);

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

    private void Recargar_Executed(object sender, ExecutedRoutedEventArgs e) => Mostrar();

    private void Recargar_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _archivoActual is not null;

    protected override void OnClosed(EventArgs e)
    {
        _vigilante?.Dispose();
        base.OnClosed(e);
    }
}
