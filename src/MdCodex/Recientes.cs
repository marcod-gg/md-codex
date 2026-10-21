using System.IO;
using System.Text.Json;

namespace MdCodex;

// Lo que quedó abierto al cerrar, para reabrirlo al iniciar sin archivos.
// Carpeta es de las versiones sin espacios de trabajo: solo se lee para crear el primero.
public sealed record Sesion(string? Carpeta, List<string> Archivos, string? Activo, string Modo);

// Espacio de trabajo: un nombre y las carpetas (de cualquier lugar) que se ven juntas en el panel.
public sealed class EspacioTrabajo
{
    public string Nombre { get; set; } = "";
    public List<string> Carpetas { get; set; } = [];
    public bool Expandido { get; set; } = true;
    // Glifo de Segoe Fluent Icons; por omisión, el de biblioteca.
    public string Icono { get; set; } = IconoPorDefecto;

    public const string IconoPorDefecto = "";
}

// Archivos y carpetas recientes, y la última sesión. Se guardan en %LOCALAPPDATA%\MdCodex\recientes.json.
public sealed class Recientes
{
    private const int Maximo = 10;

    private static readonly string Ruta = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdCodex", "recientes.json");

    private static readonly JsonSerializerOptions Opciones = new() { WriteIndented = true };

    public List<string> Archivos { get; set; } = [];
    public List<string> Carpetas { get; set; } = [];
    public Sesion? UltimaSesion { get; set; }
    // Preferencia de la barra lateral: fija (siempre abierta) o automática (se despliega al tocar un ícono).
    public bool LateralFijo { get; set; } = true;
    // Configuración del editor de texto: ajustar las líneas largas al ancho (si no, se desplazan en horizontal).
    public bool AjustarLineas { get; set; } = true;
    // En el orden en que se muestran (se reordenan arrastrándolos).
    public List<EspacioTrabajo> Espacios { get; set; } = [];

    // Siempre hay al menos un espacio de trabajo; el primero hereda la carpeta de la última sesión.
    public void AsegurarEspacio()
    {
        if (Espacios.Count == 0)
            Espacios.Add(new EspacioTrabajo { Nombre = "Principal", Carpetas = UltimaSesion?.Carpeta is { } carpeta ? [carpeta] : [] });
    }

    // Un archivo dañado o ilegible no impide abrir la app: se empieza sin recientes.
    public static Recientes Cargar()
    {
        Recientes recientes;
        try
        {
            recientes = JsonSerializer.Deserialize<Recientes>(File.ReadAllText(Ruta), Opciones) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            recientes = new();
        }
        recientes.QuitarInexistentes();
        return recientes;
    }

    public void Guardar()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Ruta)!);
            File.WriteAllText(Ruta, JsonSerializer.Serialize(this, Opciones));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin recientes no se pierde nada importante.
        }
    }

    public void AgregarArchivo(string ruta) => AlFrente(Archivos, ruta);

    public void AgregarCarpeta(string ruta) => AlFrente(Carpetas, ruta);

    public void Limpiar()
    {
        Archivos.Clear();
        Carpetas.Clear();
    }

    public void QuitarInexistentes()
    {
        Archivos.RemoveAll(r => !File.Exists(r));
        Carpetas.RemoveAll(r => !Directory.Exists(r));
    }

    private static void AlFrente(List<string> lista, string ruta)
    {
        lista.RemoveAll(r => string.Equals(r, ruta, StringComparison.OrdinalIgnoreCase));
        lista.Insert(0, ruta);
        if (lista.Count > Maximo)
            lista.RemoveRange(Maximo, lista.Count - Maximo);
    }
}
