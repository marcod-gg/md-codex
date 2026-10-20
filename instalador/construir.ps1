# Publica MD Codex (con el runtime de .NET incluido) y compila el instalador con Inno Setup.
# Uso: .\instalador\construir.ps1    Resultado: publicar\MDCodex-<version>-instalador.exe
# Requiere Inno Setup 6: winget install JRSoftware.InnoSetup
# (Textos sin acentos: Windows PowerShell 5.1 lee los scripts sin BOM como ANSI.)
$ErrorActionPreference = 'Stop'
$raiz = Split-Path $PSScriptRoot -Parent
$proyecto = Join-Path $raiz 'src\MdCodex\MdCodex.csproj'
$salida = Join-Path $raiz 'publicar\app'

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'No se encontro Inno Setup 6. Instalalo con: winget install JRSoftware.InnoSetup' }

$version = ([xml](Get-Content $proyecto)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "MD Codex $version"

Remove-Item $salida -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish $proyecto -c Release -r win-x64 --self-contained true -o $salida -p:DebugType=none -nologo
if ($LASTEXITCODE -ne 0) { throw 'Fallo dotnet publish' }

& $iscc "/DVersion=$version" /Qp (Join-Path $PSScriptRoot 'mdcodex.iss')
if ($LASTEXITCODE -ne 0) { throw 'Fallo la compilacion del instalador' }
Get-Item (Join-Path $raiz "publicar\MDCodex-$version-instalador.exe")
