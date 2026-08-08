# (Autor: Alex Roman)
# Descripcion: Genera localmente permisos y catalogo firmados para el despliegue corporativo.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$RutaScripts,

    [string]$RutaSalida = '',

    [ValidateCount(2, 2)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')]
    [string[]]$Administradores = @(
        'MAD00\aroperez_micro',
        'PCERA\alero'
    ),

    [ValidateRange(1, 10000)]
    [int]$TotalScriptsEsperado = 37
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Resuelve entradas locales sin depender del dominio.
$raiz = $PSScriptRoot
if (-not (Test-Path -LiteralPath $RutaScripts -PathType Container)) {
    throw "No se encontro la carpeta de scripts: $RutaScripts"
}
$rutaScriptsCompleta = (Resolve-Path -LiteralPath $RutaScripts).Path
if ([string]::IsNullOrWhiteSpace($RutaSalida)) {
    $sufijo = "{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
    $RutaSalida = Join-Path $raiz "ArtefactosGenerados\conjunto-firmado-$sufijo"
}
$rutaSalidaCompleta = [IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables($RutaSalida))
if (Test-Path -LiteralPath $rutaSalidaCompleta) {
    if (-not (Test-Path -LiteralPath $rutaSalidaCompleta -PathType Container)) {
        throw "La salida no es una carpeta: $rutaSalidaCompleta"
    }
    if (@(Get-ChildItem -LiteralPath $rutaSalidaCompleta -Force).Count -ne 0) {
        throw "La carpeta de salida debe estar vacia: $rutaSalidaCompleta"
    }
} else {
    New-Item -ItemType Directory -Path $rutaSalidaCompleta | Out-Null
}

$administradoresUnicos = @($Administradores | Sort-Object -Unique)
if ($administradoresUnicos.Count -ne 2) {
    throw 'Debe indicar exactamente dos administradores distintos.'
}

# Copia los scripts a una carpeta temporal y conserva sus bytes.
$archivosOrigen = @(
    Get-ChildItem -LiteralPath $rutaScriptsCompleta -Recurse -File |
        Where-Object { $_.Extension.ToLowerInvariant() -in @('.ps1', '.bat', '.cmd') }
)
if ($archivosOrigen.Count -ne $TotalScriptsEsperado) {
    throw "Se esperaban $TotalScriptsEsperado scripts y se encontraron $($archivosOrigen.Count)."
}
$raizTemporal = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scriptsPreparados = Join-Path $raizTemporal ("LanzadorScripts-Firmados-$([Guid]::NewGuid().ToString('N'))")
[IO.Directory]::CreateDirectory($scriptsPreparados) | Out-Null
try {
    foreach ($archivoOrigen in $archivosOrigen) {
        if (-not [string]::IsNullOrWhiteSpace($archivoOrigen.LinkTarget)) {
            throw "No se permiten enlaces del sistema entre los scripts: $($archivoOrigen.FullName)"
        }

        $relativa = [IO.Path]::GetRelativePath($rutaScriptsCompleta, $archivoOrigen.FullName)
        if ([IO.Path]::IsPathRooted($relativa) -or
            $relativa -eq '..' -or
            $relativa.StartsWith("..$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal)) {
            throw "El script queda fuera de la carpeta autorizada: $($archivoOrigen.FullName)"
        }

        $destino = [IO.Path]::GetFullPath((Join-Path $scriptsPreparados $relativa))
        $prefijoPreparacion = $scriptsPreparados.TrimEnd('\') + '\'
        if (-not $destino.StartsWith($prefijoPreparacion, [StringComparison]::OrdinalIgnoreCase)) {
            throw "La ruta relativa del script no es segura: $relativa"
        }

        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destino)) | Out-Null
        Copy-Item -LiteralPath $archivoOrigen.FullName -Destination $destino
        $hashOrigen = (Get-FileHash -LiteralPath $archivoOrigen.FullName -Algorithm SHA256).Hash
        $hashDestino = (Get-FileHash -LiteralPath $destino -Algorithm SHA256).Hash
        if ($hashOrigen -ne $hashDestino) {
            throw "La copia local del script no conserva su SHA-256: $relativa"
        }
    }

    # Comprueba la clave privada RSA usada para firmar artefactos.
    $proyecto = Join-Path $raiz 'LanzadorScripts.csproj'
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

    # Compila y ejecuta el generador sin transmitir secretos.
    & dotnet build $proyecto -c Release -r win-x64 --self-contained true
    if ($LASTEXITCODE -ne 0) {
        throw "La compilacion termino con codigo $LASTEXITCODE."
    }
    $ensamblado = Join-Path $raiz 'bin\Release\net10.0-windows\win-x64\LanzadorScripts.dll'
    if (-not (Test-Path -LiteralPath $ensamblado -PathType Leaf)) {
        throw "No se encontro el ensamblado generador: $ensamblado"
    }

    $scriptsBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($scriptsPreparados))
    $salidaBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($rutaSalidaCompleta))
    $administradoresJson = ConvertTo-Json -InputObject @($administradoresUnicos) -Compress
    $administradoresBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($administradoresJson))
    & dotnet $ensamblado `
        --generar-conjunto-artefactos `
        --scripts-base64 $scriptsBase64 `
        --salida-base64 $salidaBase64 `
        --administradores-base64 $administradoresBase64 `
        --total-esperado $TotalScriptsEsperado
    if ($LASTEXITCODE -ne 0) {
        throw "La generacion del conjunto termino con codigo $LASTEXITCODE."
    }

    # Verifica la forma publica del conjunto antes de entregarlo.
    $nombresEsperados = @('permisos.json', 'catalogo-scripts.json')
    $archivos = @(Get-ChildItem -LiteralPath $rutaSalidaCompleta -File)
    if ($archivos.Count -ne $nombresEsperados.Count -or
        @($archivos | Where-Object { $_.Name -notin $nombresEsperados }).Count -ne 0) {
        throw 'La salida no contiene exactamente los dos artefactos firmados requeridos.'
    }

    $tiposEsperados = @{
        'permisos.json' = 'permissions'
        'catalogo-scripts.json' = 'script-catalog'
    }
    $conjuntos = foreach ($nombre in $nombresEsperados) {
        $contenedor = Get-Content -LiteralPath (Join-Path $rutaSalidaCompleta $nombre) -Raw |
            ConvertFrom-Json
        $propiedades = @($contenedor.PSObject.Properties.Name | Sort-Object)
        $esperadas = @('Algoritmo', 'Autor', 'ConjuntoId', 'Contenido', 'Descripcion', 'Firma', 'Tipo', 'Version')
        if (Compare-Object $propiedades $esperadas) {
            throw "El contenedor $nombre no tiene exactamente las propiedades v3."
        }
        if ($contenedor.Version -ne 3 -or
            $contenedor.Algoritmo -ne 'RSA-PSS-SHA256' -or
            $contenedor.Tipo -ne $tiposEsperados[$nombre] -or
            [string]$contenedor.ConjuntoId -notmatch '^[A-F0-9]{32}$') {
            throw "El contenedor $nombre no cumple el contrato firmado v3."
        }
        [string]$contenedor.ConjuntoId
    }
    if (@($conjuntos | Sort-Object -Unique).Count -ne 1) {
        throw 'Permisos y catalogo no comparten el mismo ConjuntoId.'
    }

    $permisos = (Get-Content -LiteralPath (Join-Path $rutaSalidaCompleta 'permisos.json') -Raw |
        ConvertFrom-Json).Contenido
    $adminsGenerados = @(
        $permisos.usuarios |
            Where-Object { $_.rol -eq 'admin' } |
            ForEach-Object { [string]$_.nombreUsuario } |
            Sort-Object -Unique
    )
    if (Compare-Object $adminsGenerados $administradoresUnicos) {
        throw 'Los permisos no contienen exactamente los dos administradores solicitados.'
    }
    $catalogo = (Get-Content -LiteralPath (Join-Path $rutaSalidaCompleta 'catalogo-scripts.json') -Raw |
        ConvertFrom-Json).Contenido
    if (@($catalogo.scripts).Count -ne $TotalScriptsEsperado) {
        throw "El catalogo no contiene los $TotalScriptsEsperado scripts esperados."
    }

    $archivos |
        Sort-Object Name |
        ForEach-Object {
            [pscustomobject]@{
                Archivo = $_.FullName
                ConjuntoId = $conjuntos[0]
                Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        }
} finally {
    # Elimina solo la carpeta temporal unica creada por esta ejecucion.
    $temporalValidado = [IO.Path]::GetFullPath($scriptsPreparados)
    if ($temporalValidado.StartsWith($raizTemporal, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $temporalValidado).StartsWith('LanzadorScripts-Firmados-', [StringComparison]::Ordinal)) {
        [IO.Directory]::Delete($temporalValidado, $true)
    }
}
