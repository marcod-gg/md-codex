# VisorMD

Visor de archivos Markdown para Windows, hecho con WPF (.NET 10), WebView2 y Markdig.

## Qué hace

- Abre archivos `.md` con **Ctrl+O**, arrastrándolos a la ventana o pasándolos como argumento (sirve para "Abrir con…").
- Se actualiza solo cada vez que guardas el archivo (**F5** fuerza la recarga).
- Sigue el tema claro u oscuro de Windows.
- Muestra imágenes y enlaces relativos; los enlaces a otros `.md` se abren en el mismo visor y el resto, en la aplicación predeterminada.
- Soporta tablas, listas de tareas, notas al pie y emojis. Para hacer zoom, usa **Ctrl + rueda del mouse**.

## Uso

```powershell
dotnet run --project src\VisorMD -- docs\ejemplo.md
```

Para generar un ejecutable:

```powershell
dotnet publish src\VisorMD -c Release -r win-x64 --self-contained false -o publicar
```

Requiere el runtime de WebView2, que ya viene incluido en Windows 10 y 11.
