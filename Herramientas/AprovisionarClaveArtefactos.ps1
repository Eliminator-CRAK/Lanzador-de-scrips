# (Autor: Alex Roman)
# Descripcion: Aprovisiona de forma interactiva la clave AES protegida para este equipo.

$ErrorActionPreference = 'Stop'

$identidad = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identidad)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Ejecute este script desde una consola de PowerShell abierta como administrador.'
}

$rutaSeguridad = Join-Path $env:ProgramData 'LanzadorScripts\Seguridad'
$rutaClave = Join-Path $rutaSeguridad 'artefactos.key'
$administradores = [Security.Principal.SecurityIdentifier]::new(
    [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
    $null)
$sistema = [Security.Principal.SecurityIdentifier]::new(
    [Security.Principal.WellKnownSidType]::LocalSystemSid,
    $null)
$entropiaOrigen = [Text.Encoding]::UTF8.GetBytes('LanzadorScripts|clave-artefactos|v2')
$sha256 = [Security.Cryptography.SHA256]::Create()
$entropia = $sha256.ComputeHash($entropiaOrigen)
$sha256.Dispose()
[Security.Cryptography.CryptographicOperations]::ZeroMemory($entropiaOrigen)

function Assert-NoReparsePoint {
    param([string]$Ruta)

    # Rechaza enlaces de sistema antes de escribir la clave.
    if (Test-Path -LiteralPath $Ruta) {
        $atributos = (Get-Item -LiteralPath $Ruta -Force).Attributes
        if (($atributos -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "La ruta de seguridad no puede ser un enlace de sistema: $Ruta"
        }
    }
}

function Set-DirectorioAdministrativo {
    param([string]$Ruta)

    # Limita la carpeta a administradores y al sistema.
    New-Item -ItemType Directory -Path $Ruta -Force | Out-Null
    $seguridad = [Security.AccessControl.DirectorySecurity]::new()
    $seguridad.SetAccessRuleProtection($true, $false)
    $herencia = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach ($sid in @($administradores, $sistema)) {
        $regla = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $herencia,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $seguridad.AddAccessRule($regla)
    }

    $seguridad.SetOwner($administradores)
    $directorio = [IO.DirectoryInfo]::new($Ruta)
    [IO.FileSystemAclExtensions]::SetAccessControl($directorio, $seguridad)
}

function Set-ArchivoAdministrativo {
    param([string]$Ruta)

    # Limita el archivo a administradores y al sistema.
    $seguridad = [Security.AccessControl.FileSecurity]::new()
    $seguridad.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($administradores, $sistema)) {
        $regla = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow)
        $seguridad.AddAccessRule($regla)
    }

    $seguridad.SetOwner($administradores)
    $archivo = [IO.FileInfo]::new($Ruta)
    [IO.FileSystemAclExtensions]::SetAccessControl($archivo, $seguridad)
}

if (Test-Path -LiteralPath $rutaClave -PathType Leaf) {
    Assert-NoReparsePoint -Ruta $rutaClave
    $confirmacion = Read-Host 'La clave ya existe. Escriba REEMPLAZAR para continuar'
    if ($confirmacion -cne 'REEMPLAZAR') {
        Write-Host 'No se modifico la clave existente.'
        exit 0
    }
}

$entradaSegura = Read-Host 'Introduzca la clave AES de 32 bytes en Base64' -AsSecureString
$puntero = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($entradaSegura)
$clave = $null
$protegida = $null
try {
    $entradaBase64 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($puntero)
    try {
        $clave = [Convert]::FromBase64String($entradaBase64)
    } catch {
        throw 'La entrada no contiene Base64 valido.'
    }

    if ($clave.Length -ne 32) {
        throw 'La clave debe contener exactamente 32 bytes.'
    }

    $protegida = [Security.Cryptography.ProtectedData]::Protect(
        $clave,
        $entropia,
        [Security.Cryptography.DataProtectionScope]::LocalMachine)
    $formato = [ordered]@{
        version = 1
        ambito = 'LocalMachine'
        claveProtegida = [Convert]::ToBase64String($protegida)
    }
    $contenido = $formato | ConvertTo-Json

    Assert-NoReparsePoint -Ruta (Split-Path -Parent $rutaSeguridad)
    Assert-NoReparsePoint -Ruta $rutaSeguridad
    Set-DirectorioAdministrativo -Ruta $rutaSeguridad
    $temporal = Join-Path $rutaSeguridad ('.artefactos.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporal, $contenido, [Text.UTF8Encoding]::new($false))
        Set-ArchivoAdministrativo -Ruta $temporal
        Move-Item -LiteralPath $temporal -Destination $rutaClave -Force
        Set-ArchivoAdministrativo -Ruta $rutaClave
    } finally {
        if (Test-Path -LiteralPath $temporal -PathType Leaf) {
            Remove-Item -LiteralPath $temporal -Force
        }
    }
} finally {
    if ($null -ne $clave) {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($clave)
    }

    if ($null -ne $protegida) {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($protegida)
    }

    [Security.Cryptography.CryptographicOperations]::ZeroMemory($entropia)
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($puntero)
    $entradaSegura.Dispose()
}

Write-Host "Clave aprovisionada en $rutaClave"
