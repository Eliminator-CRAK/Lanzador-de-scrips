# (Autor: Alex Roman)
# Descripcion: Genera permisos, catalogo y paquete DPAPI-NG para dominio o usuario local.

[CmdletBinding(DefaultParameterSetName = 'Dominio')]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$RutaScripts,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$RutaSalida,

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')]
    [string]$Administrador = 'MAD00\aroperez_micro',

    [Parameter(Mandatory, ParameterSetName = 'Dominio')]
    [ValidatePattern('^S-1-5-21-(?:\d+-){2,14}\d+$')]
    [string]$SidAutorizado,

    [Parameter(Mandatory, ParameterSetName = 'Local')]
    [switch]$ModoLocalUsuario,

    [ValidateRange(1, 10000)]
    [int]$TotalScriptsEsperado = 37
)

$ErrorActionPreference = 'Stop'

# Resuelve las rutas y exige una salida nueva o vacia.
if (-not (Test-Path -LiteralPath $RutaScripts -PathType Container)) {
    throw "No se encontro la carpeta de scripts: $RutaScripts"
}
$rutaScriptsCompleta = (Resolve-Path -LiteralPath $RutaScripts).Path
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

# Selecciona una proteccion local o valida la identidad contra Active Directory.
$descriptor = if ($ModoLocalUsuario) {
    $identidad = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    if (-not [string]::Equals($identidad, $Administrador, [StringComparison]::OrdinalIgnoreCase)) {
        throw "La cuenta local $identidad no coincide con el administrador $Administrador."
    }
    'LOCAL=user'
} else {
    try {
        $sid = [Security.Principal.SecurityIdentifier]::new($SidAutorizado)
    } catch {
        throw "El SID autorizado no es valido: $SidAutorizado"
    }
    if (-not $sid.Value.StartsWith('S-1-5-21-', [StringComparison]::Ordinal)) {
        throw 'El SID autorizado debe pertenecer a un dominio de Active Directory.'
    }

    $dominio = $Administrador.Split('\')[0]
    & nltest "/dsgetdc:$dominio" *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "No se encontro un controlador del dominio $dominio. Conecte el equipo a la red corporativa o VPN."
    }
    try {
        $cuenta = [Security.Principal.NTAccount]::new($Administrador)
        $sidCuenta = $cuenta.Translate([Security.Principal.SecurityIdentifier])
    } catch {
        throw "No se pudo resolver la cuenta $Administrador en Active Directory."
    }
    if ($sidCuenta.Value -ne $sid.Value) {
        throw "La cuenta $Administrador resuelve al SID $($sidCuenta.Value), no al SID indicado."
    }
    "SID=$($sid.Value)"
}

# Copia solo scripts validos a una carpeta local y confirma que los bytes no cambian.
$archivosOrigen = @(
    Get-ChildItem -LiteralPath $rutaScriptsCompleta -Recurse -File |
        Where-Object { $_.Extension.ToLowerInvariant() -in @('.ps1', '.bat', '.cmd') }
)
if ($archivosOrigen.Count -ne $TotalScriptsEsperado) {
    throw "Se esperaban $TotalScriptsEsperado scripts y se encontraron $($archivosOrigen.Count)."
}
$raizTemporal = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scriptsPreparados = Join-Path $raizTemporal ("LanzadorScripts-Artefactos-$([Guid]::NewGuid().ToString('N'))")
[IO.Directory]::CreateDirectory($scriptsPreparados) | Out-Null
try {
    foreach ($archivoOrigen in $archivosOrigen) {
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

    # Comprueba que el certificado privado de artefactos esta disponible.
    $raiz = Split-Path -Parent $PSScriptRoot
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

    # Compila el generador y pasa solo rutas e identidades codificadas.
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
    $administradorBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Administrador))
    $descriptorBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($descriptor))
    & dotnet $ensamblado `
        --generar-conjunto-artefactos `
        --scripts-base64 $scriptsBase64 `
        --salida-base64 $salidaBase64 `
        --administrador-base64 $administradorBase64 `
        --descriptor-base64 $descriptorBase64 `
        --total-esperado $TotalScriptsEsperado
    if ($LASTEXITCODE -ne 0) {
        throw "La generacion del conjunto termino con codigo $LASTEXITCODE."
    }

    # Verifica nombres, KeyId y huellas antes de devolver los resultados.
    $nombresEsperados = @(
        'permisos.json',
        'catalogo-scripts.json',
        'clave-artefactos.dpng.json'
    )
    $archivos = @(Get-ChildItem -LiteralPath $rutaSalidaCompleta -File)
    if ($archivos.Count -ne $nombresEsperados.Count -or
        @($archivos | Where-Object { $_.Name -notin $nombresEsperados }).Count -ne 0) {
        throw 'La salida no contiene exactamente los tres artefactos requeridos.'
    }
    $keyIds = foreach ($nombre in $nombresEsperados) {
        $contenido = Get-Content -LiteralPath (Join-Path $rutaSalidaCompleta $nombre) -Raw |
            ConvertFrom-Json
        [string]$contenido.KeyId
    }
    if (@($keyIds | Sort-Object -Unique).Count -ne 1 -or [string]::IsNullOrWhiteSpace($keyIds[0])) {
        throw 'Los tres artefactos no comparten el mismo KeyId.'
    }

    $archivos |
        Sort-Object Name |
        ForEach-Object {
            [pscustomobject]@{
                Archivo = $_.FullName
                KeyId = $keyIds[0]
                Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        }
} finally {
    # Elimina solo la carpeta temporal unica creada por esta ejecucion.
    $temporalValidado = [IO.Path]::GetFullPath($scriptsPreparados)
    if ($temporalValidado.StartsWith($raizTemporal, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $temporalValidado).StartsWith('LanzadorScripts-Artefactos-', [StringComparison]::Ordinal)) {
        [IO.Directory]::Delete($temporalValidado, $true)
    }
}
