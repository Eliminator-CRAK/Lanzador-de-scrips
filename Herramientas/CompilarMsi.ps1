# (Autor: Alex Roman)
# Descripcion: Compila, configura, firma y valida el MSI x64 de LanzadorScripts.

[CmdletBinding()]
param(
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$CertThumbprint = '6C654649369000DDE0AA70F62645058D9A3437F5',

    [ValidatePattern('^https?://')]
    [string]$TimestampServer = 'http://timestamp.digicert.com',

    [string]$RutaRuntimeWebView2 = '',

    [switch]$DesarrolloSinFirma
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$raiz = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$objRaiz = [System.IO.Path]::GetFullPath((Join-Path $raiz 'obj'))
$objInstalador = [System.IO.Path]::GetFullPath((Join-Path $objRaiz 'Instalador'))
$vdproj = Join-Path $raiz 'Instalador\LanzadorScripts.Instalador.vdproj'
$solucion = Join-Path $raiz 'LanzadorScripts.slnx'
$fuenteHelper = Join-Path $raiz 'Instalador\LanzadorScripts.Instalador.cpp'
$recursoHelper = Join-Path $raiz 'Instalador\LanzadorScripts.Instalador.rc'
$helperExe = Join-Path $objInstalador 'LanzadorScripts.Instalador.exe'
$helperObj = Join-Path $objInstalador 'LanzadorScripts.Instalador.obj'
$helperRes = Join-Path $objInstalador 'LanzadorScripts.Instalador.res'
$validacionMsi = Join-Path $objInstalador 'MsiAdminImage'
$logValidacionMsi = Join-Path $objInstalador 'MsiAdminImage.log'
$msi = Join-Path $raiz 'Instalador\Release\LanzadorScripts-1.7.0-x64.msi'
$publicacion = Join-Path $raiz 'bin\Release\net10.0-windows\win-x64\publish'
$exeInstalado = Join-Path $publicacion 'LanzadorScripts.exe'
$scriptFirma = Join-Path $PSScriptRoot 'FirmarPublicacionInstalada.ps1'
$scriptConfigurar = Join-Path $PSScriptRoot 'ConfigurarMsi.ps1'
$scriptVisualStudio = Join-Path $PSScriptRoot 'PrepararVisualStudioInstalador.ps1'

foreach ($archivo in @(
        $vdproj,
        $solucion,
        $fuenteHelper,
        $recursoHelper,
        $scriptFirma,
        $scriptConfigurar,
        $scriptVisualStudio)) {
    if (-not [System.IO.File]::Exists($archivo)) {
        throw "No existe un archivo necesario para compilar el MSI: $archivo"
    }
}

if ([string]::IsNullOrWhiteSpace($RutaRuntimeWebView2)) {
    $RutaRuntimeWebView2 = Join-Path $raiz 'Recursos\WebView2\FixedRuntime-150.0.4078.48-x64\Microsoft.WebView2.FixedVersionRuntime.150.0.4078.48.x64'
}

$runtime = [System.IO.Path]::GetFullPath($RutaRuntimeWebView2)
if (-not [System.IO.File]::Exists((Join-Path $runtime 'msedgewebview2.exe'))) {
    throw "No se encontro el runtime fijo de WebView2: $runtime"
}

$prefijoObj = $objRaiz.TrimEnd('\') + '\'
if (-not $objInstalador.StartsWith($prefijoObj, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "La carpeta temporal no esta dentro de obj: $objInstalador"
}

& $scriptVisualStudio
if ($LASTEXITCODE -ne 0) {
    throw 'La preparacion de Visual Studio Professional no fue correcta.'
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$rutaVisualStudio = (& $vswhere `
    -products Microsoft.VisualStudio.Product.Professional `
    -version '[18.0,19.0)' `
    -requires Microsoft.VisualStudio.Workload.ManagedDesktop `
    -property installationPath | Select-Object -First 1).Trim()
if ([string]::IsNullOrWhiteSpace($rutaVisualStudio)) {
    throw 'No se pudo resolver Visual Studio Professional 2026.'
}

$devenv = Join-Path $rutaVisualStudio 'Common7\IDE\devenv.com'
$vsDevCmd = Join-Path $rutaVisualStudio 'Common7\Tools\VsDevCmd.bat'
if ([System.IO.Directory]::Exists($objInstalador)) {
    [System.IO.Directory]::Delete($objInstalador, $true)
}

[System.IO.Directory]::CreateDirectory($objInstalador) | Out-Null
$compilarCmd = Join-Path $objInstalador 'CompilarHelper.cmd'
$lineasCompilacion = @(
    '@echo off',
    "call `"$vsDevCmd`" -no_logo -arch=x64 -host_arch=x64",
    'if errorlevel 1 exit /b %errorlevel%',
    "rc.exe /nologo /fo `"$helperRes`" `"$recursoHelper`"",
    'if errorlevel 1 exit /b %errorlevel%',
    ('cl.exe /nologo /std:c++20 /O2 /MT /EHsc /W4 /WX /utf-8 /permissive- /sdl /guard:cf ' +
        "/DUNICODE /D_UNICODE /Fo:`"$helperObj`" /Fe:`"$helperExe`" `"$fuenteHelper`" `"$helperRes`" " +
        '/link /WX /SUBSYSTEM:WINDOWS /MACHINE:X64 /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /GUARD:CF /CETCOMPAT /INCREMENTAL:NO /Brepro'),
    'exit /b %errorlevel%'
)
[System.IO.File]::WriteAllLines($compilarCmd, $lineasCompilacion, [System.Text.Encoding]::ASCII)
& $env:ComSpec /d /c $compilarCmd
if ($LASTEXITCODE -ne 0 -or -not [System.IO.File]::Exists($helperExe)) {
    throw "No se pudo compilar el helper del MSI. Codigo: $LASTEXITCODE"
}

if (-not $DesarrolloSinFirma) {
    & $scriptFirma `
        -RutaArchivo $helperExe `
        -Thumbprint $CertThumbprint `
        -TimestampServer $TimestampServer
    if ($LASTEXITCODE -ne 0) {
        throw 'No se pudo firmar el helper del MSI.'
    }
}

$revisionGit = (& git -C $raiz rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revisionGit -notmatch '^[0-9a-f]{40}$') {
    throw 'No se pudo determinar la revision Git de la compilacion.'
}

$entornoAnterior = @{
    LANZADOR_GIT_REVISION = $env:LANZADOR_GIT_REVISION
    LANZADOR_SIGNING_THUMBPRINT = $env:LANZADOR_SIGNING_THUMBPRINT
    LANZADOR_TIMESTAMP_SERVER = $env:LANZADOR_TIMESTAMP_SERVER
    InstalledWebView2RuntimeSource = $env:InstalledWebView2RuntimeSource
}

try {
    $env:LANZADOR_GIT_REVISION = $revisionGit
    $env:LANZADOR_SIGNING_THUMBPRINT = if ($DesarrolloSinFirma) { '' } else { $CertThumbprint }
    $env:LANZADOR_TIMESTAMP_SERVER = $TimestampServer
    $env:InstalledWebView2RuntimeSource = $runtime
    if ([System.IO.File]::Exists($msi)) {
        [System.IO.File]::Delete($msi)
    }

    & $devenv $solucion /Build Release /Project LanzadorScripts.Instalador /ProjectConfig Release
    if ($LASTEXITCODE -ne 0) {
        throw "Visual Studio no pudo compilar el MSI. Codigo: $LASTEXITCODE"
    }
}
finally {
    foreach ($nombre in $entornoAnterior.Keys) {
        [System.Environment]::SetEnvironmentVariable(
            $nombre,
            $entornoAnterior[$nombre],
            [System.EnvironmentVariableTarget]::Process)
    }
}

if (-not [System.IO.File]::Exists($msi) -or -not [System.IO.File]::Exists($exeInstalado)) {
    throw 'Visual Studio no genero el MSI o el ejecutable instalado.'
}

$pwsh = Join-Path $PSHOME 'pwsh.exe'
& $pwsh -NoProfile -File $scriptConfigurar -RutaMsi $msi -RutaHerramientaInstalador $helperExe
if ($LASTEXITCODE -ne 0) {
    throw 'No se pudieron configurar las tablas del MSI.'
}

if (-not $DesarrolloSinFirma) {
    & $scriptFirma `
        -RutaArchivo $msi `
        -Thumbprint $CertThumbprint `
        -TimestampServer $TimestampServer
    if ($LASTEXITCODE -ne 0) {
        throw 'No se pudo firmar el MSI.'
    }
}

$versionExe = (Get-Item -LiteralPath $exeInstalado).VersionInfo.FileVersion
if ($versionExe -ne '1.7.0.0') {
    throw "La version del ejecutable instalado no es 1.7.0.0: $versionExe"
}

$productoExe = (Get-Item -LiteralPath $exeInstalado).VersionInfo.ProductVersion
$productoEsperado = "1.7.0+$revisionGit.installed"
if ($productoExe -ne $productoEsperado) {
    throw "La version de producto instalada no identifica el commit: $productoExe"
}

if (-not $DesarrolloSinFirma) {
    foreach ($archivo in @($helperExe, $exeInstalado, $msi)) {
        $firma = Get-AuthenticodeSignature -LiteralPath $archivo
        if ($firma.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
            $null -eq $firma.TimeStamperCertificate) {
            throw "La firma Authenticode no es valida: $archivo"
        }
    }
}

$instalador = New-Object -ComObject WindowsInstaller.Installer
$baseDatos = $instalador.OpenDatabase((Resolve-Path -LiteralPath $msi).Path, 0)
try {
    $vista = $baseDatos.OpenView('SELECT `Property`, `Value` FROM `Property`')
    $vista.Execute()
    $propiedades = @{}
    while ($fila = $vista.Fetch()) {
        $propiedades[[string]$fila.StringData(1)] = [string]$fila.StringData(2)
    }

    $vista.Close()

    if ($propiedades.ProductVersion -ne '1.7.0' -or
        $propiedades.ALLUSERS -ne '1' -or
        $propiedades.LANZADOR_MSI_CONFIGURADO -ne '1' -or
        $propiedades.UpgradeCode -ne '{24169C78-5164-45C8-AB1A-AFC281D86DE9}') {
        throw 'Los metadatos finales del MSI no coinciden con el contrato 1.7.0.'
    }
}
finally {
    [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($baseDatos) | Out-Null
    [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($instalador) | Out-Null
}

$prefijoObjInstalador = $objInstalador.TrimEnd('\') + '\'
if (-not $validacionMsi.StartsWith($prefijoObjInstalador, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'La imagen administrativa de validacion queda fuera de obj.'
}

if ([System.IO.Directory]::Exists($validacionMsi)) {
    [System.IO.Directory]::Delete($validacionMsi, $true)
}

if ([System.IO.File]::Exists($logValidacionMsi)) {
    [System.IO.File]::Delete($logValidacionMsi)
}

[System.IO.Directory]::CreateDirectory($validacionMsi) | Out-Null
$argumentosMsi = @(
    '/a',
    "`"$msi`"",
    '/qn',
    '/norestart',
    "TARGETDIR=`"$validacionMsi`"",
    '/l*v',
    "`"$logValidacionMsi`""
)
$procesoMsi = Start-Process `
    -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') `
    -ArgumentList $argumentosMsi `
    -Wait `
    -PassThru `
    -WindowStyle Hidden
if ($procesoMsi.ExitCode -ne 0) {
    throw "La extraccion administrativa del MSI fallo con codigo $($procesoMsi.ExitCode). Log: $logValidacionMsi"
}

$exeExtraido = Join-Path $validacionMsi 'LanzadorScripts.exe'
if (-not [System.IO.File]::Exists($exeExtraido)) {
    throw 'La imagen administrativa no contiene LanzadorScripts.exe.'
}

$versionExtraida = (Get-Item -LiteralPath $exeExtraido).VersionInfo
if ($versionExtraida.FileVersion -ne '1.7.0.0' -or
    $versionExtraida.ProductVersion -ne $productoEsperado) {
    throw 'El ejecutable incluido en el MSI no conserva la version esperada.'
}

$loadersExtraidos = @(Get-ChildItem -LiteralPath $validacionMsi -Recurse -File -Filter 'WebView2Loader.dll')
if ($loadersExtraidos.Count -ne 1 -or
    $loadersExtraidos[0].DirectoryName -ne $validacionMsi) {
    throw 'La imagen administrativa no contiene una unica WebView2Loader.dll en su raiz.'
}

$documentacionExtraida = @(Get-ChildItem -LiteralPath $validacionMsi -Recurse -File -Filter 'Microsoft.Web.WebView2*.xml')
if ($documentacionExtraida.Count -ne 0) {
    throw 'La imagen administrativa contiene documentacion WebView2 innecesaria.'
}

if (-not $DesarrolloSinFirma) {
    $firmaExtraida = Get-AuthenticodeSignature -LiteralPath $exeExtraido
    if ($firmaExtraida.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $firmaExtraida.TimeStamperCertificate) {
        throw 'El ejecutable incluido en el MSI no conserva una firma Authenticode valida.'
    }
}

[System.IO.Directory]::Delete($validacionMsi, $true)

$hash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
Write-Host "MSI generado: $msi"
Write-Host "SHA-256: $hash"
