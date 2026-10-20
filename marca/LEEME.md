# Marca skolldev

Logo del desarrollador en distintas versiones. Todas tienen fondo transparente salvo las insignias.

| Carpeta | Uso | Temas | Tamaños (px) |
|---|---|---|---|
| `icono/` | Solo el lobo, dibujo de líneas | `claro`, `oscuro` | 16 a 1024 |
| `silueta/` | Lobo relleno; para 32 px o menos, donde las líneas se pierden | `claro`, `oscuro` | 16 a 1024 |
| `insignia/` | Lobo sobre cuadrado redondeado (avatares, íconos de app). Usa la silueta hasta 32 px | `claro`, `oscuro`, `acento` | 16 a 1024 |
| `horizontal/` | Lobo + «skolldev» a la derecha (pies de página, encabezados) | `claro`, `oscuro` | alto 64, 128, 256 |
| `apilado/` | Lobo con «skolldev» debajo (pantallas de presentación) | `claro`, `oscuro` | ancho 256, 512, 1024 |
| `skolldev-oscuro.ico`, `skolldev-acento.ico` | Ícono de Windows multitamaño (16–256) | | |

**Temas:** `claro` = trazo oscuro `#1F2328`, para fondos claros · `oscuro` = trazo claro `#E6EDF3`, para fondos oscuros · `acento` = lobo blanco sobre el azul `#0078D4` de Lupa MD.

Para regenerarlas desde el PNG original (trazo negro sobre fondo transparente):

```powershell
.\marca\generar-marca.ps1 -Origen "ruta\al\logo.png" -Destino .\marca\skolldev
```
