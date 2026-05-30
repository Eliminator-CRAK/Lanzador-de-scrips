# (Autor: Alex Roman)
# Descripcion: Publica el ejecutable portable con validacion WebView2 y firma opcional.

param(
    [string]$CertThumbprint = '',
    [string]$CertPath = '',
    [securestring]$CertPassword,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$raiz = Split-Path -Parent $PSScriptRoot
$proyecto = Join-Path $raiz 'LanzadorScripts.csproj'
$carpetaWebView2 = Join-Path $raiz 'Recursos\WebView2'
$instaladorWebView2 = Join-Path $carpetaWebView2 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
$integridadWebView2 = Join-Path $raiz 'Servicios\IntegridadWebView2.cs'
$urlWebView2 = 'https://go.microsoft.com/fwlink/p/?LinkId=2124701'
$salida = Join-Path $raiz 'publicacion'
$tamanoMinimoExe = 209715200

function Assert-WebView2Installer {
    param([string]$Path)

    $tamano = (Get-Item -LiteralPath $Path).Length
    if ($tamano -lt 104857600) {
        throw 'El instalador de WebView2 descargado no parece valido.'
    }

    $firma = Get-AuthenticodeSignature -LiteralPath $Path
    if ($firma.Status -ne 'Valid') {
        throw "La firma Authenticode del instalador WebView2 no es valida: $($firma.Status)."
    }

    if ($null -eq $firma.SignerCertificate -or $firma.SignerCertificate.Subject -notlike '*Microsoft Corporation*') {
        throw 'El instalador WebView2 no esta firmado por Microsoft Corporation.'
    }

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Update-WebView2HashSource {
    param([string]$Hash)

    $contenido = @"
// (Autor: Alex Roman)
// Descripcion: Constantes de integridad del instalador WebView2 embebido.

namespace LanzadorScripts.Servicios;

public static class IntegridadWebView2
{
    public const string Sha256Instalador = "$Hash";
}
"@

    Set-Content -LiteralPath $integridadWebView2 -Value $contenido -Encoding UTF8
}

function Get-SigningCertificate {
    if (-not [string]::IsNullOrWhiteSpace($CertThumbprint)) {
        $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My |
            Where-Object { $_.Thumbprint -eq $CertThumbprint } |
            Select-Object -First 1

        if ($null -eq $cert) {
            throw "No se encontro el certificado de firma con thumbprint $CertThumbprint."
        }

        return $cert
    }

    if (-not [string]::IsNullOrWhiteSpace($CertPath)) {
        if ($null -eq $CertPassword) {
            return Get-PfxCertificate -FilePath $CertPath
        }

        return Get-PfxCertificate -FilePath $CertPath -Password $CertPassword
    }

    return $null
}

New-Item -ItemType Directory -Force -Path $carpetaWebView2 | Out-Null

if (-not (Test-Path -LiteralPath $instaladorWebView2)) {
    Write-Host 'Descargando Microsoft Edge WebView2 Runtime Evergreen Standalone x64...'
    Invoke-WebRequest -Uri $urlWebView2 -OutFile $instaladorWebView2
}

try {
    $hashWebView2 = Assert-WebView2Installer -Path $instaladorWebView2
} catch {
    Remove-Item -LiteralPath $instaladorWebView2 -Force -ErrorAction SilentlyContinue
    Write-Host 'Descargando Microsoft Edge WebView2 Runtime Evergreen Standalone x64...'
    Invoke-WebRequest -Uri $urlWebView2 -OutFile $instaladorWebView2
    $hashWebView2 = Assert-WebView2Installer -Path $instaladorWebView2
}

Update-WebView2HashSource -Hash $hashWebView2
Write-Host "WebView2 validado. SHA-256: $hashWebView2"

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

$certificadoFirma = Get-SigningCertificate
if ($null -ne $certificadoFirma) {
    Write-Host 'Firmando ejecutable Authenticode...'
    $firmaExe = Set-AuthenticodeSignature -FilePath $exe -Certificate $certificadoFirma -TimestampServer $TimestampServer
    if ($firmaExe.Status -ne 'Valid') {
        throw "No se pudo firmar el EXE correctamente: $($firmaExe.Status)."
    }
} else {
    Write-Warning 'No se indico certificado Authenticode. El EXE se publicara sin firma.'
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
