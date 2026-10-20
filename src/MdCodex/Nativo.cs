using System.Runtime.InteropServices;

namespace MdCodex;

internal static class Nativo
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const uint TmeLeave = 0x02;
    private const uint TmeNonClient = 0x10;

    // Mensajes de la zona "no cliente" que se usan para el botón de maximizar (Snap Layouts).
    public const int WmNcHitTest = 0x0084;
    public const int WmNcLButtonDown = 0x00A1;
    public const int WmNcLButtonUp = 0x00A2;
    public const int WmNcMouseLeave = 0x02A2;
    public const int HtMaxButton = 9;

    // Pide a Windows 11 esquinas redondeadas para la ventana; en Windows 10 no hace nada.
    public static void RedondearEsquinas(IntPtr ventana)
    {
        var preferencia = DwmwcpRound;
        _ = DwmSetWindowAttribute(ventana, DwmwaWindowCornerPreference, ref preferencia, sizeof(int));
    }

    // Pide a Windows un WM_NCMOUSELEAVE cuando el cursor salga de la zona no cliente.
    public static void SeguirSalidaNoCliente(IntPtr ventana)
    {
        var seguimiento = new TrackMouseEventDatos
        {
            Tamano = (uint)Marshal.SizeOf<TrackMouseEventDatos>(),
            Banderas = TmeLeave | TmeNonClient,
            Ventana = ventana,
        };
        _ = TrackMouseEvent(ref seguimiento);
    }

    // Coordenadas de pantalla (con signo, por los monitores a la izquierda o arriba del principal) de un lParam.
    public static (int X, int Y) PuntoDe(IntPtr lParam)
    {
        var valor = lParam.ToInt64();
        return ((short)(valor & 0xFFFF), (short)((valor >> 16) & 0xFFFF));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventDatos
    {
        public uint Tamano;
        public uint Banderas;
        public IntPtr Ventana;
        public uint TiempoEspera;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int atributo, ref int valor, int tamano);

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TrackMouseEventDatos datos);
}
