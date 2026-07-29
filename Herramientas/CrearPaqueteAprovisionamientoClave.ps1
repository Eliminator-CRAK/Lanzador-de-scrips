# (Autor: Alex Roman)
# Descripcion: Crea una vez el paquete DPAPI-NG para los equipos autorizados del dominio.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$GrupoDominio,

    [ValidateNotNullOrEmpty()]
    [string]$RutaCarpetaPermisos = '\\MAD002MICROPRU.mad.ae.aena.es\R$\PERMISOS'
)

$ErrorActionPreference = 'Stop'

$identidad = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identidad)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Ejecute este script desde una consola de PowerShell abierta como administrador.'
}

$raiz = Split-Path -Parent $PSScriptRoot
$proyecto = Join-Path $raiz 'LanzadorScripts.csproj'
$rutaClaveLocal = Join-Path $env:ProgramData 'LanzadorScripts\Seguridad\artefactos.key'
if (-not (Test-Path -LiteralPath $rutaClaveLocal -PathType Leaf)) {
    throw 'El equipo administrador no tiene la clave local. Aprovisionela una sola vez antes de crear el paquete central.'
}

if (-not (Test-Path -LiteralPath $RutaCarpetaPermisos -PathType Container)) {
    throw "No se encontro la carpeta central de permisos: $RutaCarpetaPermisos"
}

foreach ($nombre in @('permisos.json', 'catalogo-scripts.json')) {
    $rutaArtefacto = Join-Path $RutaCarpetaPermisos $nombre
    if (-not (Test-Path -LiteralPath $rutaArtefacto -PathType Leaf)) {
        throw "No se encontro el artefacto requerido: $rutaArtefacto"
    }
}

try {
    if ($GrupoDominio -match '^S-\d+(?:-\d+)+$') {
        $sid = [Security.Principal.SecurityIdentifier]::new($GrupoDominio)
    } else {
        $cuenta = [Security.Principal.NTAccount]::new($GrupoDominio)
        $sid = $cuenta.Translate([Security.Principal.SecurityIdentifier])
    }
} catch {
    throw "No se pudo resolver el grupo de Active Directory '$GrupoDominio'. Ejecute la herramienta con acceso al dominio."
}

if (-not $sid.Value.StartsWith('S-1-5-21-', [StringComparison]::Ordinal)) {
    throw 'El grupo autorizado debe pertenecer a un dominio de Active Directory.'
}

$fuenteCertificado = Get-Content -LiteralPath (Join-Path $raiz 'Servicios\ServicioTokenMaestro.cs') -Raw
$coincidenciaHuella = [regex]::Match(
    $fuenteCertificado,
    'HuellaCertificado\s*=\s*"(?<huella>[A-Fa-f0-9]+)"')
if (-not $coincidenciaHuella.Success) {
    throw 'No se pudo obtener la huella del certificado de artefactos.'
}

$huella = $coincidenciaHuella.Groups['huella'].Value
$certificado = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My |
    Where-Object {
        $_.Thumbprint -eq $huella -and
        $_.HasPrivateKey
    } |
    Select-Object -First 1
if ($null -eq $certificado) {
    throw "No se encontro el certificado privado de artefactos $huella."
}

& dotnet build $proyecto -c Release -r win-x64 --self-contained true
if ($LASTEXITCODE -ne 0) {
    throw "La compilacion termino con codigo $LASTEXITCODE."
}

$ensamblado = Join-Path $raiz 'bin\Release\net10.0-windows\win-x64\LanzadorScripts.dll'
if (-not (Test-Path -LiteralPath $ensamblado -PathType Leaf)) {
    throw "No se encontro el ensamblado generador: $ensamblado"
}

$descriptor = "SID=$($sid.Value)"
$descriptorBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($descriptor))
$permisosBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($RutaCarpetaPermisos))
& dotnet $ensamblado `
    --generar-paquete-clave-artefactos `
    --descriptor-base64 $descriptorBase64 `
    --permisos-base64 $permisosBase64
if ($LASTEXITCODE -ne 0) {
    throw "La generacion del paquete termino con codigo $LASTEXITCODE."
}

$rutaPaquete = Join-Path $RutaCarpetaPermisos 'clave-artefactos.dpng.json'
if (-not (Test-Path -LiteralPath $rutaPaquete -PathType Leaf)) {
    throw "No se genero el paquete central: $rutaPaquete"
}

Write-Host "Paquete central creado para $GrupoDominio en $rutaPaquete"
