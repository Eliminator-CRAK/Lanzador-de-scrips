# (Autor: Alex Roman)
# Descripcion: Retira el servicio y los binarios del servidor conservando sus datos.

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [switch]$EliminarDatos
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$nombreServicio = 'LanzadorScriptsServidor'
$destino = [System.IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'LanzadorScriptsServidor'))
$datos = [System.IO.Path]::GetFullPath((Join-Path $env:ProgramData 'LanzadorScriptsServidor'))

function Assert-Administrador {
    $identidad = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identidad)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Ejecute este desinstalador como administrador.'
    }
}

function Assert-ArbolSinReparse {
    param([Parameter(Mandatory)][string]$Ruta)

    if (-not [System.IO.Directory]::Exists($Ruta)) {
        return
    }

    if (([System.IO.DirectoryInfo]::new($Ruta).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "No se elimina una ruta que es un punto de reanalisis: $Ruta"
    }

    foreach ($elemento in Get-ChildItem -LiteralPath $Ruta -Force -Recurse) {
        if (($elemento.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "No se elimina un arbol que contiene un punto de reanalisis: $($elemento.FullName)"
        }
    }
}

function Invoke-Sc {
    param([Parameter(Mandatory)][string[]]$Argumentos)

    $inicio = [Diagnostics.ProcessStartInfo]::new()
    $inicio.FileName = Join-Path $env:SystemRoot 'System32\sc.exe'
    $inicio.UseShellExecute = $false
    $inicio.CreateNoWindow = $true
    foreach ($argumento in $Argumentos) {
        [void]$inicio.ArgumentList.Add($argumento)
    }
    $proceso = [Diagnostics.Process]::Start($inicio)
    if ($null -eq $proceso) {
        throw 'No se pudo completar la operacion del servicio Windows.'
    }
    try {
        if (-not $proceso.WaitForExit(30000)) {
            $proceso.Kill($true)
            $proceso.WaitForExit()
            throw 'La operacion del servicio Windows supero el tiempo permitido.'
        }

        if ($proceso.ExitCode -ne 0) {
            throw "Windows rechazo la operacion del servicio con codigo $($proceso.ExitCode)."
        }
    }
    finally {
        $proceso.Dispose()
    }
}

Assert-Administrador
$rutaActual = [System.IO.Path]::GetFullPath($PSCommandPath)
if ($rutaActual.StartsWith($destino + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Ejecute Desinstalar-Servidor.ps1 desde el ZIP de distribucion, no desde Program Files.'
}

if (-not $PSCmdlet.ShouldProcess($destino, 'Desinstalar LanzadorScripts Servidor')) {
    return
}

$procesosInstalados = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    try {
        -not [string]::IsNullOrWhiteSpace($_.Path) -and
        $_.Path.StartsWith($destino + '\', [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        $false
    }
})
if ($procesosInstalados.Count -gt 0) {
    throw 'Cierre LanzadorScripts Servidor antes de desinstalarlo.'
}

$servicio = Get-Service -Name $nombreServicio -ErrorAction SilentlyContinue
if ($null -ne $servicio) {
    if ($servicio.Status -ne 'Stopped') {
        Stop-Service -Name $nombreServicio -Force
        $servicio.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    Invoke-Sc -Argumentos @('delete', $nombreServicio)
}

& (Join-Path $env:SystemRoot 'System32\netsh.exe') advfirewall firewall delete rule 'name=LanzadorScripts Servidor' | Out-Null
$acceso = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\LanzadorScripts\LanzadorScripts Servidor.lnk'
if ([System.IO.File]::Exists($acceso)) {
    [System.IO.File]::Delete($acceso)
}

Assert-ArbolSinReparse -Ruta $destino
if ([System.IO.Directory]::Exists($destino)) {
    Remove-Item -LiteralPath $destino -Recurse -Force
}

if ($EliminarDatos) {
    Assert-ArbolSinReparse -Ruta $datos
    if ([System.IO.Directory]::Exists($datos)) {
        Remove-Item -LiteralPath $datos -Recurse -Force
    }
    Write-Host 'Servicio, binarios y datos locales eliminados.'
}
else {
    Write-Host "Servicio y binarios eliminados. La base y las copias se conservan en $datos"
}
