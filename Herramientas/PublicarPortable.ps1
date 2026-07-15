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

if ($PSVersionTable.PSEdition -ne 'Core' -or
    $PSVersionTable.PSVersion.Major -ne 7 -or
    $PSVersionTable.PSVersion.Minor -ne 6) {
    throw 'Ejecute esta publicacion con pwsh 7.6.x para generar el ZIP WebView2 reproducible.'
}

$raiz = Split-Path -Parent $PSScriptRoot
$proyecto = Join-Path $raiz 'LanzadorScripts.csproj'
$salida = Join-Path $raiz 'publicacion'
$salidaStaging = Join-Path $raiz 'obj\PublicacionStaging'
$salidaAnterior = Join-Path $raiz "obj\PublicacionAnterior-$PID"
$tamanoMinimoExe = 209715200
$cacheWebView2 = Join-Path $raiz 'Recursos\WebView2'
$runtimeZipIntermedio = Join-Path $raiz 'obj\WebView2Runtime\WebView2Runtime.zip'
$versionWebView2Fijada = '150.0.4078.48'
$nombreCabWebView2Fijado = "Microsoft.WebView2.FixedVersionRuntime.$versionWebView2Fijada.x64.cab"
$urlCabWebView2Fijado = 'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/60926d99-f201-46bb-91a0-d868dc06b275/Microsoft.WebView2.FixedVersionRuntime.150.0.4078.48.x64.cab'
$hashCabWebView2Fijado = '9E347BA96D031E381D1041D1C20FD434D457875C422EEAC3F40EEE4A5E0AB5C0'
$hashZipWebView2Fijado = '80C46993E2D5922EFDF6463ACDA737BA0525993D4D7757D377C38F50D8BB417B'
$hashEjecutableWebView2Fijado = '30428A9075E5706B5E4A77E324B4331326566CDA027F49A8922089733C728859'
$hashContenidoRuntimeFijado = '3345CEC7106D6A8EB3A5770DFF97DF36CB0750DF005331B54AB551CDF11E3DFB'
$arquitecturaPeX64 = 0x8664
$nombreRecursoWebView2 = 'Recursos.WebView2Runtime.zip'
$raizCompleta = [System.IO.Path]::GetFullPath($raiz).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$salidaCompleta = [System.IO.Path]::GetFullPath($salida)
$stagingCompleta = [System.IO.Path]::GetFullPath($salidaStaging)
$salidaAnteriorCompleta = [System.IO.Path]::GetFullPath($salidaAnterior)
$prefijoPermitido = $raizCompleta + [System.IO.Path]::DirectorySeparatorChar

if (-not $salidaCompleta.StartsWith($prefijoPermitido, [System.StringComparison]::OrdinalIgnoreCase) -or
    [System.IO.Path]::GetFileName($salidaCompleta) -ne 'publicacion') {
    throw "La carpeta de publicacion no esta dentro del proyecto: $salidaCompleta"
}

$carpetaObjCompleta = [System.IO.Path]::GetFullPath((Join-Path $raiz 'obj')).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $stagingCompleta.StartsWith($carpetaObjCompleta, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not $salidaAnteriorCompleta.StartsWith($carpetaObjCompleta, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Las carpetas temporales de publicacion no estan dentro de obj.'
}

$proyectoXml = [xml](Get-Content -LiteralPath $proyecto -Raw)
$propiedadesVersion = @($proyectoXml.Project.PropertyGroup) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_.Version) } |
    Select-Object -First 1
if ($null -eq $propiedadesVersion -or
    [string]::IsNullOrWhiteSpace($propiedadesVersion.FileVersion)) {
    throw 'No se pudo leer la version de publicacion desde LanzadorScripts.csproj.'
}

$versionProductoEsperada = [string]$propiedadesVersion.Version
$versionArchivoEsperada = [string]$propiedadesVersion.FileVersion
$revisionGitEsperada = (& git -C $raiz rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($revisionGitEsperada)) {
    throw 'No se pudo obtener la revision Git que identificara el EXE.'
}

$cambiosGit = @(& git -C $raiz status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw 'No se pudo comprobar el estado Git antes de publicar.'
}

if ($cambiosGit.Count -gt 0) {
    throw 'La publicacion final requiere que los cambios versionados esten incluidos en un commit.'
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

function Assert-FileSha256 {
    param(
        [string]$Ruta,
        [string]$HashEsperado,
        [string]$Descripcion
    )

    if (-not (Test-Path -LiteralPath $Ruta -PathType Leaf)) {
        throw "No se encontro $Descripcion`: $Ruta"
    }

    $hashActual = (Get-FileHash -LiteralPath $Ruta -Algorithm SHA256).Hash
    if (-not $hashActual.Equals($HashEsperado, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "El SHA-256 de $Descripcion no coincide. Esperado: $HashEsperado. Detectado: $hashActual."
    }

    return $hashActual
}

function Get-PortableExecutableMachine {
    param(
        [string]$Ruta
    )

    $flujo = [System.IO.File]::OpenRead($Ruta)
    $lector = [System.IO.BinaryReader]::new($flujo)
    try {
        if ($flujo.Length -lt 64) {
            throw "El ejecutable PE esta truncado: $Ruta"
        }

        $flujo.Position = 60
        $desplazamientoPe = $lector.ReadInt32()
        if ($desplazamientoPe -lt 0 -or ($desplazamientoPe + 6) -gt $flujo.Length) {
            throw "La cabecera PE no es valida: $Ruta"
        }

        $flujo.Position = $desplazamientoPe
        if ($lector.ReadUInt32() -ne 0x00004550) {
            throw "La firma PE no es valida: $Ruta"
        }

        return [int]$lector.ReadUInt16()
    } finally {
        $lector.Dispose()
        $flujo.Dispose()
    }
}

function Get-RuntimeContentHash {
    param(
        [string]$Ruta
    )

    # Calcula la huella de rutas, tamanos y contenido del runtime.
    $raizRuntime = (Resolve-Path -LiteralPath $Ruta).Path.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $rutas = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
    $relativas = [System.Collections.Generic.List[string]]::new()
    foreach ($archivo in [System.IO.Directory]::EnumerateFiles($raizRuntime, '*', [System.IO.SearchOption]::AllDirectories)) {
        if ([System.IO.Path]::GetFileName($archivo) -eq '.lanzador-webview2.sha256') {
            continue
        }

        $relativa = [System.IO.Path]::GetRelativePath($raizRuntime, $archivo).Replace('\', '/')
        $rutas.Add($relativa, $archivo)
        $relativas.Add($relativa)
    }

    $ordenadas = [string[]]$relativas.ToArray()
    [System.Array]::Sort($ordenadas, [System.StringComparer]::Ordinal)
    $integridad = [System.Security.Cryptography.IncrementalHash]::CreateHash(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($relativa in $ordenadas) {
            $longitud = ([System.IO.FileInfo]$rutas[$relativa]).Length.ToString(
                [System.Globalization.CultureInfo]::InvariantCulture)
            foreach ($valor in @($relativa, $longitud)) {
                $integridad.AppendData([System.Text.Encoding]::UTF8.GetBytes($valor + "`n"))
            }

            $flujo = [System.IO.File]::OpenRead($rutas[$relativa])
            $sha256 = [System.Security.Cryptography.SHA256]::Create()
            try {
                $hashArchivo = [Convert]::ToHexString($sha256.ComputeHash($flujo))
            } finally {
                $sha256.Dispose()
                $flujo.Dispose()
            }

            $integridad.AppendData([System.Text.Encoding]::UTF8.GetBytes($hashArchivo + "`n"))
        }

        return [Convert]::ToHexString($integridad.GetHashAndReset())
    } finally {
        $integridad.Dispose()
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
    $ejecutablesWebView2 = @(Get-ChildItem -LiteralPath $rutaCompleta -Filter 'msedgewebview2.exe' -Recurse -File)
    if ($ejecutablesWebView2.Count -ne 1) {
        throw "La carpeta de runtime WebView2 no contiene msedgewebview2.exe: $rutaCompleta"
    }

    $ejecutableWebView2 = $ejecutablesWebView2[0]
    Assert-FileSha256 `
        -Ruta $ejecutableWebView2.FullName `
        -HashEsperado $hashEjecutableWebView2Fijado `
        -Descripcion 'msedgewebview2.exe' | Out-Null

    $versionEjecutable = $ejecutableWebView2.VersionInfo.FileVersion
    $versionProducto = $ejecutableWebView2.VersionInfo.ProductVersion
    if ($versionEjecutable -ne $versionWebView2Fijada -or $versionProducto -ne $versionWebView2Fijada) {
        throw "La version de msedgewebview2.exe no coincide. Esperada: $versionWebView2Fijada. Archivo: $versionEjecutable. Producto: $versionProducto."
    }

    $arquitecturaDetectada = Get-PortableExecutableMachine -Ruta $ejecutableWebView2.FullName
    if ($arquitecturaDetectada -ne $arquitecturaPeX64) {
        throw ('msedgewebview2.exe no es x64. PE esperado: 0x{0:X4}. Detectado: 0x{1:X4}.' -f $arquitecturaPeX64, $arquitecturaDetectada)
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

    New-Item -ItemType Directory -Force -Path $cacheWebView2 | Out-Null
    $cab = Join-Path $cacheWebView2 $nombreCabWebView2Fijado
    $cabValido = $false
    if (Test-Path -LiteralPath $cab -PathType Leaf) {
        try {
            Assert-FileSha256 `
                -Ruta $cab `
                -HashEsperado $hashCabWebView2Fijado `
                -Descripcion "CAB WebView2 $versionWebView2Fijada x64" | Out-Null
            $cabValido = $true
            Write-Host "Usando WebView2 Fixed Runtime en cache: $cab"
        } catch {
            Write-Warning "El CAB WebView2 en cache no es valido y se descargara de nuevo."
        }
    }

    if (-not $cabValido) {
        $cabTemporal = "$cab.$PID.tmp"
        if (Test-Path -LiteralPath $cabTemporal) {
            Remove-Item -LiteralPath $cabTemporal -Force
        }

        Write-Host "Descargando WebView2 Fixed Runtime $versionWebView2Fijada x64..."
        try {
            Invoke-WebRequest -Uri $urlCabWebView2Fijado -OutFile $cabTemporal -UseBasicParsing
            Assert-FileSha256 `
                -Ruta $cabTemporal `
                -HashEsperado $hashCabWebView2Fijado `
                -Descripcion "CAB WebView2 $versionWebView2Fijada x64 descargado" | Out-Null
            Move-Item -LiteralPath $cabTemporal -Destination $cab -Force
        } finally {
            if (Test-Path -LiteralPath $cabTemporal) {
                Remove-Item -LiteralPath $cabTemporal -Force
            }
        }
    }

    Assert-FileSha256 `
        -Ruta $cab `
        -HashEsperado $hashCabWebView2Fijado `
        -Descripcion "CAB WebView2 $versionWebView2Fijada x64" | Out-Null

    $carpetaExpandida = Join-Path $cacheWebView2 ("FixedRuntime-" + $versionWebView2Fijada + "-x64")
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
    $hashContenidoRuntime = Get-RuntimeContentHash -Ruta $origen
    if (-not $hashContenidoRuntime.Equals($hashContenidoRuntimeFijado, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "El contenido extraido de WebView2 no coincide. Esperado: $hashContenidoRuntimeFijado. Detectado: $hashContenidoRuntime."
    }

    Write-Host 'Generando recurso embebido WebView2Runtime.zip...'
    New-ReproducibleZip -Origen $origen -Destino $runtimeZipIntermedio
    Assert-FileSha256 `
        -Ruta $runtimeZipIntermedio `
        -HashEsperado $hashZipWebView2Fijado `
        -Descripcion 'ZIP embebido de WebView2' | Out-Null
    Write-Host "WebView2 Runtime $versionWebView2Fijada x64 preparado. SHA-256 ZIP: $hashZipWebView2Fijado"
}

function Assert-WebView2EmbeddedResource {
    param(
        [string]$RutaEnsamblado
    )

    if (-not (Test-Path -LiteralPath $RutaEnsamblado -PathType Leaf)) {
        throw "No se encontro el ensamblado publicado para validar WebView2: $RutaEnsamblado"
    }

    $ensamblado = [System.Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $RutaEnsamblado).Path)
    if ($ensamblado.GetManifestResourceNames() -notcontains $nombreRecursoWebView2) {
        throw "El ensamblado publicado no contiene el recurso $nombreRecursoWebView2."
    }

    $flujoRecurso = $ensamblado.GetManifestResourceStream($nombreRecursoWebView2)
    if ($null -eq $flujoRecurso) {
        throw "No se pudo abrir el recurso embebido $nombreRecursoWebView2."
    }

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashRecurso = [Convert]::ToHexString($sha256.ComputeHash($flujoRecurso))
    } finally {
        $sha256.Dispose()
        $flujoRecurso.Dispose()
    }

    if (-not $hashRecurso.Equals($hashZipWebView2Fijado, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "El recurso WebView2 embebido no coincide con el ZIP fijado. Esperado: $hashZipWebView2Fijado. Detectado: $hashRecurso."
    }
}

function Assert-PublishedExecutable {
    param(
        [string]$RutaExe,
        [bool]$DebeEstarFirmado
    )

    # Comprueba que el EXE corresponde al proyecto y al commit actuales.
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($RutaExe)
    if (-not $version.FileVersion.Equals($versionArchivoEsperada, [System.StringComparison]::Ordinal)) {
        throw "La version de archivo del EXE no coincide. Esperada: $versionArchivoEsperada. Detectada: $($version.FileVersion)."
    }

    $productoEsperado = "$versionProductoEsperada+$revisionGitEsperada"
    if (-not $version.ProductVersion.Equals($productoEsperado, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "ProductVersion no identifica el commit publicado. Esperado: $productoEsperado. Detectado: $($version.ProductVersion)."
    }

    $firmaFinal = Get-AuthenticodeSignature -FilePath $RutaExe
    if ($DebeEstarFirmado) {
        if ($firmaFinal.Status -ne 'Valid') {
            throw "La firma final del EXE no es valida: $($firmaFinal.Status)."
        }

        if ($null -eq $firmaFinal.TimeStamperCertificate) {
            throw 'El EXE final no contiene sello de tiempo.'
        }
    }

    $hashFinal = (Get-FileHash -LiteralPath $RutaExe -Algorithm SHA256).Hash
    Write-Host "Version final: $($version.FileVersion)"
    Write-Host "ProductVersion final: $($version.ProductVersion)"
    Write-Host "SHA-256 final: $hashFinal"
    if ($DebeEstarFirmado) {
        Write-Host "Firma final: $($firmaFinal.Status); sello: $($firmaFinal.TimeStamperCertificate.Subject)"
    }

    return $hashFinal
}

Write-Host 'Restaurando dependencias...'
Invoke-NativeChecked -Descripcion 'dotnet restore' -Comando {
    dotnet restore (Join-Path $raiz 'LanzadorScripts.slnx')
}

Initialize-WebView2EmbeddedRuntime

Write-Host 'Compilando aplicacion...'
Invoke-NativeChecked -Descripcion 'dotnet build' -Comando {
    dotnet build (Join-Path $raiz 'LanzadorScripts.slnx') -c Release --no-restore
}

Write-Host 'Ejecutando pruebas...'
Invoke-NativeChecked -Descripcion 'dotnet test' -Comando {
    dotnet test (Join-Path $raiz 'Pruebas\LanzadorScripts.Pruebas.csproj') -c Release --no-restore
}

Write-Host 'Publicando ejecutable portable...'
if (Test-Path -LiteralPath $stagingCompleta) {
    Remove-Item -LiteralPath $stagingCompleta -Recurse -Force
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
        -o $stagingCompleta
}

$exe = Join-Path $stagingCompleta 'LanzadorScripts.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw 'No se genero LanzadorScripts.exe.'
}

$ensambladoPublicado = Join-Path $raiz 'bin\Release\net10.0-windows\win-x64\LanzadorScripts.dll'
Assert-WebView2EmbeddedResource -RutaEnsamblado $ensambladoPublicado

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

$archivosPublicados = @(Get-ChildItem -LiteralPath $stagingCompleta -Recurse -File)
$inesperados = @($archivosPublicados | Where-Object {
    $_.FullName -ne $exe
})
if ($archivosPublicados.Count -ne 1 -or $inesperados.Count -gt 0) {
    $lista = ($archivosPublicados | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
    throw "La publicacion contiene archivos no permitidos. Archivos encontrados:$([Environment]::NewLine)$lista"
}

$archivosLaterales = @($archivosPublicados | Where-Object {
    $_.DirectoryName -eq $stagingCompleta -and (
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

$hashExeValidado = Assert-PublishedExecutable -RutaExe $exe -DebeEstarFirmado ($null -ne $certificadoFirma)

# Sustituye la publicacion solo despues de validar todo el staging.
$habiaPublicacionAnterior = Test-Path -LiteralPath $salidaCompleta
$publicacionNuevaInstalada = $false
try {
    if ($habiaPublicacionAnterior) {
        Move-Item -LiteralPath $salidaCompleta -Destination $salidaAnteriorCompleta
    }

    Move-Item -LiteralPath $stagingCompleta -Destination $salidaCompleta
    $publicacionNuevaInstalada = $true

    $exeFinal = Join-Path $salidaCompleta 'LanzadorScripts.exe'
    $hashExeFinal = (Get-FileHash -LiteralPath $exeFinal -Algorithm SHA256).Hash
    if (-not $hashExeFinal.Equals($hashExeValidado, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "El EXE cambio durante la sustitucion final. Esperado: $hashExeValidado. Detectado: $hashExeFinal."
    }
} catch {
    $errorPublicacion = $_.Exception
    try {
        if ($publicacionNuevaInstalada -and (Test-Path -LiteralPath $salidaCompleta)) {
            Move-Item -LiteralPath $salidaCompleta -Destination $stagingCompleta
            $publicacionNuevaInstalada = $false
        }

        if (-not (Test-Path -LiteralPath $salidaCompleta) -and
            (Test-Path -LiteralPath $salidaAnteriorCompleta)) {
            Move-Item -LiteralPath $salidaAnteriorCompleta -Destination $salidaCompleta
        }
    } catch {
        throw "Fallo la sustitucion final: $($errorPublicacion.Message). Tambien fallo la restauracion automatica: $($_.Exception.Message)."
    }

    throw $errorPublicacion
}

if (Test-Path -LiteralPath $salidaAnteriorCompleta) {
    try {
        Remove-Item -LiteralPath $salidaAnteriorCompleta -Recurse -Force
    } catch {
        Write-Warning "La publicacion anterior quedo retenida temporalmente en $salidaAnteriorCompleta."
    }
}

Write-Host "EXE generado: $exeFinal"
Write-Host "Carpeta operativa de permisos: $RutaCarpetaPermisos"
if (-not $InicializarArtefactos) {
    Write-Host 'Los archivos operativos existentes no se han modificado.'
}
