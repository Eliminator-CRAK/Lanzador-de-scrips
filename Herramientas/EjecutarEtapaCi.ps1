# (Autor: Alex Roman)
# Descripcion: Ejecuta las etapas PowerShell del CI de compilacion y publicacion.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        'VerificarDotnet',
        'PrepararPowerShell',
        'VerificarCpp',
        'Publicar',
        'VerificarArtefacto')]
    [string]$Etapa
)

$ErrorActionPreference = 'Stop'
$raizRepositorio = Split-Path -Parent $PSScriptRoot
$huellaFirmaEsperada = '6C654649369000DDE0AA70F62645058D9A3437F5'
$almacenesConfianzaAgregada = [System.Collections.Generic.List[
    System.Security.Cryptography.X509Certificates.StoreName]]::new()

function Verificar-Dotnet {
    # Confirma el SDK compatible fijado por global.json sin modificar el runner.
    $version = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '^10\.0\.2\d{2}$') {
        throw "El runner debe tener un SDK .NET 10.0.2xx compatible con global.json. Version: $version"
    }

    & dotnet --info
    if ($LASTEXITCODE -ne 0) {
        throw 'No se pudo consultar la instalacion de .NET del runner.'
    }
}

function Get-Sha256Archivo {
    param(
        [Parameter(Mandatory)]
        [string]$Ruta
    )

    # Calcula el hash sin depender de modulos opcionales de PowerShell.
    $algoritmo = [System.Security.Cryptography.SHA256]::Create()
    $flujo = [System.IO.File]::OpenRead($Ruta)
    try {
        return [System.BitConverter]::ToString(
            $algoritmo.ComputeHash($flujo)).Replace('-', '')
    }
    finally {
        $flujo.Dispose()
        $algoritmo.Dispose()
    }
}

function Preparar-PowerShell {
    # Instala la version fijada de PowerShell despues de verificar su huella.
    $version = '7.6.0'
    $hashEsperado = '9E725837AF682B87BB212CD1EFE3657C06C540404203810857EC2516AE2CA322'
    $zip = Join-Path $env:RUNNER_TEMP "PowerShell-$version-win-x64.zip"
    $destino = Join-Path $env:RUNNER_TEMP "PowerShell-$version"
    $url = "https://github.com/PowerShell/PowerShell/releases/download/v$version/PowerShell-$version-win-x64.zip"

    Invoke-WebRequest -Uri $url -OutFile $zip
    $hashReal = Get-Sha256Archivo -Ruta $zip
    if ($hashReal -ne $hashEsperado) {
        throw "Hash de PowerShell inesperado: $hashReal"
    }

    $moduloArchive = Join-Path $env:SystemRoot `
        'System32\WindowsPowerShell\v1.0\Modules\Microsoft.PowerShell.Archive\Microsoft.PowerShell.Archive.psd1'
    Import-Module -Name $moduloArchive -Force
    Expand-Archive -LiteralPath $zip -DestinationPath $destino -Force
    & (Join-Path $destino 'pwsh.exe') -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'
    [IO.File]::AppendAllText(
        $env:GITHUB_PATH,
        "$destino`r`n",
        [Text.UTF8Encoding]::new($false))
}

function Verificar-Cpp {
    # Comprueba Professional 2026, C++ e Installer Projects en el runner corporativo.
    & "$PSScriptRoot\PrepararVisualStudioInstalador.ps1"
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
                Write-Host "Aprovisionando CurrentUser\$nombreAlmacen..."
                $almacen = [System.Security.Cryptography.X509Certificates.X509Store]::new(
                    $nombreAlmacen,
                    [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
                try {
                    $almacen.Open(
                        [System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                    $coincidencias = $almacen.Certificates.Find(
                        [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                        $Certificado.Thumbprint,
                        $false)
                    if ($coincidencias.Count -eq 0) {
                        $almacen.Add($certificadoPublico)
                        $almacenesConfianzaAgregada.Add($nombreAlmacen)
                        $coincidencias = $almacen.Certificates.Find(
                            [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                            $Certificado.Thumbprint,
                            $false)
                    }

                    if ($coincidencias.Count -ne 1) {
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
        $certificadoImportado = $false
        try {
            Write-Host 'Cargando certificado de firma del runner...'
            [IO.File]::WriteAllBytes(
                $certPath,
                [Convert]::FromBase64String($env:WINDOWS_SIGNING_CERT_BASE64))
            $securePassword = ConvertTo-SecureString `
                $env:WINDOWS_SIGNING_CERT_PASSWORD `
                -AsPlainText `
                -Force
            $certificadoPfx = Get-PfxCertificate `
                -FilePath $certPath `
                -Password $securePassword
            if (-not $certificadoPfx.Thumbprint.Equals(
                    $huellaFirmaEsperada,
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not $certificadoPfx.HasPrivateKey -or
                $certificadoPfx.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow -or
                $certificadoPfx.EnhancedKeyUsageList.ObjectId -notcontains '1.3.6.1.5.5.7.3.3') {
                throw 'El PFX del runner no coincide con el certificado Authenticode fijado.'
            }

            $certificado = Get-ChildItem -Path Cert:\CurrentUser\My |
                Where-Object {
                    $_.Thumbprint -eq $certificadoPfx.Thumbprint -and
                    $_.HasPrivateKey
                } |
                Select-Object -First 1
            if ($null -eq $certificado) {
                $certificado = Import-PfxCertificate `
                    -FilePath $certPath `
                    -CertStoreLocation Cert:\CurrentUser\My `
                    -Password $securePassword `
                    -Exportable:$false
                $certificadoImportado = $true
            }

            Write-Host 'Aprovisionando confianza del certificado en el runner...'
            Confiar-CertificadoFirmaCi -Certificado $certificado
            Write-Host 'Certificado de firma preparado correctamente.'
            & "$PSScriptRoot\PublicarPortable.ps1" `
                -CertThumbprint $certificado.Thumbprint
            & "$PSScriptRoot\PublicarServidor.ps1" `
                -CertThumbprint $certificado.Thumbprint
        }
        finally {
            if ($certificadoImportado) {
                $almacenPrivado = [System.Security.Cryptography.X509Certificates.X509Store]::new(
                    [System.Security.Cryptography.X509Certificates.StoreName]::My,
                    [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
                try {
                    $almacenPrivado.Open(
                        [System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                    foreach ($certificadoGuardado in $almacenPrivado.Certificates.Find(
                            [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                            $huellaFirmaEsperada,
                            $false)) {
                        $almacenPrivado.Remove($certificadoGuardado)
                    }
                }
                finally {
                    $almacenPrivado.Close()
                }
            }

            Remove-ConfianzaCertificadoFirmaCi
            Remove-Item -LiteralPath $certPath -Force -ErrorAction SilentlyContinue
        }
    }
    else {
        & "$PSScriptRoot\PublicarPortable.ps1" `
            -CertThumbprint '' `
            -AllowUnsignedForDev
        & "$PSScriptRoot\PublicarServidor.ps1" `
            -CertThumbprint $huellaFirmaEsperada `
            -AllowUnsignedForDev
    }
}

function Remove-ConfianzaCertificadoFirmaCi {
    # Retira solo las anclas agregadas por esta ejecucion del runner.
    foreach ($nombreAlmacen in $almacenesConfianzaAgregada) {
        $almacen = [System.Security.Cryptography.X509Certificates.X509Store]::new(
            $nombreAlmacen,
            [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
        try {
            $almacen.Open(
                [System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            foreach ($certificado in $almacen.Certificates.Find(
                    [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                    $huellaFirmaEsperada,
                    $false)) {
                $almacen.Remove($certificado)
            }
        }
        finally {
            $almacen.Close()
        }
    }

    $almacenesConfianzaAgregada.Clear()
}

function Verificar-Artefacto {
    # Comprueba los paquetes cliente y servidor y sus firmas.
    $carpeta = Join-Path $raizRepositorio 'publicacion'
    $archivosEsperados = @(
        (Join-Path $carpeta 'LanzadorScripts-1.8.2-x64.msi'),
        (Join-Path $carpeta 'LanzadorScripts_Portable-1.8.2-x64.exe')
    )
    foreach ($archivo in $archivosEsperados) {
        if (-not (Test-Path -LiteralPath $archivo)) {
            throw "No se genero $(Split-Path -Leaf $archivo)."
        }
    }

    $archivos = @(Get-ChildItem -LiteralPath $carpeta -Recurse -File)
    if ($archivos.Count -ne 2 -or
        @($archivos | Where-Object { $_.FullName -notin $archivosEsperados }).Count -gt 0) {
        throw "La publicacion debe generar exactamente el MSI y el portable esperados. Archivos: $($archivos.Count)"
    }

    foreach ($archivo in $archivosEsperados) {
        $firma = Get-AuthenticodeSignature -LiteralPath $archivo
        if ($env:RELEASE_BUILD -eq 'true' -and
            ($firma.Status -ne 'Valid' -or $null -eq $firma.TimeStamperCertificate)) {
            throw "La firma de $(Split-Path -Leaf $archivo) no es valida: $($firma.Status)."
        }

        Get-FileHash -LiteralPath $archivo -Algorithm SHA256 | Format-List
    }

    $zipServidor = Join-Path $raizRepositorio `
        'publicacion-servidor\LanzadorScripts_Servidor-1.8.2-x64.zip'
    if (-not (Test-Path -LiteralPath $zipServidor -PathType Leaf)) {
        throw 'No se genero el paquete ZIP del servidor.'
    }

    Add-Type -AssemblyName System.IO.Compression
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipServidor)
    $temporal = Join-Path $env:RUNNER_TEMP "LanzadorScriptsServidor-$PID"
    try {
        $esperadas = [System.Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        foreach ($nombre in @(
                'Servicio/LanzadorScripts.Servidor.Servicio.exe',
                'LanzadorScripts.Servidor.exe',
                'Instalar-Servidor.ps1',
                'Desinstalar-Servidor.ps1',
                'Crear-ConfiguracionCliente.ps1',
                'LEEME-Servidor.txt',
                'SHA256SUMS.txt')) {
            [void]$esperadas.Add($nombre)
        }
        if ($env:RELEASE_BUILD -eq 'true') {
            [void]$esperadas.Add('LanzadorScripts-CodeSigning-Public.cer')
        }

        foreach ($entrada in $zip.Entries) {
            $nombre = $entrada.FullName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($nombre) -or
                [System.IO.Path]::IsPathRooted($nombre) -or
                $nombre.Split('/') -contains '..' -or
                -not $esperadas.Remove($nombre)) {
                throw "El ZIP servidor contiene una entrada inesperada: $nombre"
            }
        }
        if ($esperadas.Count -ne 0) {
            throw "Faltan archivos en el ZIP servidor: $([string]::Join(', ', $esperadas))"
        }

        [System.IO.Directory]::CreateDirectory($temporal) | Out-Null
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zipServidor, $temporal, $false)
        $lineas = Get-Content -LiteralPath (Join-Path $temporal 'SHA256SUMS.txt')
        $archivosPaquete = @(Get-ChildItem -LiteralPath $temporal -Recurse -File |
            Where-Object { $_.Name -ne 'SHA256SUMS.txt' })
        if ($lineas.Count -ne $archivosPaquete.Count) {
            throw 'SHA256SUMS.txt no cubre todos los archivos del paquete servidor.'
        }
        foreach ($archivo in $archivosPaquete) {
            $relativa = [System.IO.Path]::GetRelativePath($temporal, $archivo.FullName).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $archivo.FullName -Algorithm SHA256).Hash
            if ($lineas -notcontains "$hash  $relativa") {
                throw "El hash interno no coincide para $relativa."
            }
        }

        if ($env:RELEASE_BUILD -eq 'true') {
            foreach ($relativa in @(
                    'Servicio\LanzadorScripts.Servidor.Servicio.exe',
                    'LanzadorScripts.Servidor.exe',
                    'Instalar-Servidor.ps1',
                    'Desinstalar-Servidor.ps1',
                    'Crear-ConfiguracionCliente.ps1')) {
                $firma = Get-AuthenticodeSignature -LiteralPath (Join-Path $temporal $relativa)
                if ($firma.Status -ne 'Valid' -or $null -eq $firma.TimeStamperCertificate) {
                    throw "La firma interna de $relativa no es valida: $($firma.Status)."
                }
            }
        }
    }
    finally {
        $zip.Dispose()
        if ([System.IO.Directory]::Exists($temporal)) {
            Remove-Item -LiteralPath $temporal -Recurse -Force
        }
    }

    Get-FileHash -LiteralPath $zipServidor -Algorithm SHA256 | Format-List
}

Push-Location $raizRepositorio
try {
    # Ejecuta solamente la etapa solicitada por el workflow.
    switch ($Etapa) {
        'VerificarDotnet' { Verificar-Dotnet }
        'PrepararPowerShell' { Preparar-PowerShell }
        'VerificarCpp' { Verificar-Cpp }
        'Publicar' { Publicar-Aplicacion }
        'VerificarArtefacto' { Verificar-Artefacto }
    }
}
finally {
    Pop-Location
}
