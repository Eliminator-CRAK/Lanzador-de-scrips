# (Autor: Alex Roman)
# Descripcion: Genera localmente los tres artefactos para el usuario PCERA\alero.

[CmdletBinding()]
param(
    [string]$RutaScripts = '',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')]
    [string]$Administrador = 'PCERA\alero',

    [ValidateRange(1, 10000)]
    [int]$TotalScriptsEsperado = 37
)

$ErrorActionPreference = 'Stop'
$nombresArtefactos = @(
    'permisos.json',
    'catalogo-scripts.json',
    'clave-artefactos.dpng.json'
)

function Resolve-RutaScriptsOperativa {
    param(
        [string]$RutaIndicada,
        [string]$RaizRepositorio,
        [bool]$RutaFueIndicada
    )

    # Busca ACTUALES en la ruta indicada, OneDrive corporativo o la raiz descargada.
    $candidatas = if ($RutaFueIndicada) {
        @($RutaIndicada)
    } else {
        @(
            $(if ($env:OneDriveCommercial) {
                Join-Path $env:OneDriveCommercial 'Documentos\notas\SCRIPS\ACTUALES'
            }),
            (Join-Path $env:USERPROFILE 'OneDrive - Aena, SME S.A\Documentos\notas\SCRIPS\ACTUALES'),
            (Join-Path $RaizRepositorio 'ACTUALES')
        )
    }

    foreach ($candidata in $candidatas | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
        if (Test-Path -LiteralPath $candidata -PathType Container) {
            return (Resolve-Path -LiteralPath $candidata).Path
        }
    }

    throw 'No se encontro la carpeta ACTUALES. Indiquela mediante -RutaScripts.'
}

function Assert-PrerrequisitosLocales {
    param(
        [string]$RaizRepositorio,
        [string]$CuentaEsperada
    )

    # Comprueba Windows, PowerShell, .NET, la cuenta local y el certificado privado.
    if (-not $IsWindows) {
        throw 'Esta herramienta solo puede ejecutarse en Windows.'
    }
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        throw 'Ejecute la herramienta con PowerShell 7 mediante pwsh.exe.'
    }
    foreach ($nombre in @('LanzadorScripts.csproj', 'LanzadorScripts.slnx')) {
        if (-not (Test-Path -LiteralPath (Join-Path $RaizRepositorio $nombre) -PathType Leaf)) {
            throw "La carpeta actual no contiene $nombre. Ejecute el script desde la raiz del repositorio."
        }
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'No se encontro dotnet. Instale .NET SDK 10.'
    }
    $sdks = @(& dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0 -or @($sdks | Where-Object { $_ -match '^10\.' }).Count -eq 0) {
        throw 'No se encontro .NET SDK 10.'
    }

    $identidad = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    if (-not [string]::Equals($identidad, $CuentaEsperada, [StringComparison]::OrdinalIgnoreCase)) {
        throw "La consola se ejecuta como $identidad, no como $CuentaEsperada."
    }
    Write-Host "Cuenta local validada: $identidad"

    $fuenteCertificado = Get-Content -LiteralPath (
        Join-Path $RaizRepositorio 'Servicios\ServicioTokenMaestro.cs') -Raw
    $coincidencia = [regex]::Match(
        $fuenteCertificado,
        'HuellaCertificado\s*=\s*"(?<huella>[A-Fa-f0-9]+)"')
    if (-not $coincidencia.Success) {
        throw 'No se pudo leer la huella del certificado de artefactos.'
    }
    $huella = $coincidencia.Groups['huella'].Value
    $certificado = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My |
        Where-Object { $_.Thumbprint -eq $huella -and $_.HasPrivateKey } |
        Select-Object -First 1
    if ($null -eq $certificado) {
        throw "No se encontro el certificado privado de artefactos $huella para $identidad."
    }
    Write-Host "Certificado privado validado: $huella"
}

function Get-ResumenConjunto {
    param([string]$Carpeta)

    # Valida nombres, enlaces, KeyId y hashes del conjunto completo.
    if (-not (Test-Path -LiteralPath $Carpeta -PathType Container)) {
        throw "No existe la carpeta de artefactos: $Carpeta"
    }
    $archivos = @(Get-ChildItem -LiteralPath $Carpeta -File -Force)
    if ($archivos.Count -ne $nombresArtefactos.Count -or
        @($archivos | Where-Object { $_.Name -notin $nombresArtefactos }).Count -ne 0) {
        throw 'La carpeta no contiene exactamente los tres artefactos requeridos.'
    }

    $hashes = [ordered]@{}
    $keyIds = foreach ($nombre in $nombresArtefactos) {
        $ruta = Join-Path $Carpeta $nombre
        $archivo = Get-Item -LiteralPath $ruta -Force
        if ($archivo.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw "El artefacto $nombre no puede ser un enlace de sistema."
        }
        $contenido = Get-Content -LiteralPath $ruta -Raw | ConvertFrom-Json
        $keyId = [string]$contenido.KeyId
        if ($keyId -notmatch '^[A-Fa-f0-9]{16}$') {
            throw "El artefacto $nombre no contiene un KeyId valido."
        }
        $hashes[$nombre] = (Get-FileHash -LiteralPath $ruta -Algorithm SHA256).Hash
        $keyId.ToUpperInvariant()
    }
    $keyIdsUnicos = @($keyIds | Sort-Object -Unique)
    if ($keyIdsUnicos.Count -ne 1) {
        throw 'Los tres artefactos no comparten el mismo KeyId.'
    }

    return [pscustomobject]@{
        KeyId = $keyIdsUnicos[0]
        Hashes = $hashes
    }
}

function Remove-SalidaFallida {
    param(
        [string]$RutaSalida,
        [string]$CarpetaBase
    )

    # Elimina solo una salida incompleta creada por esta ejecucion.
    if (-not [IO.Directory]::Exists($RutaSalida)) {
        return
    }
    $base = [IO.Path]::GetFullPath($CarpetaBase).TrimEnd('\') + '\'
    $salida = [IO.Path]::GetFullPath($RutaSalida)
    $nombre = [IO.Path]::GetFileName($salida)
    if (-not $salida.StartsWith($base, [StringComparison]::OrdinalIgnoreCase) -or
        -not $nombre.StartsWith('conjunto-', [StringComparison]::Ordinal)) {
        throw 'La salida incompleta no pertenece a la carpeta local validada.'
    }
    [IO.Directory]::Delete($salida, $true)
}

# Resuelve todas las rutas dentro del repositorio descargado.
$raizRepositorio = [IO.Path]::GetFullPath($PSScriptRoot)
$rutaScriptsResuelta = Resolve-RutaScriptsOperativa `
    -RutaIndicada $RutaScripts `
    -RaizRepositorio $raizRepositorio `
    -RutaFueIndicada $PSBoundParameters.ContainsKey('RutaScripts')
Assert-PrerrequisitosLocales `
    -RaizRepositorio $raizRepositorio `
    -CuentaEsperada $Administrador

# Comprueba el inventario antes de generar material criptografico.
$scripts = @(
    Get-ChildItem -LiteralPath $rutaScriptsResuelta -Recurse -File |
        Where-Object { $_.Extension.ToLowerInvariant() -in @('.ps1', '.bat', '.cmd') }
)
if ($scripts.Count -ne $TotalScriptsEsperado) {
    throw "Se esperaban $TotalScriptsEsperado scripts y se encontraron $($scripts.Count)."
}

$carpetaBaseSalida = [IO.Path]::GetFullPath((Join-Path $raizRepositorio 'ArtefactosGenerados'))
$prefijoRepositorio = $raizRepositorio.TrimEnd('\') + '\'
if (-not $carpetaBaseSalida.StartsWith($prefijoRepositorio, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($carpetaBaseSalida) -ne 'ArtefactosGenerados') {
    throw 'La carpeta de salida local no pertenece al repositorio.'
}
if ([IO.Directory]::Exists($carpetaBaseSalida) -and
    (Get-Item -LiteralPath $carpetaBaseSalida -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
    throw 'La carpeta ArtefactosGenerados no puede ser un enlace de sistema.'
}
[IO.Directory]::CreateDirectory($carpetaBaseSalida) | Out-Null

$marcaTiempo = Get-Date -Format 'yyyyMMdd-HHmmss'
$idSalida = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$rutaSalida = Join-Path $carpetaBaseSalida "conjunto-$marcaTiempo-$idSalida"
$generador = Join-Path $raizRepositorio 'Herramientas\GenerarConjuntoArtefactos.ps1'
if (-not (Test-Path -LiteralPath $generador -PathType Leaf)) {
    throw "No se encontro el generador requerido: $generador"
}

try {
    Write-Host "Generando el conjunto local desde $($scripts.Count) scripts..."
    $null = @(& $generador `
        -RutaScripts $rutaScriptsResuelta `
        -RutaSalida $rutaSalida `
        -Administrador $Administrador `
        -ModoLocalUsuario `
        -TotalScriptsEsperado $TotalScriptsEsperado)
    $resumen = Get-ResumenConjunto -Carpeta $rutaSalida
} catch {
    Remove-SalidaFallida -RutaSalida $rutaSalida -CarpetaBase $carpetaBaseSalida
    throw
}

Write-Host 'Conjunto local generado y validado.'
Write-Host "Cuenta autorizada: $Administrador"
Write-Host "KeyId: $($resumen.KeyId)"
foreach ($nombre in $nombresArtefactos) {
    Write-Host "$nombre SHA-256: $($resumen.Hashes[$nombre])"
}
Write-Host "Salida: $rutaSalida"
Write-Warning 'El paquete LOCAL=user solo puede abrirse como PCERA\alero en este mismo equipo. Copie siempre los tres archivos juntos.'
