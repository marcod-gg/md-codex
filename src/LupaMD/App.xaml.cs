using System.IO;
using System.Windows;

namespace LupaMD;

public partial class App : Application
{
    // Acepta rutas de archivos como argumentos para poder usarlo con "Abrir con…" en Windows; cada una abre una pestaña.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        new MainWindow(e.Args.Where(File.Exists).ToList()).Show();
    }
}
