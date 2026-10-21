using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;

namespace MdCodex;

// Un documento abierto: su texto (con su propio historial de deshacer), el archivo en disco
// y la posición en la que quedó el usuario.
public sealed class Pestana : INotifyPropertyChanged, IDisposable
{
    private static int _ultimoId;
    private static int _sinTitulo;

    private readonly string? _nombreSinTitulo;
    private readonly DispatcherTimer _demoraDisco;
    private FileSystemWatcher? _vigilante;
    private bool _esActiva;
    private bool _esTemporal;

    private Pestana(string? ruta, string texto, Encoding codificacion)
    {
        Id = ++_ultimoId;
        Documento = new TextDocument(texto);
        Documento.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(UndoStack.IsOriginalFile))
                return;
            if (Modificado)
                EsTemporal = false; // como en VS Code: editar fija la pestaña
            Avisar(nameof(Modificado));
        };
        Codificacion = codificacion;
        if (ruta is null)
            _nombreSinTitulo = $"Sin título-{++_sinTitulo}";

        // Los editores suelen disparar varios eventos al guardar; se agrupan en un solo aviso.
        _demoraDisco = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _demoraDisco.Tick += (_, _) =>
        {
            _demoraDisco.Stop();
            CambioEnDisco?.Invoke(this, EventArgs.Empty);
        };
        AsignarRuta(ruta);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // Se lanza en el hilo de la interfaz cuando el archivo cambia fuera de la app.
    public event EventHandler? CambioEnDisco;

    public int Id { get; }
    public TextDocument Documento { get; }
    public string? Ruta { get; private set; }
    public Encoding Codificacion { get; set; }
    public string Nombre => Ruta is null ? _nombreSinTitulo! : Path.GetFileName(Ruta);
    public string Descripcion => Ruta ?? "Sin guardar";
    public bool TieneRuta => Ruta is not null;
    public bool Modificado => !Documento.UndoStack.IsOriginalFile;

    // Versión en disco que el usuario ya decidió no recargar, para no volver a preguntar por lo mismo.
    public string? CambioRechazado { get; set; }

    // Dónde quedó el usuario, para volver al mismo lugar al cambiar de pestaña.
    public int Cursor { get; set; }
    public double ScrollEditor { get; set; }
    public double ScrollVista { get; set; }

    public string? Carpeta => Ruta is null ? null : Path.GetDirectoryName(Ruta);

    public bool EsActiva
    {
        get => _esActiva;
        set => Cambiar(ref _esActiva, value, nameof(EsActiva));
    }

    // Pestaña de vista rápida (nombre en cursiva): la reemplaza el siguiente archivo abierto desde el Explorador.
    public bool EsTemporal
    {
        get => _esTemporal;
        set => Cambiar(ref _esTemporal, value, nameof(EsTemporal));
    }

    public static Pestana Nueva() => new(null, "", new UTF8Encoding(false));

    public static Pestana Abrir(string ruta)
    {
        var (texto, codificacion) = LeerTexto(ruta);
        return new Pestana(ruta, texto, codificacion);
    }

    // Permite leer aunque otro programa tenga el archivo abierto, y detecta la codificación para guardar igual.
    public static (string Texto, Encoding Codificacion) LeerTexto(string ruta)
    {
        using var flujo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var lector = new StreamReader(flujo, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var texto = lector.ReadToEnd();
        return (texto, lector.CurrentEncoding);
    }

    public void Escribir(string ruta)
    {
        File.WriteAllText(ruta, Documento.Text, Codificacion);
        Documento.UndoStack.MarkAsOriginalFile();
        CambioRechazado = null;
        EsTemporal = false;
        if (!string.Equals(ruta, Ruta, StringComparison.OrdinalIgnoreCase))
            AsignarRuta(Path.GetFullPath(ruta));
    }

    // El archivo se movió en el disco: la pestaña pasa a vigilar la nueva ruta.
    public void Mover(string ruta) => AsignarRuta(Path.GetFullPath(ruta));

    public void Recargar(string texto)
    {
        Documento.Text = texto;
        Documento.UndoStack.ClearAll();
        Documento.UndoStack.MarkAsOriginalFile();
        CambioRechazado = null;
    }

    public void Dispose()
    {
        _vigilante?.Dispose();
        _demoraDisco.Stop();
    }

    private void AsignarRuta(string? ruta)
    {
        Ruta = ruta;
        _vigilante?.Dispose();
        _vigilante = null;
        if (ruta is not null)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _vigilante = new FileSystemWatcher(Carpeta!, Path.GetFileName(ruta))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler alCambiar = (_, _) => dispatcher.BeginInvoke(() =>
            {
                _demoraDisco.Stop();
                _demoraDisco.Start();
            });
            _vigilante.Changed += alCambiar;
            _vigilante.Created += alCambiar;
            _vigilante.Renamed += (s, e) => alCambiar(s, e);
            _vigilante.EnableRaisingEvents = true;
        }
        Avisar(nameof(Ruta));
        Avisar(nameof(Nombre));
        Avisar(nameof(Descripcion));
        Avisar(nameof(TieneRuta));
    }

    private void Cambiar(ref bool campo, bool valor, string propiedad)
    {
        if (campo == valor)
            return;
        campo = valor;
        Avisar(propiedad);
    }

    private void Avisar(string propiedad) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propiedad));
}
