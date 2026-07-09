# (Autor: Alex Roman)
# Descripcion: Publica el ejecutable portable y sus artefactos protegidos.

param(
    [string]$CertThumbprint = '',
    [string]$CertPath = '',
    [securestring]$CertPassword,
    [string]$TimestampServer = 'http://timestamp.digicert.com',
    [string]$RutaScriptsIniciales = (Join-Path $env:USERPROFILE 'OneDrive - Aena, SME S.A\Escritorio\notas\SCRIPS\ACTUALES'),
    [string]$RutaCarpetaPermisos = '\\MAD002MICROPRU.mad.ae.aena.es\R$\PERMISOS',
    [string]$RutaRuntimeWebView2Portable = '',
    [switch]$InicializarArtefactos,
    [switch]$AllowUnsignedForDev
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$raiz = Split-Path -Parent $PSScriptRoot
$proyecto = Join-Path $raiz 'LanzadorScripts.csproj'
$salida = Join-Path $raiz 'publicacion'
$tamanoMinimoExe = 209715200
$paginaDescargaWebView2 = 'https://developer.microsoft.com/en-us/microsoft-edge/webview2/'
$cacheWebView2 = Join-Path $raiz 'Recursos\WebView2'
$runtimeZipIntermedio = Join-Path $raiz 'obj\WebView2Runtime\WebView2Runtime.zip'
$raizCompleta = [System.IO.Path]::GetFullPath($raiz).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$salidaCompleta = [System.IO.Path]::GetFullPath($salida)
$prefijoPermitido = $raizCompleta + [System.IO.Path]::DirectorySeparatorChar

if (-not $salidaCompleta.StartsWith($prefijoPermitido, [System.StringComparison]::OrdinalIgnoreCase) -or
    [System.IO.Path]::GetFileName($salidaCompleta) -ne 'publicacion') {
    throw "La carpeta de publicacion no esta dentro del proyecto: $salidaCompleta"
}

function Get-SigningCertificate {
    if (-not [string]::IsNullOrWhiteSpace($CertThumbprint)) {
        $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My |
            Where-Object { $_.Thumbprint -eq $CertThumbprint } |
            Select-Object -First 1

        if ($null -eq $cert) {
            throw "No se encontro el certificado de firma con thumbprint $CertThumbprint."
        }

        return $cert
    }

    if (-not [string]::IsNullOrWhiteSpace($CertPath)) {
        if ($null -eq $CertPassword) {
            return Get-PfxCertificate -FilePath $CertPath
        }

        return Get-PfxCertificate -FilePath $CertPath -Password $CertPassword
    }

    return $null
}

function Invoke-NativeChecked {
    param(
        [string]$Descripcion,
        [scriptblock]$Comando
    )

    & $Comando
    $codigoSalida = $LASTEXITCODE
    if ($codigoSalida -ne 0) {
        throw "$Descripcion fallo. Codigo de salida: $codigoSalida"
    }
}

function Get-WebView2FixedRuntimeInfo {
    Write-Host 'Consultando WebView2 Fixed Runtime x64 oficial...'
    $contenido = (Invoke-WebRequest -Uri $paginaDescargaWebView2 -UseBasicParsing).Content
    $patrones = @(
        'https:\\u002F\\u002F[^"]+?Microsoft\.WebView2\.FixedVersionRuntime\.(?<version>[0-9.]+)\.x64\.cab',
        'https://[^"]+?Microsoft\.WebView2\.FixedVersionRuntime\.(?<version>[0-9.]+)\.x64\.cab'
    )
    $coincidencias = @($patrones | ForEach-Object { [regex]::Matches($contenido, $_) })
    if ($coincidencias.Count -eq 0) {
        throw 'No se encontro la descarga oficial x64 de WebView2 Fixed Runtime.'
    }

    $seleccion = $coincidencias |
        Sort-Object { [version]$_.Groups['version'].Value } -Descending |
        Select-Object -First 1
    $url = $seleccion.Value.Replace('\u002F', '/').Replace('\u0026', '&')
    $version = $seleccion.Groups['version'].Value
    return [pscustomobject]@{
        Version = $version
        Url = $url
        Archivo = "Microsoft.WebView2.FixedVersionRuntime.$version.x64.cab"
    }
}

function Test-WebView2RuntimeFolder {
    param(
        [string]$Ruta
    )

    if ([string]::IsNullOrWhiteSpace($Ruta) -or -not (Test-Path -LiteralPath $Ruta -PathType Container)) {
        throw "No se encontro la carpeta de runtime WebView2: $Ruta"
    }

    $rutaCompleta = (Resolve-Path -LiteralPath $Ruta).Path
    $ejecutableWebView2 = Get-ChildItem -LiteralPath $rutaCompleta -Filter 'msedgewebview2.exe' -Recurse -File |
        Select-Object -First 1
    if ($null -eq $ejecutableWebView2) {
        throw "La carpeta de runtime WebView2 no contiene msedgewebview2.exe: $rutaCompleta"
    }

    $firma = Get-AuthenticodeSignature -LiteralPath $ejecutableWebView2.FullName
    if ($firma.Status -ne 'Valid') {
        throw "La firma de msedgewebview2.exe no es valida: $($firma.Status)."
    }

    if ($null -eq $firma.SignerCertificate -or $firma.SignerCertificate.Subject -notlike '*Microsoft Corporation*') {
        throw 'msedgewebview2.exe no esta firmado por Microsoft Corporation.'
    }

    return $rutaCompleta
}

function Expand-WebView2Cab {
    param(
        [string]$Cab,
        [string]$Destino
    )

    if (Test-Path -LiteralPath $Destino) {
        Remove-Item -LiteralPath $Destino -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $Destino | Out-Null
    $expand = Join-Path $env:SystemRoot 'System32\expand.exe'
    & $expand -F:* $Cab $Destino | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo expandir WebView2 Fixed Runtime. Codigo: $LASTEXITCODE"
    }

    return Test-WebView2RuntimeFolder -Ruta $Destino
}

function Get-WebView2RuntimeSource {
    if (-not [string]::IsNullOrWhiteSpace($RutaRuntimeWebView2Portable)) {
        return Test-WebView2RuntimeFolder -Ruta $RutaRuntimeWebView2Portable
    }

    $info = Get-WebView2FixedRuntimeInfo
    New-Item -ItemType Directory -Force -Path $cacheWebView2 | Out-Null
    $cab = Join-Path $cacheWebView2 $info.Archivo
    if (-not (Test-Path -LiteralPath $cab -PathType Leaf)) {
        Write-Host "Descargando WebView2 Fixed Runtime $($info.Version) x64..."
        Invoke-WebRequest -Uri $info.Url -OutFile $cab -UseBasicParsing
    } else {
        Write-Host "Usando WebView2 Fixed Runtime en cache: $cab"
    }

    $carpetaExpandida = Join-Path $cacheWebView2 ("FixedRuntime-" + $info.Version + "-x64")
    return Expand-WebView2Cab -Cab $cab -Destino $carpetaExpandida
}

function New-ReproducibleZip {
    param(
        [string]$Origen,
        [string]$Destino
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $destinoPadre = Split-Path -Parent $Destino
    New-Item -ItemType Directory -Force -Path $destinoPadre | Out-Null
    if (Test-Path -LiteralPath $Destino) {
        Remove-Item -LiteralPath $Destino -Force
    }

    $origenCompleto = (Resolve-Path -LiteralPath $Origen).Path.TrimEnd('\')
    $uriOrigen = [Uri]($origenCompleto + '\')
    $zip = [System.IO.Compression.ZipFile]::Open($Destino, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -LiteralPath $origenCompleto -Recurse -File |
            Sort-Object FullName |
            ForEach-Object {
                $relativo = [Uri]::UnescapeDataString($uriOrigen.MakeRelativeUri([Uri]$_.FullName).ToString())
                $entrada = $zip.CreateEntry($relativo, [System.IO.Compression.CompressionLevel]::Optimal)
                $entrada.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $flujoEntrada = $entrada.Open()
                try {
                    $flujoOrigen = [System.IO.File]::OpenRead($_.FullName)
                    try {
                        $flujoOrigen.CopyTo($flujoEntrada)
                    } finally {
                        $flujoOrigen.Dispose()
                    }
                } finally {
                    $flujoEntrada.Dispose()
                }
            }
    } finally {
        $zip.Dispose()
    }

    if (-not (Test-Path -LiteralPath $Destino -PathType Leaf)) {
        throw "No se genero el ZIP embebido de WebView2: $Destino"
    }
}

function Initialize-WebView2EmbeddedRuntime {
    if (Test-Path -LiteralPath $runtimeZipIntermedio) {
        Remove-Item -LiteralPath $runtimeZipIntermedio -Force
    }

    $origen = Get-WebView2RuntimeSource
    Write-Host 'Generando recurso embebido WebView2Runtime.zip...'
    New-ReproducibleZip -Origen $origen -Destino $runtimeZipIntermedio
    $hash = (Get-FileHash -LiteralPath $runtimeZipIntermedio -Algorithm SHA256).Hash
    Write-Host "WebView2 Runtime embebido preparado. SHA-256 ZIP: $hash"
}

Write-Host 'Restaurando dependencias...'
Invoke-NativeChecked -Descripcion 'dotnet restore' -Comando {
    dotnet restore (Join-Path $raiz 'LanzadorScripts.slnx')
}

Write-Host 'Compilando aplicacion...'
Invoke-NativeChecked -Descripcion 'dotnet build' -Comando {
    dotnet build (Join-Path $raiz 'LanzadorScripts.slnx') -c Release --no-restore
}

Write-Host 'Ejecutando pruebas...'
Invoke-NativeChecked -Descripcion 'dotnet test' -Comando {
    dotnet test (Join-Path $raiz 'Pruebas\LanzadorScripts.Pruebas.csproj') -c Release --no-restore
}

Initialize-WebView2EmbeddedRuntime

Write-Host 'Publicando ejecutable portable...'
if (Test-Path -LiteralPath $salidaCompleta) {
    Remove-Item -LiteralPath $salidaCompleta -Recurse -Force
}

Invoke-NativeChecked -Descripcion 'dotnet publish' -Comando {
    dotnet publish $proyecto `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:SelfContained=true `
        -p:PublishSelfContained=true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishTrimmed=false `
        -p:UseAppHost=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $salidaCompleta
}

$exe = Join-Path $salidaCompleta 'LanzadorScripts.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw 'No se genero LanzadorScripts.exe.'
}

$certificadoFirma = Get-SigningCertificate
if ($null -ne $certificadoFirma) {
    Write-Host 'Firmando ejecutable Authenticode...'
    $firmaExe = Set-AuthenticodeSignature -FilePath $exe -Certificate $certificadoFirma -TimestampServer $TimestampServer
    if ($firmaExe.Status -ne 'Valid') {
        throw "No se pudo firmar el EXE correctamente: $($firmaExe.Status)."
    }
} else {
    if (-not $AllowUnsignedForDev) {
        throw 'No se indico certificado Authenticode. Use -AllowUnsignedForDev solo para pruebas locales.'
    }

    Write-Warning 'Publicacion local sin firma permitida explicitamente para desarrollo.'
}

if ($InicializarArtefactos) {
    if (-not (Test-Path -LiteralPath $RutaScriptsIniciales -PathType Container)) {
        throw "No se encontro la carpeta de scripts iniciales: $RutaScriptsIniciales"
    }

    if (-not (Test-Path -LiteralPath $RutaCarpetaPermisos -PathType Container)) {
        throw "No se encontro la carpeta operativa de permisos: $RutaCarpetaPermisos"
    }

    Write-Host 'Generando permisos y catalogo en la carpeta operativa...'
    $carpetaPermisosCompleta = (Resolve-Path -LiteralPath $RutaCarpetaPermisos).Path
    $permisos = Join-Path $carpetaPermisosCompleta 'permisos.json'
    $catalogo = Join-Path $carpetaPermisosCompleta 'catalogo-scripts.json'
    $scriptsBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Resolve-Path -LiteralPath $RutaScriptsIniciales).Path))
    $salidaBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($carpetaPermisosCompleta))
    $ensambladoGenerador = Join-Path $raiz 'bin\Release\net10.0-windows\win-x64\LanzadorScripts.dll'
    if (-not (Test-Path -LiteralPath $ensambladoGenerador)) {
        throw "No se encontro el ensamblado generador: $ensambladoGenerador"
    }

    Invoke-NativeChecked -Descripcion 'generador de artefactos operativos' -Comando {
        dotnet $ensambladoGenerador `
            --generar-artefactos-iniciales `
            --scripts-base64 $scriptsBase64 `
            --salida-base64 $salidaBase64
    }
    if (-not (Test-Path -LiteralPath $permisos) -or -not (Test-Path -LiteralPath $catalogo)) {
        throw 'No se pudieron generar los artefactos operativos.'
    }
}

$archivosPublicados = @(Get-ChildItem -LiteralPath $salidaCompleta -Recurse -File)
$inesperados = @($archivosPublicados | Where-Object {
    $_.FullName -ne $exe
})
if ($archivosPublicados.Count -ne 1 -or $inesperados.Count -gt 0) {
    $lista = ($archivosPublicados | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
    throw "La publicacion contiene archivos no permitidos. Archivos encontrados:$([Environment]::NewLine)$lista"
}

$archivosLaterales = @($archivosPublicados | Where-Object {
    $_.DirectoryName -eq $salidaCompleta -and (
        $_.Name -like '*.dll' -or
        $_.Name -like '*.deps.json' -or
        $_.Name -like '*.runtimeconfig.json')
})
if ($archivosLaterales.Count -gt 0) {
    $lista = ($archivosLaterales | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
    throw "La publicacion contiene archivos laterales de .NET:$([Environment]::NewLine)$lista"
}

$tamanoExe = (Get-Item -LiteralPath $exe).Length
if ($tamanoExe -lt $tamanoMinimoExe) {
    throw "El EXE generado parece incompleto. Tamano detectado: $tamanoExe bytes."
}

Write-Host "EXE generado: $exe"
Write-Host "Carpeta operativa de permisos: $RutaCarpetaPermisos"
if (-not $InicializarArtefactos) {
    Write-Host 'Los archivos operativos existentes no se han modificado.'
}
