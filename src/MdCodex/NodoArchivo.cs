using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;

namespace MdCodex;

// Elemento del árbol de espacios de trabajo: un espacio (primer nivel), una de sus carpetas, o lo que
// hay dentro. Las carpetas cargan su contenido al expandirse por primera vez.
public sealed class NodoArchivo : INotifyPropertyChanged
{
    private static readonly HashSet<string> Ignoradas = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".vscode", ".idea", "node_modules", "bin", "obj" };

    private bool _expandido;
    private bool _cargado;
    private bool _esActual;

    private NodoArchivo(string ruta, bool esCarpeta, NodoArchivo? padre)
    {
        Ruta = ruta;
        Nombre = Path.GetFileName(ruta);
        EsCarpeta = esCarpeta;
        Padre = padre;
        if (esCarpeta)
            Hijos.Add(new NodoArchivo("…", esCarpeta: false, this)); // marcador para que se vea la flecha de expandir
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // En un espacio de trabajo, su nombre.
    public string Ruta { get; }
    public string Nombre { get; private init; }
    public bool EsCarpeta { get; }
    public NodoArchivo? Padre { get; }
    // Carpeta de un espacio de trabajo, en el segundo nivel del árbol.
    public bool EsRaiz { get; private init; }
    // Solo en los nodos de primer nivel.
    public EspacioTrabajo? Espacio { get; private init; }
    public bool EsEspacio => Espacio is not null;
    // El espacio de trabajo al que pertenece el nodo (una misma carpeta puede estar en varios); null en Documentos.
    public EspacioTrabajo? EspacioDe => Espacio ?? Padre?.EspacioDe;

    // Sección "Documentos" (primer nivel) y sus grupos "Abiertos" y "Recientes": se pliegan como carpetas,
    // pero no son lugares del disco.
    public bool EsGrupo { get; private init; }
    // Los grupos (Abiertos, Recientes) se muestran como encabezados.
    public bool EsSeccion => EsGrupo;
    public bool EnDocumentos => EsGrupo || Padre?.EnDocumentos == true;
    private string? _iconoGrupo;

    // Raíz (no se muestra) del panel Documentos, con sus dos grupos; un grupo vacío se oculta.
    public static NodoArchivo DeDocumentos()
    {
        var raiz = Grupo("Documentos", "", null, expandido: true);
        raiz.Hijos.Add(Grupo("Abiertos", "", raiz, expandido: true));
        raiz.Hijos.Add(Grupo("Recientes", "", raiz, expandido: true));
        return raiz;
    }

    public NodoArchivo Abiertos => Hijos[0];
    public NodoArchivo Recientes => Hijos[1];

    // Documento recién agregado a un grupo: su fila aparece con animación la primera vez que se muestra.
    public bool Entrando { get; set; }

    // Ver → Mostrar la ruta de las carpetas: las carpetas de un espacio de trabajo llevan su ruta junto al
    // nombre. Se lee al armar el árbol (al cambiarla, el árbol se rehace).
    public static bool MostrarRutas { get; set; }
    public string Descripcion => EsRaiz && MostrarRutas ? "   " + Ruta : "";

    // Carpeta donde caería lo que se está arrastrando: su fila se resalta.
    private bool _esDestino;
    public bool EsDestino
    {
        get => _esDestino;
        set
        {
            if (_esDestino == value)
                return;
            _esDestino = value;
            Avisar(nameof(EsDestino));
        }
    }

    private bool _oculto;
    public bool Oculto
    {
        get => _oculto;
        set
        {
            if (_oculto == value)
                return;
            _oculto = value;
            Avisar(nameof(Oculto));
        }
    }

    // Deja los documentos de un grupo en el orden indicado, moviendo los que ya están (sin rehacerlos) y
    // agregando los nuevos; los que sobran se quitan. Los nuevos se marcan para entrar con animación.
    public void OrdenarDocumentos(IReadOnlyList<string> rutas, bool animar)
    {
        for (var i = 0; i < rutas.Count; i++)
        {
            var existente = Hijos.Skip(i).FirstOrDefault(n => string.Equals(n.Ruta, rutas[i], StringComparison.OrdinalIgnoreCase));
            if (existente is null)
                Hijos.Insert(i, new NodoArchivo(rutas[i], esCarpeta: false, this) { Entrando = animar });
            else if (Hijos.IndexOf(existente) is var j && j != i)
                Hijos.Move(j, i);
        }
        while (Hijos.Count > rutas.Count)
            Hijos.RemoveAt(Hijos.Count - 1);
        Oculto = Hijos.Count == 0;
    }

    private static NodoArchivo Grupo(string nombre, string icono, NodoArchivo? padre, bool expandido)
    {
        var grupo = new NodoArchivo(nombre, esCarpeta: true, padre)
        {
            EsGrupo = true,
            Nombre = nombre,
            _iconoGrupo = icono,
            _cargado = true,
            _expandido = expandido,
        };
        grupo.Hijos.Clear(); // sin el marcador "…": su contenido se llena aparte
        grupo._oculto = padre is not null; // los grupos parten vacíos
        return grupo;
    }

    public static NodoArchivo DeEspacio(EspacioTrabajo espacio)
    {
        var nodo = new NodoArchivo(espacio.Nombre, esCarpeta: false, null)
        {
            Espacio = espacio,
            Nombre = espacio.Nombre,
            _cargado = true,
            _expandido = espacio.Expandido,
        };
        foreach (var carpeta in espacio.Carpetas)
            nodo.Hijos.Add(new NodoArchivo(carpeta, esCarpeta: true, nodo) { EsRaiz = true, Nombre = NombreDeCarpeta(carpeta) });
        return nodo;
    }

    // La raíz de una unidad (p. ej. "D:\") no tiene nombre de carpeta: se muestra la ruta.
    public static string NombreDeCarpeta(string carpeta) => Path.GetFileName(carpeta) is { Length: > 0 } nombre ? nombre : carpeta;

    public ObservableCollection<NodoArchivo> Hijos { get; } = [];
    public string Icono => Espacio is not null ? Espacio.Icono : _iconoGrupo ?? (EsCarpeta ? (Expandido ? "\uE838" : "\uE8B7") : "\uE8A5");

    public bool Expandido
    {
        get => _expandido;
        set
        {
            if (_expandido == value)
                return;
            _expandido = value;
            // Lo expandido de cada espacio se recuerda entre sesiones.
            if (Espacio is not null)
                Espacio.Expandido = value;
            if (value && !_cargado)
            {
                _cargado = true;
                Hijos.Clear();
                foreach (var hijo in Listar(Ruta, this))
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

    private static List<NodoArchivo> Listar(string carpeta, NodoArchivo padre)
    {
        var nodos = new List<NodoArchivo>();
        try
        {
            var info = new DirectoryInfo(carpeta);
            foreach (var sub in info.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
                if (!sub.Attributes.HasFlag(FileAttributes.Hidden) && !Ignoradas.Contains(sub.Name))
                    nodos.Add(new NodoArchivo(sub.FullName, esCarpeta: true, padre));
            foreach (var archivo in info.EnumerateFiles().OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
                if (Documentos.EsMarkdown(archivo.Name))
                    nodos.Add(new NodoArchivo(archivo.FullName, esCarpeta: false, padre));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Carpeta sin permiso o desaparecida: se muestra vacía.
        }
        return nodos;
    }

    // Expande las carpetas que llevan hasta la ruta y marca el archivo como actual.
    // Los espacios de trabajo no se expanden solos: se respeta si el usuario los plegó.
    public static void Revelar(IEnumerable<NodoArchivo> nodos, string? ruta)
    {
        foreach (var nodo in nodos)
        {
            if (ruta is not null && nodo.EsCarpeta && ruta.StartsWith(nodo.Ruta + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                nodo.Expandido = true;
            nodo.EsActual = !nodo.EsCarpeta && !nodo.EsEspacio && string.Equals(nodo.Ruta, ruta, StringComparison.OrdinalIgnoreCase);
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
