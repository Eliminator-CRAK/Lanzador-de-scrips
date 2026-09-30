# (Autor: Alex Roman)
# Descripcion: Descarga y verifica el instalador oficial offline fijado para compilaciones reproducibles.

[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
$carpeta = Join-Path $raiz 'Recursos\WebView2\Evergreen'
$archivo = Join-Path $carpeta 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
$hash = '771042DB15CB5C463BAC51A8408E70183D7130E8AC946709384C2223DA582C1B'
$url = 'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/06fb6ad8-1976-4e78-9ceb-3ae170edebde/MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
New-Item -ItemType Directory -Path $carpeta -Force | Out-Null
if (-not (Test-Path -LiteralPath $archivo -PathType Leaf)) {
    $temporal = "$archivo.$([Guid]::NewGuid().ToString('N')).partial"
    try {
        Invoke-WebRequest -Uri $url -OutFile $temporal
        if ((Get-FileHash -LiteralPath $temporal -Algorithm SHA256).Hash -ne $hash) {
            throw 'El instalador descargado no coincide con el SHA-256 autorizado.'
        }
        Move-Item -LiteralPath $temporal -Destination $archivo
    }
    finally { if (Test-Path -LiteralPath $temporal) { Remove-Item -LiteralPath $temporal -Force } }
}
if ((Get-FileHash -LiteralPath $archivo -Algorithm SHA256).Hash -ne $hash) {
    throw 'El instalador Evergreen en cache no coincide con el SHA-256 autorizado.'
}
$firma = Get-AuthenticodeSignature -LiteralPath $archivo
if ($firma.Status -ne 'Valid' -or $firma.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
    throw 'El instalador Evergreen no tiene una firma Microsoft valida.'
}
[pscustomobject]@{ Ruta = $archivo; Sha256 = $hash }
