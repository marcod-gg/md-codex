param([string]$Origen, [string]$Destino)
# Genera las versiones del logo de skolldev a partir del PNG original (trazo negro sobre transparente).
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$colores = @{
    claro  = @{ Trazo = '#1F2328'; Fondo = '#F6F8FA' }   # para fondos claros
    oscuro = @{ Trazo = '#E6EDF3'; Fondo = '#0D1117' }   # para fondos oscuros
    acento = @{ Trazo = '#FFFFFF'; Fondo = '#0078D4' }   # insignia con el azul de MD Codex
}
function Color($hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

$original = New-Object System.Drawing.Bitmap $Origen

# Silueta: rellena el interior del contorno (todo lo que no se alcanza desde el borde por zonas transparentes).
# A 16-32 px el dibujo de líneas se vuelve una mancha; la silueta del perfil se sigue reconociendo.
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Collections.Generic; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class Silueta {
    public static Bitmap Crear(Bitmap fuente, int lado) {
        var b = new Bitmap(lado, lado, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(fuente, 0, 0, lado, lado); }
        var d = b.LockBits(new Rectangle(0, 0, lado, lado), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var px = new int[lado * lado]; Marshal.Copy(d.Scan0, px, 0, px.Length);
        var fuera = new bool[px.Length]; var cola = new Queue<int>();
        Func<int, bool> vacio = i => ((px[i] >> 24) & 0xFF) < 128;
        for (int i = 0; i < lado; i++) foreach (var p in new[] { i, (lado - 1) * lado + i, i * lado, i * lado + lado - 1 })
            if (!fuera[p] && vacio(p)) { fuera[p] = true; cola.Enqueue(p); }
        while (cola.Count > 0) {
            int p = cola.Dequeue(), x = p % lado, y = p / lado;
            foreach (var q in new[] { x > 0 ? p - 1 : -1, x < lado - 1 ? p + 1 : -1, y > 0 ? p - lado : -1, y < lado - 1 ? p + lado : -1 })
                if (q >= 0 && !fuera[q] && vacio(q)) { fuera[q] = true; cola.Enqueue(q); }
        }
        // Dentro: opaco; fuera: se conserva el alfa original del borde para mantener el antialias.
        for (int i = 0; i < px.Length; i++) px[i] = fuera[i] ? px[i] : unchecked((int)0xFF000000);
        Marshal.Copy(px, 0, d.Scan0, px.Length); b.UnlockBits(d); return b;
    }
}
"@
$silueta = [Silueta]::Crear($original, 1024)

# Dibuja el lobo recoloreado (conserva solo el canal alfa del original) en el rectángulo indicado.
function DibujarLobo($g, $hex, [float]$x, [float]$y, [float]$lado, $imagen = $original) {
    $c = Color $hex
    $m = New-Object System.Drawing.Imaging.ColorMatrix
    $m.Matrix00 = 0; $m.Matrix11 = 0; $m.Matrix22 = 0; $m.Matrix33 = 1; $m.Matrix44 = 1
    $m.Matrix40 = $c.R / 255; $m.Matrix41 = $c.G / 255; $m.Matrix42 = $c.B / 255
    $attr = New-Object System.Drawing.Imaging.ImageAttributes; $attr.SetColorMatrix($m)
    $rect = New-Object System.Drawing.Rectangle ([int]$x), ([int]$y), ([int]$lado), ([int]$lado)
    $g.DrawImage($imagen, $rect, 0, 0, $imagen.Width, $imagen.Height, [System.Drawing.GraphicsUnit]::Pixel, $attr)
}

function Lienzo([int]$ancho, [int]$alto) {
    $bmp = New-Object System.Drawing.Bitmap $ancho, $alto, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.CompositingQuality = 'HighQuality'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)
    return $bmp, $g
}

function Guardar($bmp, $g, $ruta) {
    $g.Dispose()
    New-Item -ItemType Directory -Force (Split-Path $ruta) | Out-Null
    $bmp.Save($ruta, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}

function CuadradoRedondeado([float]$x, [float]$y, [float]$lado, [float]$radio) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $d = $radio * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $lado - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $lado - $d, $y + $lado - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $lado - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

$fuente = 'Segoe UI Variable Display'
if (-not ([System.Drawing.FontFamily]::Families.Name -contains $fuente)) { $fuente = 'Segoe UI' }

# 1. Ícono: solo el lobo
foreach ($tema in 'claro', 'oscuro') {
    foreach ($t in 16, 24, 32, 48, 64, 128, 256, 512, 1024) {
        $bmp, $g = Lienzo $t $t
        DibujarLobo $g $colores[$tema].Trazo 0 0 $t
        Guardar $bmp $g "$Destino\icono\skolldev-icono-$tema-$t.png"

        $bmp, $g = Lienzo $t $t
        DibujarLobo $g $colores[$tema].Trazo 0 0 $t $silueta
        Guardar $bmp $g "$Destino\silueta\skolldev-silueta-$tema-$t.png"
    }
}

# 2. Insignia: lobo sobre cuadrado redondeado
foreach ($tema in 'claro', 'oscuro', 'acento') {
    foreach ($t in 16, 24, 32, 48, 64, 128, 256, 512, 1024) {
        $bmp, $g = Lienzo $t $t
        $fondo = New-Object System.Drawing.SolidBrush (Color $colores[$tema].Fondo)
        $g.FillPath($fondo, (CuadradoRedondeado 0 0 $t ($t * 0.22)))
        $lobo = $t * 0.74
        $imagen = if ($t -le 32) { $silueta } else { $original }
        DibujarLobo $g $colores[$tema].Trazo (($t - $lobo) / 2) (($t - $lobo) / 2) $lobo $imagen
        Guardar $bmp $g "$Destino\insignia\skolldev-insignia-$tema-$t.png"
    }
}

# 3. Horizontal: lobo + nombre
foreach ($tema in 'claro', 'oscuro') {
    foreach ($alto in 64, 128, 256) {
        $letra = New-Object System.Drawing.Font $fuente, ($alto * 0.42), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $medida = New-Object System.Drawing.Bitmap 1, 1; $gm = [System.Drawing.Graphics]::FromImage($medida)
        $tam = $gm.MeasureString('skolldev', $letra, [System.Drawing.PointF]::Empty, [System.Drawing.StringFormat]::GenericTypographic)
        $gm.Dispose(); $medida.Dispose()
        $separacion = $alto * 0.18
        $bmp, $g = Lienzo ([int]($alto + $separacion + $tam.Width + $alto * 0.08)) $alto
        DibujarLobo $g $colores[$tema].Trazo 0 0 $alto
        $pincel = New-Object System.Drawing.SolidBrush (Color $colores[$tema].Trazo)
        $g.DrawString('skolldev', $letra, $pincel, ($alto + $separacion), (($alto - $tam.Height) / 2), [System.Drawing.StringFormat]::GenericTypographic)
        Guardar $bmp $g "$Destino\horizontal\skolldev-horizontal-$tema-$alto.png"
    }
}

# 4. Apilado: lobo arriba, nombre debajo
foreach ($tema in 'claro', 'oscuro') {
    foreach ($ancho in 256, 512, 1024) {
        $letra = New-Object System.Drawing.Font $fuente, ($ancho * 0.16), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $lobo = $ancho * 0.72
        $bmp, $g = Lienzo $ancho ([int]($lobo + $ancho * 0.30))
        DibujarLobo $g $colores[$tema].Trazo (($ancho - $lobo) / 2) 0 $lobo
        $formato = New-Object System.Drawing.StringFormat ([System.Drawing.StringFormat]::GenericTypographic); $formato.Alignment = 'Center'
        $pincel = New-Object System.Drawing.SolidBrush (Color $colores[$tema].Trazo)
        $g.DrawString('skolldev', $letra, $pincel, (New-Object System.Drawing.RectangleF 0, ($lobo + $ancho * 0.04), $ancho, ($ancho * 0.26)), $formato)
        Guardar $bmp $g "$Destino\apilado\skolldev-apilado-$tema-$ancho.png"
    }
}

# 5. .ico multitamaño (entradas PNG, válidas desde Windows Vista)
function CrearIco($pngs, $ruta) {
    $datos = $pngs | ForEach-Object { , [System.IO.File]::ReadAllBytes($_.Ruta) }
    $fs = [System.IO.File]::Create($ruta); $w = New-Object System.IO.BinaryWriter $fs
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
    $offset = 6 + 16 * $pngs.Count
    for ($i = 0; $i -lt $pngs.Count; $i++) {
        $t = $pngs[$i].Tam; $b = if ($t -ge 256) { 0 } else { $t }
        $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$datos[$i].Length); $w.Write([uint32]$offset)
        $offset += $datos[$i].Length
    }
    foreach ($d in $datos) { $w.Write($d) }
    $w.Close()
}
foreach ($tema in 'oscuro', 'acento') {
    $lista = 16, 24, 32, 48, 64, 128, 256 | ForEach-Object { [pscustomobject]@{ Tam = $_; Ruta = "$Destino\insignia\skolldev-insignia-$tema-$_.png" } }
    CrearIco $lista "$Destino\skolldev-$tema.ico"
}

$original.Dispose(); $silueta.Dispose()
Get-ChildItem -Recurse $Destino -File | Measure-Object Length -Sum | ForEach-Object { "archivos: $($_.Count) | total: {0:N0} KB" -f ($_.Sum / 1KB) }
