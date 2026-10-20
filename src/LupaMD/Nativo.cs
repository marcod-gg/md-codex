using System.Runtime.InteropServices;

namespace LupaMD;

internal static class Nativo
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    // Pide a Windows 11 esquinas redondeadas para la ventana; en Windows 10 no hace nada.
    public static void RedondearEsquinas(IntPtr ventana)
    {
        var preferencia = DwmwcpRound;
        _ = DwmSetWindowAttribute(ventana, DwmwaWindowCornerPreference, ref preferencia, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int atributo, ref int valor, int tamano);
}
