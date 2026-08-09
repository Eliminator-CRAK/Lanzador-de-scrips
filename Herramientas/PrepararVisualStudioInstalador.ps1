# (Autor: Alex Roman)
# Descripcion: Verifica o prepara Visual Studio Professional 2026 para compilar el proyecto MSI.

[CmdletBinding()]
param(
    [switch]$Instalar
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not [System.IO.File]::Exists($vswhere)) {
    throw 'No se encontro vswhere. Instale Visual Studio Professional 2026.'
}

$resultado = & $vswhere `
    -products Microsoft.VisualStudio.Product.Professional `
    -version '[18.0,19.0)' `
    -format json `
    -utf8
if ($LASTEXITCODE -ne 0) {
    throw 'vswhere no pudo consultar Visual Studio Professional 2026.'
}

$instancias = @($resultado | ConvertFrom-Json)
if ($instancias.Count -ne 1) {
    throw "Se esperaba una unica instalacion de Visual Studio Professional 2026 y se detectaron $($instancias.Count)."
}

$instancia = $instancias[0]
$rutaVisualStudio = [System.IO.Path]::GetFullPath([string]$instancia.installationPath)
$devenv = Join-Path $rutaVisualStudio 'Common7\IDE\devenv.com'
$vsixInstaller = Join-Path $rutaVisualStudio 'Common7\IDE\VSIXInstaller.exe'
$manifiestoVsi = Join-Path $rutaVisualStudio 'Common7\IDE\CommonExtensions\Microsoft\VSI\extension.vsixmanifest'
$cargaTrabajo = 'Microsoft.VisualStudio.Workload.ManagedDesktop'
$componenteCpp = 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
$versionExtension = '3.0.0'
$hashExtension = '36D2D52176DD7B2FA8D03E80652ACB063498CA3990E101C5CE2350446826541F'

function Test-CargaTrabajo {
    $ruta = & $vswhere `
        -products Microsoft.VisualStudio.Product.Professional `
        -version '[18.0,19.0)' `
        -requires $cargaTrabajo $componenteCpp `
        -property installationPath
    return $LASTEXITCODE -eq 0 -and
        -not [string]::IsNullOrWhiteSpace(($ruta | Select-Object -First 1))
}

function Test-ExtensionInstalador {
    if (-not [System.IO.File]::Exists($manifiestoVsi)) {
        return $false
    }

    [xml]$manifiesto = [System.IO.File]::ReadAllText($manifiestoVsi)
    $identidad = $manifiesto.PackageManifest.Metadata.Identity
    return $identidad.Id -eq 'VSInstallerProjects2022' -and
        ([version]$identidad.Version) -eq [version]$versionExtension
}

if ($Instalar -and -not (Test-CargaTrabajo)) {
    $identidadActual = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identidadActual)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Ejecute este script como administrador para agregar la carga de trabajo de escritorio.'
    }

    $setup = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\setup.exe'
    if (-not [System.IO.File]::Exists($setup)) {
        throw "No se encontro el instalador de Visual Studio: $setup"
    }

    $proceso = Start-Process `
        -FilePath $setup `
        -ArgumentList @(
            'modify',
            '--installPath', $rutaVisualStudio,
            '--add', $cargaTrabajo,
            '--add', $componenteCpp,
            '--includeRecommended',
            '--passive',
            '--norestart'
        ) `
        -Wait `
        -PassThru `
        -WindowStyle Hidden
    if ($proceso.ExitCode -ne 0 -and $proceso.ExitCode -ne 3010) {
        throw "Visual Studio Installer termino con codigo $($proceso.ExitCode)."
    }
}

if ($Instalar -and -not (Test-ExtensionInstalador)) {
    if (-not [System.IO.File]::Exists($vsixInstaller)) {
        throw "No se encontro VSIXInstaller: $vsixInstaller"
    }

    $urlVsix = "https://marketplace.visualstudio.com/_apis/public/gallery/publishers/VisualStudioClient/vsextensions/MicrosoftVisualStudio2022InstallerProjects/$versionExtension/vspackage"
    $rutaVsix = Join-Path $env:TEMP ("InstallerProjects2022-{0}.vsix" -f [guid]::NewGuid().ToString('N'))
    try {
        Invoke-WebRequest -Uri $urlVsix -OutFile $rutaVsix -UseBasicParsing
        $hashDescargado = (Get-FileHash -LiteralPath $rutaVsix -Algorithm SHA256).Hash
        if (-not $hashDescargado.Equals($hashExtension, [StringComparison]::OrdinalIgnoreCase)) {
            throw "La huella de Installer Projects $versionExtension no es valida: $hashDescargado"
        }

        $proceso = Start-Process `
            -FilePath $vsixInstaller `
            -ArgumentList @('/quiet', '/admin', "/instanceIds:$($instancia.instanceId)", $rutaVsix) `
            -Wait `
            -PassThru `
            -WindowStyle Hidden
        if ($proceso.ExitCode -notin @(0, 1001)) {
            throw "VSIXInstaller termino con codigo $($proceso.ExitCode)."
        }
    }
    finally {
        if ([System.IO.File]::Exists($rutaVsix)) {
            [System.IO.File]::Delete($rutaVsix)
        }
    }
}

if (-not (Test-CargaTrabajo)) {
    throw "Faltan $cargaTrabajo o $componenteCpp en Visual Studio Professional 2026."
}

if (-not (Test-ExtensionInstalador)) {
    throw "Falta Microsoft Visual Studio Installer Projects 2022 version $versionExtension."
}

if (-not [System.IO.File]::Exists($devenv)) {
    throw "No se encontro devenv.com: $devenv"
}

Write-Host "Visual Studio Professional listo: $rutaVisualStudio"
Write-Host "Compilador MSI listo: $devenv"
