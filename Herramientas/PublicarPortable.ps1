# (Autor: Alex Roman)
# Descripcion: Publica el ejecutable portable con WebView2 Runtime embebido.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$raiz = Split-Path -Parent $PSScriptRoot
$proyecto = Join-Path $raiz 'LanzadorScripts.csproj'
$carpetaWebView2 = Join-Path $raiz 'Recursos\WebView2'
$instaladorWebView2 = Join-Path $carpetaWebView2 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
$urlWebView2 = 'https://go.microsoft.com/fwlink/p/?LinkId=2124701'
$salida = Join-Path $raiz 'publicacion'

New-Item -ItemType Directory -Force -Path $carpetaWebView2 | Out-Null

if (-not (Test-Path -LiteralPath $instaladorWebView2)) {
    Write-Host 'Descargando Microsoft Edge WebView2 Runtime Evergreen Standalone x64...'
    Invoke-WebRequest -Uri $urlWebView2 -OutFile $instaladorWebView2
}

$tamano = (Get-Item -LiteralPath $instaladorWebView2).Length
if ($tamano -lt 104857600) {
    Remove-Item -LiteralPath $instaladorWebView2 -Force
    Write-Host 'Descargando Microsoft Edge WebView2 Runtime Evergreen Standalone x64...'
    Invoke-WebRequest -Uri $urlWebView2 -OutFile $instaladorWebView2
    $tamano = (Get-Item -LiteralPath $instaladorWebView2).Length
}

if ($tamano -lt 104857600) {
    throw 'El instalador de WebView2 descargado no parece valido.'
}

Write-Host 'Compilando aplicacion...'
dotnet build $proyecto

Write-Host 'Ejecutando pruebas...'
dotnet run --project (Join-Path $raiz 'Pruebas\LanzadorScripts.Pruebas.csproj')

Write-Host 'Publicando ejecutable portable...'
if (Test-Path -LiteralPath $salida) {
    Remove-Item -LiteralPath $salida -Recurse -Force
}

dotnet publish $proyecto `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $salida

$exe = Join-Path $salida 'LanzadorScripts.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw 'No se genero LanzadorScripts.exe.'
}

Write-Host "EXE generado: $exe"
