# (Autor: Alex Roman)
# Descripcion: Instala y activa LanzadorScripts Servidor para todos los usuarios.

[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)]
    [int]$Puerto = 47831,

    [ValidateNotNullOrEmpty()]
    [string]$RutaScripts = 'R:\SCRIPS',

    [switch]$NoAbrir,

    [switch]$PermitirPaqueteDesarrolloSinFirma
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$nombreServicio = 'LanzadorScriptsServidor'
$raizPaquete = [System.IO.Path]::GetFullPath($PSScriptRoot)
$destino = [System.IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'LanzadorScriptsServidor'))
$datos = [System.IO.Path]::GetFullPath((Join-Path $env:ProgramData 'LanzadorScriptsServidor'))
$actualizaciones = [System.IO.Path]::GetFullPath((Join-Path $datos 'Actualizaciones'))
$nombreRecursoActualizaciones = 'LanzadorScriptsActualizaciones$'
$servicioOrigen = Join-Path $raizPaquete 'Servicio\LanzadorScripts.Servidor.Servicio.exe'
$administracionOrigen = Join-Path $raizPaquete 'LanzadorScripts.Servidor.exe'
$servicioDestino = Join-Path $destino 'Servicio\LanzadorScripts.Servidor.Servicio.exe'
$administracionDestino = Join-Path $destino 'LanzadorScripts.Servidor.exe'
$huellaFirmaEsperada = '6C654649369000DDE0AA70F62645058D9A3437F5'

function Assert-Administrador {
    $identidad = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identidad)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Ejecute este instalador como administrador.'
    }
}

function Assert-SinReparse {
    param([Parameter(Mandatory)][string]$Ruta)

    if (([System.IO.File]::GetAttributes($Ruta) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "No se admiten enlaces ni puntos de reanalisis: $Ruta"
    }
}

function Invoke-Nativo {
    param(
        [Parameter(Mandatory)][string]$Ejecutable,
        [Parameter(Mandatory)][string[]]$Argumentos,
        [switch]$IgnorarError
    )

    $inicio = [Diagnostics.ProcessStartInfo]::new()
    $inicio.FileName = $Ejecutable
    $inicio.UseShellExecute = $false
    $inicio.CreateNoWindow = $true
    $inicio.RedirectStandardOutput = $true
    $inicio.RedirectStandardError = $true
    foreach ($argumento in $Argumentos) {
        [void]$inicio.ArgumentList.Add($argumento)
    }

    $proceso = [Diagnostics.Process]::Start($inicio)
    if ($null -eq $proceso) {
        throw "No se pudo iniciar $Ejecutable."
    }

    try {
        $salidaPendiente = $proceso.StandardOutput.ReadToEndAsync()
        $errorPendiente = $proceso.StandardError.ReadToEndAsync()
        if (-not $proceso.WaitForExit(30000)) {
            $proceso.Kill($true)
            $proceso.WaitForExit()
            throw "La herramienta $Ejecutable supero el tiempo permitido."
        }

        $salida = $salidaPendiente.GetAwaiter().GetResult()
        $errorProceso = $errorPendiente.GetAwaiter().GetResult()

        if (-not $IgnorarError -and $proceso.ExitCode -ne 0) {
            throw "La herramienta $Ejecutable termino con codigo $($proceso.ExitCode). $errorProceso $salida".Trim()
        }
    }
    finally {
        $proceso.Dispose()
    }
}

function Copy-ArchivoSeguro {
    param(
        [Parameter(Mandatory)][string]$Origen,
        [Parameter(Mandatory)][string]$Destino
    )

    if (-not [System.IO.File]::Exists($Origen)) {
        throw "Falta un archivo del paquete servidor: $Origen"
    }

    Assert-SinReparse -Ruta $Origen
    $carpeta = [System.IO.Path]::GetDirectoryName($Destino)
    [System.IO.Directory]::CreateDirectory($carpeta) | Out-Null
    Assert-SinReparse -Ruta $carpeta
    $temporal = "$Destino.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [System.IO.File]::Copy($Origen, $temporal, $false)
        [System.IO.File]::Move($temporal, $Destino, $true)
    }
    finally {
        if ([System.IO.File]::Exists($temporal)) {
            [System.IO.File]::Delete($temporal)
        }
    }
}

function Assert-IntegridadPaquete {
    $rutaHashes = Join-Path $raizPaquete 'SHA256SUMS.txt'
    if (-not [System.IO.File]::Exists($rutaHashes)) {
        if ($PermitirPaqueteDesarrolloSinFirma) {
            return
        }

        throw 'El paquete no contiene SHA256SUMS.txt.'
    }

    $hashes = [System.Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($linea in [System.IO.File]::ReadAllLines($rutaHashes)) {
        if ($linea -notmatch '^([0-9A-F]{64})  ([^\\].*)$') {
            throw 'SHA256SUMS.txt contiene una linea no valida.'
        }

        $relativa = $Matches[2].Replace('/', '\')
        if ([System.IO.Path]::IsPathRooted($relativa) -or
            $relativa.Split('\') -contains '..' -or
            -not $hashes.TryAdd($relativa, $Matches[1])) {
            throw 'SHA256SUMS.txt contiene una ruta insegura o duplicada.'
        }
    }

    $archivos = @(Get-ChildItem -LiteralPath $raizPaquete -Recurse -File |
        Where-Object { $_.FullName -ne $rutaHashes })
    if ($archivos.Count -ne $hashes.Count) {
        throw 'El contenido extraido no coincide con SHA256SUMS.txt.'
    }

    foreach ($archivo in $archivos) {
        Assert-SinReparse -Ruta $archivo.FullName
        $relativa = [System.IO.Path]::GetRelativePath(
            $raizPaquete,
            $archivo.FullName)
        $hashEsperado = ''
        if (-not $hashes.TryGetValue($relativa, [ref]$hashEsperado)) {
            throw "SHA256SUMS.txt no autoriza $relativa."
        }

        $hashReal = (Get-FileHash -LiteralPath $archivo.FullName -Algorithm SHA256).Hash
        if (-not $hashReal.Equals($hashEsperado, [StringComparison]::OrdinalIgnoreCase)) {
            throw "La integridad SHA-256 no coincide para $relativa."
        }
    }

    foreach ($rutaFirmada in @(
            $servicioOrigen,
            $administracionOrigen,
            (Join-Path $raizPaquete 'Instalar-Servidor.ps1'),
            (Join-Path $raizPaquete 'Desinstalar-Servidor.ps1'),
            (Join-Path $raizPaquete 'Crear-ConfiguracionCliente.ps1'))) {
        $firma = Get-AuthenticodeSignature -LiteralPath $rutaFirmada
        $huella = if ($null -eq $firma.SignerCertificate) {
            ''
        }
        else {
            $firma.SignerCertificate.Thumbprint
        }
        if ($firma.Status -notin @('Valid', 'NotTrusted') -or
            -not [string]::Equals(
                $huella,
                $huellaFirmaEsperada,
                [StringComparison]::OrdinalIgnoreCase)) {
            if ($PermitirPaqueteDesarrolloSinFirma -and $firma.Status -eq 'NotSigned') {
                continue
            }

            throw "La firma Authenticode de $([System.IO.Path]::GetFileName($rutaFirmada)) no es valida."
        }
    }
}

Assert-Administrador
Assert-IntegridadPaquete
if ($RutaScripts.Contains('..', [StringComparison]::Ordinal) -or
    -not [System.IO.Path]::IsPathFullyQualified($RutaScripts)) {
    throw 'RutaScripts debe ser una ruta local absoluta sin segmentos de retroceso.'
}

foreach ($archivo in @($servicioOrigen, $administracionOrigen)) {
    if (-not [System.IO.File]::Exists($archivo)) {
        throw "El paquete servidor esta incompleto: $archivo"
    }
    Assert-SinReparse -Ruta $archivo
}

$procesosInstalados = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    try {
        $_.Id -ne $PID -and
        -not [string]::IsNullOrWhiteSpace($_.Path) -and
        $_.Path.StartsWith($destino + '\', [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        $false
    }
})
if ($procesosInstalados.Count -gt 0) {
    throw 'Cierre la consola LanzadorScripts Servidor instalada antes de actualizar.'
}

$servicioActual = Get-Service -Name $nombreServicio -ErrorAction SilentlyContinue
if ($null -ne $servicioActual -and $servicioActual.Status -ne 'Stopped') {
    Stop-Service -Name $nombreServicio -Force
    $servicioActual.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

[System.IO.Directory]::CreateDirectory($destino) | Out-Null
Assert-SinReparse -Ruta $destino
Copy-ArchivoSeguro -Origen $servicioOrigen -Destino $servicioDestino
Copy-ArchivoSeguro -Origen $administracionOrigen -Destino $administracionDestino
foreach ($nombre in @('Desinstalar-Servidor.ps1', 'Crear-ConfiguracionCliente.ps1', 'LEEME-Servidor.txt')) {
    $origen = Join-Path $raizPaquete $nombre
    if ([System.IO.File]::Exists($origen)) {
        Copy-ArchivoSeguro -Origen $origen -Destino (Join-Path $destino $nombre)
    }
}

[System.IO.Directory]::CreateDirectory($datos) | Out-Null
Assert-SinReparse -Ruta $datos
$icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
Invoke-Nativo -Ejecutable $icacls -Argumentos @(
    $datos,
    '/inheritance:r',
    '/grant:r',
    '*S-1-5-18:(OI)(CI)F',
    '*S-1-5-32-544:(OI)(CI)F')
[System.IO.Directory]::CreateDirectory($actualizaciones) | Out-Null
Assert-SinReparse -Ruta $actualizaciones
Invoke-Nativo -Ejecutable $icacls -Argumentos @(
    $actualizaciones,
    '/inheritance:r',
    '/grant:r',
    '*S-1-5-18:(OI)(CI)F',
    '*S-1-5-32-544:(OI)(CI)F',
    '*S-1-5-11:(OI)(CI)RX')
$usuariosAutenticados = ([Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::AuthenticatedUserSid,
        $null)).Translate([Security.Principal.NTAccount]).Value
$administradoresLocales = ([Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
        $null)).Translate([Security.Principal.NTAccount]).Value
$sistemaLocal = ([Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::LocalSystemSid,
        $null)).Translate([Security.Principal.NTAccount]).Value
$net = Join-Path $env:SystemRoot 'System32\net.exe'
Invoke-Nativo -Ejecutable $net -Argumentos @(
    'share', $nombreRecursoActualizaciones, '/delete', '/y') -IgnorarError
Invoke-Nativo -Ejecutable $net -Argumentos @(
    'share',
    "$nombreRecursoActualizaciones=$actualizaciones",
    "/GRANT:$usuariosAutenticados,READ",
    "/GRANT:$administradoresLocales,FULL",
    "/GRANT:$sistemaLocal,FULL",
    '/CACHE:None')
$rutaConfiguracion = Join-Path $datos 'configuracion-servidor.json'
if (-not [System.IO.File]::Exists($rutaConfiguracion)) {
    $configuracion = [ordered]@{
        version = 1
        puerto = $Puerto
        maximoConexiones = 64
        diasRetencionAuditoria = 3650
        rutaScripts = [System.IO.Path]::GetFullPath($RutaScripts)
    } | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText(
        $rutaConfiguracion,
        $configuracion,
        [System.Text.UTF8Encoding]::new($false))
}

Assert-SinReparse -Ruta $rutaConfiguracion
$bytesConfiguracion = [System.IO.File]::ReadAllBytes($rutaConfiguracion)
if ($bytesConfiguracion.Length -le 0 -or $bytesConfiguracion.Length -gt 128KB) {
    throw 'La configuracion existente del servidor tiene un tamano no valido.'
}

$documentoConfiguracion = $null
$puertoEfectivo = 0
try {
    $textoConfiguracion = [System.Text.UTF8Encoding]::new($false, $true).GetString(
        $bytesConfiguracion)
    $documentoConfiguracion = [System.Text.Json.JsonDocument]::Parse(
        [string]$textoConfiguracion)
    $puertoJson = [System.Text.Json.JsonElement]::new()
    if ($documentoConfiguracion.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object -or
        -not $documentoConfiguracion.RootElement.TryGetProperty('puerto', [ref]$puertoJson) -or
        $puertoJson.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        -not $puertoJson.TryGetInt32([ref]$puertoEfectivo) -or
        $puertoEfectivo -lt 1024 -or $puertoEfectivo -gt 65535) {
        throw 'La configuracion existente no contiene un puerto valido.'
    }
}
finally {
    if ($null -ne $documentoConfiguracion) {
        $documentoConfiguracion.Dispose()
    }
}
if ($PSBoundParameters.ContainsKey('Puerto') -and $Puerto -ne $puertoEfectivo) {
    Write-Warning "Se conserva el puerto $puertoEfectivo de la configuracion existente. Cambielo desde configuracion-servidor.json con el servicio detenido."
}

$sc = Join-Path $env:SystemRoot 'System32\sc.exe'
if ($null -eq $servicioActual) {
    Invoke-Nativo -Ejecutable $sc -Argumentos @(
        'create', $nombreServicio,
        'binPath=', "`"$servicioDestino`"",
        'start=', 'delayed-auto',
        'obj=', 'LocalSystem',
        'DisplayName=', 'LanzadorScripts Servidor')
}
else {
    Invoke-Nativo -Ejecutable $sc -Argumentos @(
        'config', $nombreServicio,
        'binPath=', "`"$servicioDestino`"",
        'start=', 'delayed-auto',
        'obj=', 'LocalSystem',
        'DisplayName=', 'LanzadorScripts Servidor')
}

Invoke-Nativo -Ejecutable $sc -Argumentos @('description', $nombreServicio, 'Servicio central cifrado de permisos, catalogo y auditoria.')
Invoke-Nativo -Ejecutable $sc -Argumentos @('failure', $nombreServicio, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000')
Invoke-Nativo -Ejecutable $sc -Argumentos @('failureflag', $nombreServicio, '1')
Invoke-Nativo -Ejecutable $sc -Argumentos @('sidtype', $nombreServicio, 'unrestricted')

$cuentaAdministradora = [Security.Principal.WindowsIdentity]::GetCurrent().Name
Invoke-Nativo -Ejecutable $servicioDestino -Argumentos @(
    '--preparar-administrador-inicial',
    $cuentaAdministradora)

$netsh = Join-Path $env:SystemRoot 'System32\netsh.exe'
Invoke-Nativo -Ejecutable $netsh -Argumentos @(
    'advfirewall', 'firewall', 'delete', 'rule', 'name=LanzadorScripts Servidor') -IgnorarError
Invoke-Nativo -Ejecutable $netsh -Argumentos @(
    'advfirewall', 'firewall', 'add', 'rule',
    'name=LanzadorScripts Servidor', 'dir=in', 'action=allow', 'profile=domain',
    'protocol=TCP', "localport=$puertoEfectivo", "program=$servicioDestino", 'enable=yes')

$carpetaMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\LanzadorScripts'
[System.IO.Directory]::CreateDirectory($carpetaMenu) | Out-Null
$shell = New-Object -ComObject WScript.Shell
$acceso = $null
try {
    $acceso = $shell.CreateShortcut((Join-Path $carpetaMenu 'LanzadorScripts Servidor.lnk'))
    $acceso.TargetPath = $administracionDestino
    $acceso.WorkingDirectory = $destino
    $acceso.Description = 'Administracion de LanzadorScripts Servidor'
    $acceso.Save()
}
finally {
    if ($null -ne $acceso) {
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($acceso) | Out-Null
    }
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
}

Start-Service -Name $nombreServicio
(Get-Service -Name $nombreServicio).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host "LanzadorScripts Servidor instalado y activo en el puerto $puertoEfectivo."
Write-Host "Datos protegidos: $datos"
Write-Host "Actualizaciones: \\$env:COMPUTERNAME\$nombreRecursoActualizaciones"
if (-not $NoAbrir) {
    Start-Process -FilePath $administracionDestino
}
