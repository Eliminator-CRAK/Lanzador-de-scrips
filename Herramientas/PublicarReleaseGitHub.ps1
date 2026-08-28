# (Autor: Alex Roman)
# Descripcion: Publica los entregables firmados directamente en una release de GitHub.

[CmdletBinding()]
param(
    [string]$Etiqueta = $env:GITHUB_REF_NAME,
    [string]$Repositorio = $env:GITHUB_REPOSITORY
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Etiqueta -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "La etiqueta de release no es valida: $Etiqueta"
}

if ($Repositorio -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "El repositorio de GitHub no es valido: $Repositorio"
}

$gh = Get-Command gh -CommandType Application -ErrorAction Stop
$raiz = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$prefijoRaiz = $raiz.TrimEnd('\') + '\'
$archivosRelativos = @(
    'publicacion\LanzadorScripts-1.8.4-x64.msi',
    'publicacion\LanzadorScripts_Portable-1.8.4-x64.exe',
    'publicacion-servidor\LanzadorScripts_Servidor-1.8.3-x64.zip'
)
$archivos = foreach ($relativa in $archivosRelativos) {
    $ruta = [System.IO.Path]::GetFullPath((Join-Path $raiz $relativa))
    if (-not $ruta.StartsWith($prefijoRaiz, [StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.File]::Exists($ruta)) {
        throw "No se encontro un entregable valido dentro del proyecto: $relativa"
    }

    $ruta
}

& $gh.Source release view $Etiqueta --repo $Repositorio *> $null
if ($LASTEXITCODE -ne 0) {
    & $gh.Source release create $Etiqueta `
        --repo $Repositorio `
        --verify-tag `
        --title "LanzadorScripts $Etiqueta" `
        --generate-notes
    if ($LASTEXITCODE -ne 0) {
        throw 'No se pudo crear la release de GitHub.'
    }
}

& $gh.Source release upload $Etiqueta @archivos `
    --repo $Repositorio `
    --clobber
if ($LASTEXITCODE -ne 0) {
    throw 'No se pudieron publicar los entregables en la release de GitHub.'
}
