# (Autor: Alex Roman)
# Descripcion: Genera un paquete cliente sin permisos, certificados ni secretos.

[CmdletBinding()]
param(
    [string]$ServidorCentral = 'MAD002MICROPRU.mad.ae.aena.es',

    [ValidateRange(1024, 65535)]
    [int]$Puerto = 47831,

    [string]$RutaScripts = '\\MAD002MICROPRU.mad.ae.aena.es\R$\SCRIPS',

    [string]$Salida = (Join-Path $PWD 'LanzadorScripts-Cliente.lanzadorconfig')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$servidor = $ServidorCentral.Trim().TrimEnd('.')
if ($servidor.Length -lt 1 -or $servidor.Length -gt 253 -or
    $servidor.Contains('\') -or $servidor.Contains('/') -or $servidor.Contains(':') -or
    @($servidor.Split('.') | Where-Object {
        $_.Length -lt 1 -or $_.Length -gt 63 -or
        $_[0] -eq '-' -or $_[$_.Length - 1] -eq '-' -or
        $_ -notmatch '^[A-Za-z0-9-]+$'
    }).Count -gt 0) {
    throw 'ServidorCentral no contiene un nombre DNS valido.'
}

if ($RutaScripts.Contains('..', [StringComparison]::Ordinal) -or
    -not [System.IO.Path]::IsPathFullyQualified($RutaScripts)) {
    throw 'RutaScripts debe ser una ruta absoluta sin segmentos de retroceso.'
}

$rutaSalida = [System.IO.Path]::GetFullPath($Salida)
if ([System.IO.Path]::GetExtension($rutaSalida) -ne '.lanzadorconfig') {
    throw 'La salida debe usar la extension .lanzadorconfig.'
}

$carpetaSalida = [System.IO.Path]::GetDirectoryName($rutaSalida)
[System.IO.Directory]::CreateDirectory($carpetaSalida) | Out-Null
if (([System.IO.DirectoryInfo]::new($carpetaSalida).Attributes -band
        [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'La carpeta de salida no puede ser un punto de reanalisis.'
}

$paquete = [ordered]@{
    autor = 'Alex Roman'
    descripcion = 'Conexion de LanzadorScripts con el servidor central.'
    version = 2
    tipo = 'configuracion-cliente'
    rutaScripts = [System.IO.Path]::GetFullPath($RutaScripts)
    servidorCentral = $servidor
    puertoServidorCentral = $Puerto
    creadoUtc = [DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json -Depth 4

[System.IO.File]::WriteAllText(
    $rutaSalida,
    $paquete,
    [System.Text.UTF8Encoding]::new($false))
Write-Host "Paquete cliente generado: $rutaSalida"
Write-Host "SPN Kerberos esperado: LanzadorScripts/$servidor"
