# (Autor: Alex Roman)
# Descripcion: Ejecuta las etapas PowerShell del CI de compilacion y publicacion.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('PrepararPowerShell', 'VerificarCpp', 'Publicar', 'VerificarArtefacto')]
    [string]$Etapa
)

$ErrorActionPreference = 'Stop'
$raizRepositorio = Split-Path -Parent $PSScriptRoot
$huellaFirmaEsperada = '6C654649369000DDE0AA70F62645058D9A3437F5'

function Preparar-PowerShell {
    # Instala la version fijada de PowerShell despues de verificar su huella.
    $version = '7.6.0'
    $hashEsperado = '9E725837AF682B87BB212CD1EFE3657C06C540404203810857EC2516AE2CA322'
    $zip = Join-Path $env:RUNNER_TEMP "PowerShell-$version-win-x64.zip"
    $destino = Join-Path $env:RUNNER_TEMP "PowerShell-$version"
    $url = "https://github.com/PowerShell/PowerShell/releases/download/v$version/PowerShell-$version-win-x64.zip"

    Invoke-WebRequest -Uri $url -OutFile $zip
    $hashReal = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    if ($hashReal -ne $hashEsperado) {
        throw "Hash de PowerShell inesperado: $hashReal"
    }

    Expand-Archive -LiteralPath $zip -DestinationPath $destino -Force
    & (Join-Path $destino 'pwsh.exe') -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'
    [IO.File]::AppendAllText(
        $env:GITHUB_PATH,
        "$destino`r`n",
        [Text.UTF8Encoding]::new($false))
}

function Verificar-Cpp {
    # Comprueba que el runner contiene las herramientas C++ x64.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $instalacion = & $vswhere `
        -latest `
        -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath
    if ([string]::IsNullOrWhiteSpace($instalacion)) {
        throw 'La imagen no contiene las herramientas C++ x64 requeridas.'
    }
}

function Confiar-CertificadoFirmaCi {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificado
    )

    # Confia solo en el certificado fijado dentro del runner efimero.
    if (-not $Certificado.Thumbprint.Equals(
        $huellaFirmaEsperada,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Certificado de firma inesperado: $($Certificado.Thumbprint)."
    }
    if (-not $Certificado.HasPrivateKey) {
        throw 'El certificado de firma no contiene clave privada.'
    }
    if ($Certificado.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
        throw 'El certificado de firma esta caducado.'
    }

    if ($env:GITHUB_ACTIONS -eq 'true') {
        # Agrega solo la parte publica sin abrir dialogos en el runner.
        $bytesPublicos = $Certificado.Export(
            [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
        $certificadoPublico = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $bytesPublicos)
        try {
            foreach ($nombreAlmacen in @(
                [System.Security.Cryptography.X509Certificates.StoreName]::Root,
                [System.Security.Cryptography.X509Certificates.StoreName]::TrustedPublisher)) {
                Write-Host "Aprovisionando LocalMachine\$nombreAlmacen..."
                $almacen = [System.Security.Cryptography.X509Certificates.X509Store]::new(
                    $nombreAlmacen,
                    [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
                try {
                    $almacen.Open(
                        [System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                    $almacen.Add($certificadoPublico)
                    $coincidencias = $almacen.Certificates.Find(
                        [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                        $Certificado.Thumbprint,
                        $false)
                    if ($coincidencias.Count -lt 1) {
                        throw "No se pudo confiar en el certificado dentro de $nombreAlmacen."
                    }
                }
                finally {
                    $almacen.Close()
                }
            }
        }
        finally {
            $certificadoPublico.Dispose()
        }
    }
}

function Publicar-Aplicacion {
    # Firma solo main o etiquetas y crea builds de desarrollo en ramas.
    if ($PSVersionTable.PSVersion.Major -ne 7 -or $PSVersionTable.PSVersion.Minor -ne 6) {
        throw "La publicacion exige PowerShell 7.6.x. Version activa: $($PSVersionTable.PSVersion)"
    }

    if ($env:RELEASE_BUILD -eq 'true') {
        if ([string]::IsNullOrWhiteSpace($env:WINDOWS_SIGNING_CERT_BASE64)) {
            throw 'Falta WINDOWS_SIGNING_CERT_BASE64. Main y las etiquetas exigen firma Authenticode.'
        }
        if ([string]::IsNullOrWhiteSpace($env:WINDOWS_SIGNING_CERT_PASSWORD)) {
            throw 'Falta WINDOWS_SIGNING_CERT_PASSWORD.'
        }

        $certPath = Join-Path $env:RUNNER_TEMP 'lanzador-signing.pfx'
        try {
            Write-Host 'Cargando certificado de firma del runner...'
            [IO.File]::WriteAllBytes(
                $certPath,
                [Convert]::FromBase64String($env:WINDOWS_SIGNING_CERT_BASE64))
            $securePassword = ConvertTo-SecureString `
                $env:WINDOWS_SIGNING_CERT_PASSWORD `
                -AsPlainText `
                -Force
            $certificado = Get-PfxCertificate `
                -FilePath $certPath `
                -Password $securePassword
            Write-Host 'Aprovisionando confianza del certificado en el runner...'
            Confiar-CertificadoFirmaCi -Certificado $certificado
            Write-Host 'Certificado de firma preparado correctamente.'
            & "$PSScriptRoot\PublicarPortable.ps1" `
                -CertPath $certPath `
                -CertPassword $securePassword
        }
        finally {
            Remove-Item -LiteralPath $certPath -Force -ErrorAction SilentlyContinue
        }
    }
    else {
        & "$PSScriptRoot\PublicarPortable.ps1" -AllowUnsignedForDev
    }
}

function Verificar-Artefacto {
    # Comprueba los dos ejecutables y sus firmas.
    $carpeta = Join-Path $raizRepositorio 'publicacion'
    $ejecutablesEsperados = @(
        (Join-Path $carpeta 'LanzadorScripts.exe'),
        (Join-Path $carpeta 'LanzadorScripts_Portable.exe')
    )
    foreach ($exe in $ejecutablesEsperados) {
        if (-not (Test-Path -LiteralPath $exe)) {
            throw "No se genero $(Split-Path -Leaf $exe)."
        }
    }

    $archivos = @(Get-ChildItem -LiteralPath $carpeta -Recurse -File)
    if ($archivos.Count -ne 2 -or
        @($archivos | Where-Object { $_.FullName -notin $ejecutablesEsperados }).Count -gt 0) {
        throw "La publicacion debe generar exactamente los dos EXE esperados. Archivos: $($archivos.Count)"
    }

    foreach ($exe in $ejecutablesEsperados) {
        $firma = Get-AuthenticodeSignature -LiteralPath $exe
        if ($env:RELEASE_BUILD -eq 'true' -and $firma.Status -ne 'Valid') {
            throw "La firma de $(Split-Path -Leaf $exe) no es valida: $($firma.Status)."
        }

        Get-FileHash -LiteralPath $exe -Algorithm SHA256 | Format-List
    }
}

Push-Location $raizRepositorio
try {
    # Ejecuta solamente la etapa solicitada por el workflow.
    switch ($Etapa) {
        'PrepararPowerShell' { Preparar-PowerShell }
        'VerificarCpp' { Verificar-Cpp }
        'Publicar' { Publicar-Aplicacion }
        'VerificarArtefacto' { Verificar-Artefacto }
    }
}
finally {
    Pop-Location
}
