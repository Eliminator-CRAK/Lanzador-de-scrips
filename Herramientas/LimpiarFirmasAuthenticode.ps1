# (Autor: Alex Roman)
# Descripcion: Retira bloques Authenticode finales y conserva una copia completa.

param(
    [Parameter(Mandatory = $true)]
    [string]$RutaScripts
)

$ErrorActionPreference = 'Stop'
$marca = [System.Text.Encoding]::ASCII.GetBytes('# SIG # Begin signature block')
$raiz = (Resolve-Path -LiteralPath $RutaScripts).Path
$padre = Split-Path -Parent $raiz
$respaldo = Join-Path $padre ('BACKUP_FIRMAS_AUTHENTICODE_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
$total = 0

function Find-Bytes {
    param(
        [byte[]]$Datos,
        [byte[]]$Patron
    )

    for ($indice = $Datos.Length - $Patron.Length; $indice -ge 0; $indice--) {
        $coincide = $true
        for ($posicion = 0; $posicion -lt $Patron.Length; $posicion++) {
            if ($Datos[$indice + $posicion] -ne $Patron[$posicion]) {
                $coincide = $false
                break
            }
        }

        if ($coincide) {
            return $indice
        }
    }

    return -1
}

foreach ($archivo in Get-ChildItem -LiteralPath $raiz -Filter '*.ps1' -File -Recurse) {
    $datos = [System.IO.File]::ReadAllBytes($archivo.FullName)
    $indice = Find-Bytes -Datos $datos -Patron $marca
    if ($indice -lt 0) {
        continue
    }

    $prefijoRaiz = $raiz.TrimEnd('\') + '\'
    if (-not $archivo.FullName.StartsWith($prefijoRaiz, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "El archivo esta fuera de la carpeta de scripts: $($archivo.FullName)"
    }

    $relativa = $archivo.FullName.Substring($prefijoRaiz.Length)
    $rutaRespaldo = Join-Path $respaldo $relativa
    $carpetaRespaldo = Split-Path -Parent $rutaRespaldo
    [System.IO.Directory]::CreateDirectory($carpetaRespaldo) | Out-Null
    [System.IO.File]::Copy($archivo.FullName, $rutaRespaldo, $false)

    $limpios = [byte[]]::new($indice)
    [System.Array]::Copy($datos, $limpios, $indice)
    $temporal = $archivo.FullName + '.' + [System.Guid]::NewGuid().ToString('N') + '.tmp'
    $respaldoSustitucion = $archivo.FullName + '.' + [System.Guid]::NewGuid().ToString('N') + '.bak'
    try {
        [System.IO.File]::WriteAllBytes($temporal, $limpios)
        [System.IO.File]::Replace($temporal, $archivo.FullName, $respaldoSustitucion, $true)
    }
    finally {
        if ([System.IO.File]::Exists($temporal)) {
            [System.IO.File]::Delete($temporal)
        }

        if ([System.IO.File]::Exists($respaldoSustitucion)) {
            [System.IO.File]::Delete($respaldoSustitucion)
        }
    }

    $total++
}

Write-Host "Firmas Authenticode retiradas: $total"
if ($total -gt 0) {
    Write-Host "Respaldo: $respaldo"
}
