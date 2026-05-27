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
$tamanoMinimoExe = 209715200

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
    -p:SelfContained=true `
    -p:PublishSelfContained=true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:UseAppHost=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $salida

$exe = Join-Path $salida 'LanzadorScripts.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw 'No se genero LanzadorScripts.exe.'
}

$archivosPublicados = @(Get-ChildItem -LiteralPath $salida -Recurse -File)
if ($archivosPublicados.Count -ne 1 -or $archivosPublicados[0].FullName -ne $exe) {
    $lista = ($archivosPublicados | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
    throw "La publicacion debe generar un unico EXE. Archivos encontrados:$([Environment]::NewLine)$lista"
}

$archivosLaterales = @($archivosPublicados | Where-Object {
    $_.Name -like '*.dll' -or
    $_.Name -like '*.deps.json' -or
    $_.Name -like '*.runtimeconfig.json'
})
if ($archivosLaterales.Count -gt 0) {
    $lista = ($archivosLaterales | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
    throw "La publicacion contiene archivos laterales de .NET:$([Environment]::NewLine)$lista"
}

$tamanoExe = (Get-Item -LiteralPath $exe).Length
if ($tamanoExe -lt $tamanoMinimoExe) {
    throw "El EXE generado parece incompleto. Tamano detectado: $tamanoExe bytes."
}

Write-Host 'Distribuye solo publicacion\LanzadorScripts.exe. No uses los EXE de bin\Debug ni bin\Release.'
Write-Host "EXE generado: $exe"
