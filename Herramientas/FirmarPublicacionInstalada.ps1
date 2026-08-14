# (Autor: Alex Roman)
# Descripcion: Firma y valida ejecutables, instaladores y scripts de distribucion.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RutaArchivo,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$Thumbprint,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https?://')]
    [string]$TimestampServer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$rutaCompleta = [System.IO.Path]::GetFullPath($RutaArchivo)
if (-not [System.IO.File]::Exists($rutaCompleta)) {
    throw "No existe el archivo que debe firmarse: $rutaCompleta"
}

$extension = [System.IO.Path]::GetExtension($rutaCompleta)
if ($extension -notin @('.exe', '.msi', '.ps1')) {
    throw "Solo se admiten archivos EXE, MSI o PS1: $rutaCompleta"
}

$atributos = [System.IO.File]::GetAttributes($rutaCompleta)
if (($atributos -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "No se admite firmar un enlace o punto de reanalisis: $rutaCompleta"
}

$huella = $Thumbprint.ToUpperInvariant()
$certificado = Get-ChildItem -Path Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Thumbprint -eq $huella -and
        $_.HasPrivateKey -and
        ($_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3')
    } |
    Select-Object -First 1
if ($null -eq $certificado) {
    throw "No se encontro el certificado privado de firma $huella."
}

if ($certificado.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
    throw "El certificado de firma $huella esta caducado."
}

$firma = Set-AuthenticodeSignature `
    -FilePath $rutaCompleta `
    -Certificate $certificado `
    -HashAlgorithm SHA256 `
    -TimestampServer $TimestampServer
if ($firma.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "No se pudo firmar el archivo: $($firma.StatusMessage)"
}

$validacion = Get-AuthenticodeSignature -LiteralPath $rutaCompleta
if ($validacion.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $validacion.TimeStamperCertificate) {
    throw 'La firma Authenticode o su sello de tiempo no son validos.'
}

Write-Host "Archivo firmado: $rutaCompleta"
