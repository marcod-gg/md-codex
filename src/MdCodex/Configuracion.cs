using System.IO;

namespace MdCodex;

public enum TemaPreferido { Sistema, Claro, Oscuro }

// Modo con que se abre la app: el de la última vez o uno fijo.
public enum ModoInicial { Ultimo, Vista, Dividido, Editor, Pro }

public enum CambioExterno { Recargar, Preguntar }

public enum GuardadoAutomatico { Desactivado, AlCambiar, AlDejarDeEscribir }

public enum Ancho { Compacto, Completo }

// Lo que el usuario elige en el panel Configuración. Se guarda junto a los recientes.
public sealed class Configuracion
{
    // General
    public TemaPreferido Tema { get; set; } = TemaPreferido.Sistema;
    public ModoInicial ModoInicial { get; set; } = ModoInicial.Ultimo;
    public bool ReabrirSesion { get; set; } = true;
    public CambioExterno CambioExterno { get; set; } = CambioExterno.Recargar;
    public bool BuscarActualizaciones { get; set; } = true;

    // Editor de texto
    public bool AjustarLineas { get; set; } = true;
    public bool NumerosLinea { get; set; } = true;
    public bool ResaltarLinea { get; set; } = true;
    public GuardadoAutomatico GuardadoAutomatico { get; set; } = GuardadoAutomatico.AlDejarDeEscribir;

    // Vista previa y editor por bloques
    public Ancho AnchoVista { get; set; } = Ancho.Compacto;
    public bool SincronizarDesplazamiento { get; set; } = true;
    public Ancho AnchoBloques { get; set; } = Ancho.Compacto;

    // Imágenes: una carpeta fija (ruta completa) o, si es solo un nombre, una carpeta junto a cada documento.
    public string CarpetaImagenes { get; set; } = CarpetaImagenesPorDefecto;

    // Espacios de trabajo: cada carpeta agregada muestra su ruta junto al nombre.
    public bool MostrarRutasCarpetas { get; set; }

    // Ver: herramientas de la barra lateral que se muestran (su ícono en la barra de actividad y su panel).
    public bool MostrarDocumentos { get; set; } = true;
    public bool MostrarEspacios { get; set; } = true;
    public bool MostrarEsquema { get; set; } = true;

    // Zoom de lo que se está viendo (editor, vista o bloques), desde la barra de estado.
    public double Zoom { get; set; } = 1;

    // Para ajustar una sola vez lo guardado por versiones anteriores (ver Migrar).
    public int Version { get; set; }
    private const int VersionActual = 2;

    // Imágenes del usuario\md-codex.
    public static string CarpetaImagenesPorDefecto { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "md-codex");

    // La 1.3.4 guardó los valores por omisión de entonces: el guardado automático desactivado y la carpeta
    // "imagenes" junto a cada documento. Se llevan a los nuevos una vez; lo que el usuario cambió se respeta.
    public bool Migrar()
    {
        if (Version >= VersionActual)
            return false;
        if (Version < 2)
        {
            if (GuardadoAutomatico == GuardadoAutomatico.Desactivado)
                GuardadoAutomatico = GuardadoAutomatico.AlDejarDeEscribir;
            if (CarpetaImagenes == "imagenes")
                CarpetaImagenes = CarpetaImagenesPorDefecto;
        }
        Version = VersionActual;
        return true;
    }

    // Una ruta completa se normaliza tal cual. Un nombre se toma relativo al documento: sin salir con "..",
    // sin caracteres inválidos, y acepta subcarpetas ("recursos/img"). Si no sirve, la predeterminada.
    public static string CarpetaValida(string? nombre)
    {
        nombre = nombre?.Trim() ?? "";
        if (Path.IsPathFullyQualified(nombre))
        {
            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(nombre));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return CarpetaImagenesPorDefecto;
            }
        }
        var partes = nombre.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (partes.Length == 0 || partes.Any(p => p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return CarpetaImagenesPorDefecto;
        return string.Join('/', partes);
    }
}
