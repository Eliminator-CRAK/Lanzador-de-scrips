# (Autor: Alex Roman)
# Descripcion: Comprueba el equipo de dominio, genera los tres artefactos y los despliega con respaldo.

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$RutaScripts = '',

    [ValidateNotNullOrEmpty()]
    [string]$RutaCentralPermisos = '\\MAD002MICROPRU.mad.ae.aena.es\R$\PERMISOS',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')]
    [string]$Administrador = 'MAD00\aroperez_micro',

    [ValidatePattern('^S-1-5-21-(?:\d+-){2,14}\d+$')]
    [string]$SidAutorizado = 'S-1-5-21-1979283502-1139295200-817656539-77039',

    [ValidateRange(1, 10000)]
    [int]$TotalScriptsEsperado = 37,

    [switch]$Desplegar
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

function Resolve-RutaCentralValidada {
    param([string]$Ruta)

    # Acepta solo una ruta UNC absoluta terminada en PERMISOS y sin navegacion relativa.
    $expandida = [Environment]::ExpandEnvironmentVariables($Ruta.Trim())
    $segmentos = @($expandida -split '[\\/]' | Where-Object { $_.Length -gt 0 })
    if (-not $expandida.StartsWith('\\', [StringComparison]::Ordinal) -or
        $expandida.Contains('/') -or
        $segmentos.Count -lt 3 -or
        @($segmentos | Where-Object { $_ -eq '.' -or $_ -eq '..' }).Count -ne 0) {
        throw 'La ruta central debe ser una ruta UNC absoluta sin segmentos relativos.'
    }

    $completa = [IO.Path]::GetFullPath($expandida).TrimEnd('\')
    if ((Split-Path -Leaf $completa) -ne 'PERMISOS') {
        throw 'La ruta central debe terminar en la carpeta PERMISOS.'
    }

    return $completa
}

function Assert-Prerrequisitos {
    param(
        [string]$RaizRepositorio,
        [string]$CuentaEsperada,
        [string]$SidEsperado,
        [bool]$RequiereAdministrador
    )

    # Comprueba Windows, PowerShell, .NET, identidad, dominio y certificado privado.
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

    $identidad = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not [string]::Equals(
            $identidad.Name,
            $CuentaEsperada,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "La consola se ejecuta como $($identidad.Name), no como $CuentaEsperada."
    }
    if ($RequiereAdministrador) {
        $principal = [Security.Principal.WindowsPrincipal]::new($identidad)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Abra PowerShell como administrador usando la misma cuenta de dominio.'
        }
    }

    if (-not (Get-Command -Name 'nltest.exe' -ErrorAction SilentlyContinue)) {
        throw 'No se encontro nltest.exe. Ejecute la herramienta en una instalacion compatible de Windows.'
    }

    $dominio = $CuentaEsperada.Split('\')[0]
    & nltest "/dsgetdc:$dominio" *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "No se encontro un controlador del dominio $dominio. Conecte la red corporativa o VPN."
    }
    try {
        $cuenta = [Security.Principal.NTAccount]::new($CuentaEsperada)
        $sidCuenta = $cuenta.Translate([Security.Principal.SecurityIdentifier]).Value
    } catch {
        throw "No se pudo resolver la cuenta $CuentaEsperada en Active Directory."
    }
    if (-not [string]::Equals($sidCuenta, $SidEsperado, [StringComparison]::Ordinal)) {
        throw "La cuenta resuelve al SID $sidCuenta, no al SID configurado $SidEsperado."
    }

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
        throw "No se encontro el certificado privado de artefactos $huella."
    }
}

function Get-ResumenConjunto {
    param(
        [string]$Carpeta,
        [switch]$PermitirArchivosAdicionales
    )

    # Valida nombres, enlaces, KeyId y hashes del conjunto completo.
    if (-not (Test-Path -LiteralPath $Carpeta -PathType Container)) {
        throw "No existe la carpeta de artefactos: $Carpeta"
    }
    $archivos = @(Get-ChildItem -LiteralPath $Carpeta -File -Force)
    $faltantes = @($nombresArtefactos | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $Carpeta $_) -PathType Leaf)
    })
    $adicionales = @($archivos | Where-Object { $_.Name -notin $nombresArtefactos })
    if ($faltantes.Count -ne 0 -or
        (-not $PermitirArchivosAdicionales -and $adicionales.Count -ne 0)) {
        throw 'La carpeta no contiene exactamente los tres artefactos requeridos.'
    }

    $hashes = [ordered]@{}
    $keyIds = foreach ($nombre in $nombresArtefactos) {
        $ruta = Join-Path $Carpeta $nombre
        if ((Get-Item -LiteralPath $ruta -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
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

function Assert-HashesIguales {
    param(
        [System.Collections.IDictionary]$Esperados,
        [System.Collections.IDictionary]$Actuales,
        [string]$Descripcion
    )

    # Exige que cada archivo conserve exactamente su SHA-256.
    foreach ($nombre in $nombresArtefactos) {
        if (-not [string]::Equals(
                [string]$Esperados[$nombre],
                [string]$Actuales[$nombre],
                [StringComparison]::Ordinal)) {
            throw "El SHA-256 de $nombre no coincide en $Descripcion."
        }
    }
}

function Remove-StagingCentral {
    param(
        [string]$RutaStaging,
        [string]$RutaCentral
    )

    # Elimina solo la carpeta temporal creada dentro de la ruta central validada.
    if ([string]::IsNullOrWhiteSpace($RutaStaging) -or
        -not (Test-Path -LiteralPath $RutaStaging -PathType Container)) {
        return
    }
    $central = [IO.Path]::GetFullPath($RutaCentral).TrimEnd('\')
    $staging = [IO.Path]::GetFullPath($RutaStaging)
    if (-not $staging.StartsWith($central + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not (Split-Path -Leaf $staging).StartsWith('.staging-', [StringComparison]::Ordinal)) {
        throw 'La carpeta temporal central no pertenece a la ruta validada.'
    }
    [IO.Directory]::Delete($staging, $true)
}

# Resuelve el repositorio sin depender del nombre de la carpeta descargada.
$raizRepositorio = [IO.Path]::GetFullPath($PSScriptRoot)
$rutaScriptsResuelta = Resolve-RutaScriptsOperativa `
    -RutaIndicada $RutaScripts `
    -RaizRepositorio $raizRepositorio `
    -RutaFueIndicada $PSBoundParameters.ContainsKey('RutaScripts')
$rutaCentralResuelta = Resolve-RutaCentralValidada -Ruta $RutaCentralPermisos

Assert-Prerrequisitos `
    -RaizRepositorio $raizRepositorio `
    -CuentaEsperada $Administrador `
    -SidEsperado $SidAutorizado `
    -RequiereAdministrador $Desplegar.IsPresent

# Comprueba el inventario antes de generar una clave nueva.
$scripts = @(
    Get-ChildItem -LiteralPath $rutaScriptsResuelta -Recurse -File |
        Where-Object { $_.Extension.ToLowerInvariant() -in @('.ps1', '.bat', '.cmd') }
)
if ($scripts.Count -ne $TotalScriptsEsperado) {
    throw "Se esperaban $TotalScriptsEsperado scripts y se encontraron $($scripts.Count)."
}

$marcaTiempo = Get-Date -Format 'yyyyMMdd-HHmmss'
$idSalida = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$rutaSalida = Join-Path $raizRepositorio "obj\artefactos-finales-$marcaTiempo-$idSalida"
$generador = Join-Path $raizRepositorio 'Herramientas\GenerarConjuntoArtefactos.ps1'
if (-not (Test-Path -LiteralPath $generador -PathType Leaf)) {
    throw "No se encontro el generador requerido: $generador"
}

Write-Host "Generando el conjunto desde $($scripts.Count) scripts..."
& $generador `
    -RutaScripts $rutaScriptsResuelta `
    -RutaSalida $rutaSalida `
    -Administrador $Administrador `
    -SidAutorizado $SidAutorizado `
    -TotalScriptsEsperado $TotalScriptsEsperado
$resumenLocal = Get-ResumenConjunto -Carpeta $rutaSalida
Write-Host "Conjunto validado. KeyId: $($resumenLocal.KeyId)"
Write-Host "Salida local: $rutaSalida"

if (-not $Desplegar) {
    Write-Host 'No se modifico el servidor. Repita el comando con -Desplegar para respaldar y publicar el conjunto.'
    return
}
if (-not (Test-Path -LiteralPath $rutaCentralResuelta -PathType Container)) {
    throw "No se puede acceder a la carpeta central: $rutaCentralResuelta"
}
if (-not $PSCmdlet.ShouldProcess(
        $rutaCentralResuelta,
        "Respaldar y sustituir los tres artefactos con KeyId $($resumenLocal.KeyId)")) {
    Write-Host 'Despliegue cancelado. El conjunto generado permanece en la salida local.'
    return
}

# Respalda el estado anterior antes de escribir en la carpeta central.
$rutaRespaldo = Join-Path $rutaCentralResuelta "RESPALDO_$marcaTiempo"
$rutaStaging = Join-Path $rutaCentralResuelta ".staging-$([Guid]::NewGuid().ToString('N'))"
$existianAntes = [ordered]@{}
$despliegueIniciado = $false
if (Test-Path -LiteralPath $rutaRespaldo) {
    throw "Ya existe la carpeta de respaldo: $rutaRespaldo"
}
[IO.Directory]::CreateDirectory($rutaRespaldo) | Out-Null
foreach ($nombre in $nombresArtefactos) {
    $rutaActual = Join-Path $rutaCentralResuelta $nombre
    $existia = Test-Path -LiteralPath $rutaActual -PathType Leaf
    $existianAntes[$nombre] = $existia
    if ($existia) {
        if ((Get-Item -LiteralPath $rutaActual -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw "El archivo central $nombre no puede ser un enlace de sistema."
        }
        $rutaCopia = Join-Path $rutaRespaldo $nombre
        Copy-Item -LiteralPath $rutaActual -Destination $rutaCopia
        if ((Get-FileHash -LiteralPath $rutaActual -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $rutaCopia -Algorithm SHA256).Hash) {
            throw "El respaldo de $nombre no conserva su SHA-256."
        }
    }
}

try {
    # Copia primero a staging y valida antes de sustituir archivos operativos.
    [IO.Directory]::CreateDirectory($rutaStaging) | Out-Null
    foreach ($nombre in $nombresArtefactos) {
        Copy-Item -LiteralPath (Join-Path $rutaSalida $nombre) `
            -Destination (Join-Path $rutaStaging $nombre)
    }
    $resumenStaging = Get-ResumenConjunto -Carpeta $rutaStaging
    Assert-HashesIguales `
        -Esperados $resumenLocal.Hashes `
        -Actuales $resumenStaging.Hashes `
        -Descripcion 'el staging central'

    # Sustituye el conjunto y activa la restauracion automatica ante cualquier fallo.
    $despliegueIniciado = $true
    foreach ($nombre in $nombresArtefactos) {
        Copy-Item -LiteralPath (Join-Path $rutaStaging $nombre) `
            -Destination (Join-Path $rutaCentralResuelta $nombre) `
            -Force
    }
    $resumenCentral = Get-ResumenConjunto `
        -Carpeta $rutaCentralResuelta `
        -PermitirArchivosAdicionales
    Assert-HashesIguales `
        -Esperados $resumenLocal.Hashes `
        -Actuales $resumenCentral.Hashes `
        -Descripcion 'la carpeta central'
    if (-not [string]::Equals(
            $resumenCentral.KeyId,
            $resumenLocal.KeyId,
            [StringComparison]::Ordinal)) {
        throw 'El KeyId desplegado no coincide con el conjunto generado.'
    }
} catch {
    # Restaura el estado anterior si el despliegue quedo incompleto o no se valido.
    $errorDespliegue = $_
    $erroresRestauracion = [Collections.Generic.List[string]]::new()
    if ($despliegueIniciado) {
        foreach ($nombre in $nombresArtefactos) {
            $rutaDestino = Join-Path $rutaCentralResuelta $nombre
            try {
                if ($existianAntes[$nombre]) {
                    $rutaCopia = Join-Path $rutaRespaldo $nombre
                    Copy-Item -LiteralPath $rutaCopia -Destination $rutaDestino -Force
                    if ((Get-FileHash -LiteralPath $rutaCopia -Algorithm SHA256).Hash -ne
                        (Get-FileHash -LiteralPath $rutaDestino -Algorithm SHA256).Hash) {
                        throw 'El SHA-256 restaurado no coincide con el respaldo.'
                    }
                } elseif (Test-Path -LiteralPath $rutaDestino -PathType Leaf) {
                    [IO.File]::Delete($rutaDestino)
                }
            } catch {
                $erroresRestauracion.Add("$nombre`: $($_.Exception.Message)")
            }
        }
    }
    $detalleRestauracion = if (-not $despliegueIniciado) {
        'No fue necesario restaurar porque ningun archivo operativo se habia sustituido.'
    } elseif ($erroresRestauracion.Count -eq 0) {
        'La restauracion del conjunto anterior no notifico errores.'
    } else {
        "La restauracion quedo incompleta: $($erroresRestauracion -join '; ')"
    }
    throw "El despliegue fallo. $detalleRestauracion Error original: $($errorDespliegue.Exception.Message)"
} finally {
    try {
        Remove-StagingCentral -RutaStaging $rutaStaging -RutaCentral $rutaCentralResuelta
    } catch {
        Write-Warning "No se pudo eliminar el staging central: $($_.Exception.Message)"
    }
}

Write-Host 'Despliegue completado y verificado.'
Write-Host "Respaldo anterior: $rutaRespaldo"
Write-Host 'Abra LanzadorScripts 1.5.6 como MAD00\aroperez_micro para aprovisionar artefactos.key automaticamente.'
