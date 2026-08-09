# (Autor: Alex Roman)
# Descripcion: Prepara la carpeta remota de auditoria y sus permisos de creacion controlada.

[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$RutaPermisos = '\\MAD002MICROPRU.mad.ae.aena.es\R$\PERMISOS',
    [string]$IdentidadCreadores = 'S-1-5-11'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Administrador {
    # Exige una sesion elevada para cambiar ACL remotas.
    $identidad = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identidad)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Ejecute este script desde una consola elevada.'
    }
}

function Resolve-Identidad {
    param([string]$Valor)

    # Convierte nombres de dominio o SID en una identidad estable.
    if ($Valor -match '^S-\d-(?:\d+-)+\d+$') {
        return [Security.Principal.SecurityIdentifier]::new($Valor)
    }

    return [Security.Principal.NTAccount]::new($Valor).Translate(
        [Security.Principal.SecurityIdentifier])
}

function Assert-RutaSegura {
    param([string]$Ruta)

    # Rechaza navegacion y puntos de reanalisis en la ruta administrativa.
    if ([string]::IsNullOrWhiteSpace($Ruta) -or
        $Ruta.Contains('/') -or
        (($Ruta -split '[\\/]') | Where-Object { $_ -in '.', '..' })) {
        throw 'La ruta de permisos no es segura.'
    }

    if (-not [IO.Path]::IsPathFullyQualified($Ruta)) {
        throw 'La ruta de permisos debe ser absoluta.'
    }

    $completa = [IO.Path]::GetFullPath($Ruta).TrimEnd('\')
    $actual = [IO.Path]::GetPathRoot($completa)
    foreach ($segmento in $completa.Substring($actual.Length).Split(
        [char[]]@('\'),
        [StringSplitOptions]::RemoveEmptyEntries)) {
        $actual = [IO.Path]::Combine($actual, $segmento)
        if ([IO.Directory]::Exists($actual) -and
            ([IO.File]::GetAttributes($actual) -band [IO.FileAttributes]::ReparsePoint)) {
            throw "La ruta contiene un punto de reanalisis: $actual"
        }
    }

    return $completa
}

Assert-Administrador
$permisos = Assert-RutaSegura -Ruta $RutaPermisos
if (-not [IO.Directory]::Exists($permisos)) {
    throw "No existe la carpeta central de permisos: $permisos"
}

$auditoria = [IO.Path]::Combine($permisos, 'Auditoria')
if (-not $PSCmdlet.ShouldProcess($auditoria, 'Crear y proteger la auditoria remota')) {
    return
}

[IO.Directory]::CreateDirectory($auditoria) | Out-Null
$auditoria = Assert-RutaSegura -Ruta $auditoria
$administradores = [Security.Principal.SecurityIdentifier]::new(
    [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
    $null)
$sistema = [Security.Principal.SecurityIdentifier]::new(
    [Security.Principal.WellKnownSidType]::LocalSystemSid,
    $null)
$propietarioCreador = [Security.Principal.SecurityIdentifier]::new(
    [Security.Principal.WellKnownSidType]::CreatorOwnerSid,
    $null)
$creadores = Resolve-Identidad -Valor $IdentidadCreadores
$herencia = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
    [Security.AccessControl.InheritanceFlags]::ObjectInherit

$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
$acl.SetOwner($administradores)
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
    $administradores,
    [Security.AccessControl.FileSystemRights]::FullControl,
    $herencia,
    [Security.AccessControl.PropagationFlags]::None,
    [Security.AccessControl.AccessControlType]::Allow))
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
    $sistema,
    [Security.AccessControl.FileSystemRights]::FullControl,
    $herencia,
    [Security.AccessControl.PropagationFlags]::None,
    [Security.AccessControl.AccessControlType]::Allow))
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
    $creadores,
    [Security.AccessControl.FileSystemRights]::ReadAndExecute -bor
        [Security.AccessControl.FileSystemRights]::CreateDirectories,
    [Security.AccessControl.InheritanceFlags]::None,
    [Security.AccessControl.PropagationFlags]::None,
    [Security.AccessControl.AccessControlType]::Allow))
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
    $propietarioCreador,
    [Security.AccessControl.FileSystemRights]::Modify,
    $herencia,
    [Security.AccessControl.PropagationFlags]::InheritOnly,
    [Security.AccessControl.AccessControlType]::Allow))

[IO.DirectoryInfo]::new($auditoria).SetAccessControl($acl)
Write-Host "Auditoria preparada: $auditoria"
Write-Host "Identidad autorizada para crear carpetas: $($creadores.Value)"
