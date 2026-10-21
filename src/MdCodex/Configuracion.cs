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
    public GuardadoAutomatico GuardadoAutomatico { get; set; } = GuardadoAutomatico.Desactivado;

    // Vista previa y editor por bloques
    public Ancho AnchoVista { get; set; } = Ancho.Compacto;
    public bool SincronizarDesplazamiento { get; set; } = true;
    public Ancho AnchoBloques { get; set; } = Ancho.Compacto;

    // Imágenes
    public string CarpetaImagenes { get; set; } = CarpetaImagenesPorDefecto;

    // Zoom de lo que se está viendo (editor, vista o bloques), desde la barra de estado.
    public double Zoom { get; set; } = 1;

    public const string CarpetaImagenesPorDefecto = "imagenes";

    // Nombre de carpeta usable bajo la del documento: sin rutas absolutas, sin salir con "..", sin caracteres
    // inválidos. Acepta subcarpetas ("recursos/img"). Si no sirve, la de siempre.
    public static string CarpetaValida(string? nombre)
    {
        var partes = (nombre ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (partes.Length == 0 || partes.Any(p => p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return CarpetaImagenesPorDefecto;
        return string.Join('/', partes);
    }
}
