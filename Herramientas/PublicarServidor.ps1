# (Autor: Alex Roman)
# Descripcion: Publica el paquete autocontenido y firmado de LanzadorScripts Servidor.

[CmdletBinding()]
param(
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$CertThumbprint = '6C654649369000DDE0AA70F62645058D9A3437F5',

    [ValidatePattern('^https?://')]
    [string]$TimestampServer = 'http://timestamp.digicert.com',

    [switch]$AllowUnsignedForDev
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$version = '1.8.0'
$raiz = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$obj = [System.IO.Path]::GetFullPath((Join-Path $raiz 'obj\PublicacionServidor'))
$staging = [System.IO.Path]::GetFullPath((Join-Path $obj 'Paquete'))
$salidaTemporal = [System.IO.Path]::GetFullPath((Join-Path $obj 'Salida'))
$salida = [System.IO.Path]::GetFullPath((Join-Path $raiz 'publicacion-servidor'))
$nombreZip = "LanzadorScripts_Servidor-$version-x64.zip"
$zipTemporal = Join-Path $salidaTemporal $nombreZip
$proyectoServicio = Join-Path $raiz 'Servidor\LanzadorScripts.Servidor.Servicio\LanzadorScripts.Servidor.Servicio.csproj'
$proyectoAdministracion = Join-Path $raiz 'Servidor\LanzadorScripts.Servidor.Administracion\LanzadorScripts.Servidor.Administracion.csproj'
$publicacionServicio = Join-Path $obj 'ServicioPublicado'
$publicacionAdministracion = Join-Path $obj 'AdministracionPublicada'
$scriptFirma = Join-Path $PSScriptRoot 'FirmarPublicacionInstalada.ps1'
$distribucion = Join-Path $raiz 'Servidor\Distribucion'

function Assert-RutaInterna {
    param(
        [Parameter(Mandatory)][string]$Ruta,
        [Parameter(Mandatory)][string]$RaizPermitida
    )

    $completa = [System.IO.Path]::GetFullPath($Ruta)
    $permitida = [System.IO.Path]::GetFullPath($RaizPermitida).TrimEnd('\') + '\'
    if (-not $completa.StartsWith($permitida, [StringComparison]::OrdinalIgnoreCase)) {
        throw "La ruta queda fuera de su raiz permitida: $completa"
    }
}

function Remove-CarpetaSegura {
    param(
        [Parameter(Mandatory)][string]$Ruta,
        [Parameter(Mandatory)][string]$RaizPermitida
    )

    Assert-RutaInterna -Ruta $Ruta -RaizPermitida $RaizPermitida
    if (-not [System.IO.Directory]::Exists($Ruta)) {
        return
    }

    if (([System.IO.DirectoryInfo]::new($Ruta).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        @(Get-ChildItem -LiteralPath $Ruta -Recurse -Force | Where-Object {
            ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
        }).Count -gt 0) {
        throw "No se elimina una carpeta que contiene puntos de reanalisis: $Ruta"
    }

    Remove-Item -LiteralPath $Ruta -Recurse -Force
}

function Invoke-DotnetPublish {
    param(
        [Parameter(Mandatory)][string]$Proyecto,
        [Parameter(Mandatory)][string]$Destino,
        [Parameter(Mandatory)][string]$Revision
    )

    & dotnet publish $Proyecto `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -o $Destino `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishTrimmed=false `
        -p:DebugType=embedded `
        "-p:InformationalVersion=$version+$Revision.server"
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo publicar $Proyecto."
    }
}

function New-ZipDeterminista {
    param(
        [Parameter(Mandatory)][string]$Origen,
        [Parameter(Mandatory)][string]$Destino
    )

    Add-Type -AssemblyName System.IO.Compression
    $flujoZip = [System.IO.File]::Open(
        $Destino,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $zip = [System.IO.Compression.ZipArchive]::new(
            $flujoZip,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false,
            [System.Text.UTF8Encoding]::new($false))
        try {
            foreach ($archivo in Get-ChildItem -LiteralPath $Origen -Recurse -File | Sort-Object FullName) {
                $relativa = [System.IO.Path]::GetRelativePath($Origen, $archivo.FullName).Replace('\', '/')
                $entrada = $zip.CreateEntry(
                    $relativa,
                    [System.IO.Compression.CompressionLevel]::Optimal)
                $entrada.LastWriteTime = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $origenFlujo = [System.IO.File]::OpenRead($archivo.FullName)
                $destinoFlujo = $entrada.Open()
                try {
                    $origenFlujo.CopyTo($destinoFlujo)
                }
                finally {
                    $destinoFlujo.Dispose()
                    $origenFlujo.Dispose()
                }
            }
        }
        finally {
            $zip.Dispose()
        }
    }
    finally {
        $flujoZip.Dispose()
    }
}

foreach ($archivo in @(
        $proyectoServicio,
        $proyectoAdministracion,
        $scriptFirma,
        (Join-Path $distribucion 'Instalar-Servidor.ps1'),
        (Join-Path $distribucion 'Desinstalar-Servidor.ps1'),
        (Join-Path $distribucion 'Crear-ConfiguracionCliente.ps1'),
        (Join-Path $distribucion 'LEEME-Servidor.txt'))) {
    if (-not [System.IO.File]::Exists($archivo)) {
        throw "Falta un archivo necesario para publicar el servidor: $archivo"
    }
}

$revision = (& git -C $raiz rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[0-9a-f]{40}$') {
    throw 'No se pudo obtener la revision Git de la publicacion servidor.'
}

$cambios = @(& git -C $raiz status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $cambios.Count -gt 0) {
    throw 'La publicacion servidor final requiere un arbol Git limpio.'
}

Remove-CarpetaSegura -Ruta $obj -RaizPermitida (Join-Path $raiz 'obj')
if ([System.IO.Directory]::Exists($salida)) {
    if ([System.IO.Path]::GetFileName($salida) -ne 'publicacion-servidor') {
        throw 'La carpeta de salida del servidor no tiene el nombre esperado.'
    }
    Remove-CarpetaSegura -Ruta $salida -RaizPermitida $raiz
}

foreach ($carpeta in @($staging, $salidaTemporal, $publicacionServicio, $publicacionAdministracion)) {
    [System.IO.Directory]::CreateDirectory($carpeta) | Out-Null
}

Invoke-DotnetPublish -Proyecto $proyectoServicio -Destino $publicacionServicio -Revision $revision
Invoke-DotnetPublish -Proyecto $proyectoAdministracion -Destino $publicacionAdministracion -Revision $revision

$exeServicioOrigen = Join-Path $publicacionServicio 'LanzadorScripts.Servidor.Servicio.exe'
$exeAdministracionOrigen = Join-Path $publicacionAdministracion 'LanzadorScripts.Servidor.exe'
$exeServicio = Join-Path $staging 'Servicio\LanzadorScripts.Servidor.Servicio.exe'
$exeAdministracion = Join-Path $staging 'LanzadorScripts.Servidor.exe'
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($exeServicio)) | Out-Null
[System.IO.File]::Copy($exeServicioOrigen, $exeServicio, $false)
[System.IO.File]::Copy($exeAdministracionOrigen, $exeAdministracion, $false)
foreach ($nombre in @('Instalar-Servidor.ps1', 'Desinstalar-Servidor.ps1', 'Crear-ConfiguracionCliente.ps1', 'LEEME-Servidor.txt')) {
    [System.IO.File]::Copy((Join-Path $distribucion $nombre), (Join-Path $staging $nombre), $false)
}

$firmar = -not $AllowUnsignedForDev
if ($firmar) {
    foreach ($archivo in @(
            $exeServicio,
            $exeAdministracion,
            (Join-Path $staging 'Instalar-Servidor.ps1'),
            (Join-Path $staging 'Desinstalar-Servidor.ps1'),
            (Join-Path $staging 'Crear-ConfiguracionCliente.ps1'))) {
        & $scriptFirma -RutaArchivo $archivo -Thumbprint $CertThumbprint -TimestampServer $TimestampServer
        if ($LASTEXITCODE -ne 0) {
            throw "No se pudo firmar $archivo."
        }
    }

    $certificado = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $CertThumbprint } |
        Select-Object -First 1
    if ($null -eq $certificado) {
        throw 'No se encontro el certificado publico de la firma Authenticode.'
    }
    Export-Certificate `
        -Cert $certificado `
        -FilePath (Join-Path $staging 'LanzadorScripts-CodeSigning-Public.cer') `
        -Type CERT | Out-Null
}

$lineasHash = Get-ChildItem -LiteralPath $staging -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        $relativa = [System.IO.Path]::GetRelativePath($staging, $_.FullName).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$hash  $relativa"
    }
[System.IO.File]::WriteAllLines(
    (Join-Path $staging 'SHA256SUMS.txt'),
    $lineasHash,
    [System.Text.UTF8Encoding]::new($false))

New-ZipDeterminista -Origen $staging -Destino $zipTemporal
[System.IO.Directory]::Move($salidaTemporal, $salida)
$zipFinal = Join-Path $salida $nombreZip
if (-not [System.IO.File]::Exists($zipFinal) -or (Get-Item $zipFinal).Length -lt 10MB) {
    throw 'El ZIP servidor no se genero correctamente.'
}

Write-Host "Paquete servidor: $zipFinal"
Write-Host "SHA-256: $((Get-FileHash -LiteralPath $zipFinal -Algorithm SHA256).Hash)"
