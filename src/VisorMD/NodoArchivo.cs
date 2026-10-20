using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;

namespace VisorMD;

// Elemento del árbol del Explorador. Las carpetas cargan su contenido al expandirse por primera vez.
public sealed class NodoArchivo : INotifyPropertyChanged
{
    private static readonly HashSet<string> Ignoradas = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".vscode", ".idea", "node_modules", "bin", "obj" };

    private bool _expandido;
    private bool _cargado;
    private bool _esActual;

    private NodoArchivo(string ruta, bool esCarpeta)
    {
        Ruta = ruta;
        Nombre = Path.GetFileName(ruta);
        EsCarpeta = esCarpeta;
        if (esCarpeta)
            Hijos.Add(new NodoArchivo("…", esCarpeta: false)); // marcador para que se vea la flecha de expandir
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Ruta { get; }
    public string Nombre { get; }
    public bool EsCarpeta { get; }
    public ObservableCollection<NodoArchivo> Hijos { get; } = [];
    public string Icono => EsCarpeta ? (Expandido ? "" : "") : "";

    public bool Expandido
    {
        get => _expandido;
        set
        {
            if (_expandido == value)
                return;
            _expandido = value;
            if (value && !_cargado)
            {
                _cargado = true;
                Hijos.Clear();
                foreach (var hijo in Listar(Ruta))
                    Hijos.Add(hijo);
            }
            Avisar(nameof(Expandido));
            Avisar(nameof(Icono));
        }
    }

    public bool EsActual
    {
        get => _esActual;
        private set
        {
            if (_esActual == value)
                return;
            _esActual = value;
            Avisar(nameof(EsActual));
        }
    }

    public static ObservableCollection<NodoArchivo> Listar(string carpeta)
    {
        var nodos = new ObservableCollection<NodoArchivo>();
        try
        {
            var info = new DirectoryInfo(carpeta);
            foreach (var sub in info.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
                if (!sub.Attributes.HasFlag(FileAttributes.Hidden) && !Ignoradas.Contains(sub.Name))
                    nodos.Add(new NodoArchivo(sub.FullName, esCarpeta: true));
            foreach (var archivo in info.EnumerateFiles().OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
                if (Documentos.EsMarkdown(archivo.Name))
                    nodos.Add(new NodoArchivo(archivo.FullName, esCarpeta: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Carpeta sin permiso o desaparecida: se muestra vacía.
        }
        return nodos;
    }

    // Expande las carpetas que llevan hasta la ruta y marca el archivo como actual.
    public static void Revelar(IEnumerable<NodoArchivo> nodos, string? ruta)
    {
        foreach (var nodo in nodos)
        {
            if (ruta is not null && nodo.EsCarpeta && ruta.StartsWith(nodo.Ruta + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                nodo.Expandido = true;
            nodo.EsActual = !nodo.EsCarpeta && string.Equals(nodo.Ruta, ruta, StringComparison.OrdinalIgnoreCase);
            if (nodo._cargado)
                Revelar(nodo.Hijos, ruta);
        }
    }

    public static HashSet<string> Expandidas(IEnumerable<NodoArchivo> nodos)
    {
        var rutas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Recorrer(IEnumerable<NodoArchivo> lista)
        {
            foreach (var nodo in lista.Where(n => n.Expandido))
            {
                rutas.Add(nodo.Ruta);
                Recorrer(nodo.Hijos);
            }
        }
        Recorrer(nodos);
        return rutas;
    }

    public static void Expandir(IEnumerable<NodoArchivo> nodos, IReadOnlySet<string> rutas)
    {
        foreach (var nodo in nodos.Where(n => n.EsCarpeta && rutas.Contains(n.Ruta)))
        {
            nodo.Expandido = true;
            Expandir(nodo.Hijos, rutas);
        }
    }

    private void Avisar(string propiedad) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propiedad));
}
