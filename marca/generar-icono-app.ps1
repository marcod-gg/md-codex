param([string]$Destino = (Join-Path $PSScriptRoot 'mdcodex'))
# Ícono de la app MD Codex: "M↓" blanco sobre cuadrado redondeado azul (#0078D4).
# Genera PNG de 16 a 256 px y un .ico multitamaño; a 16 px se dibuja solo la "M", la flecha no se distingue.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Destino | Out-Null

$fuente = 'Segoe UI Variable Display'
if (-not ([System.Drawing.FontFamily]::Families.Name -contains $fuente)) { $fuente = 'Segoe UI' }
$azul = [System.Drawing.ColorTranslator]::FromHtml('#0078D4')

function Cuadrado([float]$lado, [float]$radio) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $d = $radio * 2
    $p.AddArc(0, 0, $d, $d, 180, 90); $p.AddArc($lado - $d, 0, $d, $d, 270, 90)
    $p.AddArc($lado - $d, $lado - $d, $d, $d, 0, 90); $p.AddArc(0, $lado - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

$tamanos = 16, 20, 24, 32, 40, 48, 64, 128, 256
foreach ($t in $tamanos) {
    $bmp = New-Object System.Drawing.Bitmap $t, $t, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.FillPath((New-Object System.Drawing.SolidBrush $azul), (Cuadrado $t ($t * 0.22)))

    # La flecha va por código: Windows PowerShell 5.1 lee los scripts sin BOM como ANSI y la rompería.
    $texto = if ($t -le 16) { 'M' } else { 'M' + [char]0x2193 }
    $tam = if ($t -le 16) { $t * 0.66 } else { $t * 0.46 }
    $letra = New-Object System.Drawing.Font $fuente, $tam, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $formato = New-Object System.Drawing.StringFormat ([System.Drawing.StringFormat]::GenericTypographic)
    $formato.Alignment = 'Center'; $formato.LineAlignment = 'Center'
    $g.DrawString($texto, $letra, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ($t * 0.02), $t, $t), $formato)
    $g.Dispose()
    $bmp.Save((Join-Path $Destino "mdcodex-$t.png"), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}

# .ico con entradas PNG (válidas desde Windows Vista)
$ico = Join-Path $Destino 'mdcodex.ico'
$datos = $tamanos | ForEach-Object { , [System.IO.File]::ReadAllBytes((Join-Path $Destino "mdcodex-$_.png")) }
$w = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($ico))
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$tamanos.Count)
$offset = 6 + 16 * $tamanos.Count
for ($i = 0; $i -lt $tamanos.Count; $i++) {
    $b = if ($tamanos[$i] -ge 256) { 0 } else { $tamanos[$i] }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$datos[$i].Length); $w.Write([uint32]$offset)
    $offset += $datos[$i].Length
}
foreach ($d in $datos) { $w.Write($d) }
$w.Close()
"icono: $ico"
