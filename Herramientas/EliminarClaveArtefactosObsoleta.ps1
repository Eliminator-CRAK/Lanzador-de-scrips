# (Autor: Alex Roman)
# Descripcion: Elimina opcionalmente la clave AES local obsoleta despues del periodo de rollback.

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param()

$ErrorActionPreference = 'Stop'
$rutaEsperada = [IO.Path]::GetFullPath(
    (Join-Path $env:ProgramData 'LanzadorScripts\Seguridad\artefactos.key'))

# Limita la limpieza al archivo historico exacto.
if (-not (Test-Path -LiteralPath $rutaEsperada -PathType Leaf)) {
    Write-Host "No existe la clave obsoleta: $rutaEsperada"
    return
}
if ((Get-Item -LiteralPath $rutaEsperada -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
    throw 'La clave obsoleta no puede eliminarse porque es un enlace del sistema.'
}

if ($PSCmdlet.ShouldProcess($rutaEsperada, 'Eliminar clave AES obsoleta')) {
    Remove-Item -LiteralPath $rutaEsperada -Force
    Write-Host "Clave obsoleta eliminada: $rutaEsperada"
}
