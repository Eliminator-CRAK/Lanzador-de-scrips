# (Autor: Alex Roman)
# Descripcion: Publica el MSI instalado y el EXE portable del cliente.

param(
    [string]$CertThumbprint = '6C654649369000DDE0AA70F62645058D9A3437F5',
    [string]$CertPath = '',
    [securestring]$CertPassword,
    [string]$TimestampServer = 'http://timestamp.digicert.com',
    [string]$RutaRuntimeWebView2Portable = '',
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
$salidaRuntimeStaging = Join-Path $raiz 'obj\PublicacionRuntime'
$salidaLanzadorNativo = Join-Path $raiz 'obj\LanzadorNativoBuild'
$salidaAnterior = Join-Path $raiz "obj\PublicacionAnterior-$PID"
$tamanoMinimoExe = 209715200
$tamanoMinimoMsi = 209715200
$scriptCompilarMsi = Join-Path $PSScriptRoot 'CompilarMsi.ps1'
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
$raizCompleta = [System.IO.Path]::GetFullPath($raiz).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$salidaCompleta = [System.IO.Path]::GetFullPath($salida)
$stagingCompleta = [System.IO.Path]::GetFullPath($salidaStaging)
$runtimeStagingCompleta = [System.IO.Path]::GetFullPath($salidaRuntimeStaging)
$lanzadorNativoCompleta = [System.IO.Path]::GetFullPath($salidaLanzadorNativo)
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
    -not $runtimeStagingCompleta.StartsWith($carpetaObjCompleta, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not $lanzadorNativoCompleta.StartsWith($carpetaObjCompleta, [System.StringComparison]::OrdinalIgnoreCase) -or
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
if ($versionProductoEsperada -ne '1.8.4' -or $versionArchivoEsperada -ne '1.8.4.0') {
    throw 'La publicacion MSI y portable requiere la version 1.8.4.'
}

$nombreMsiFinal = 'LanzadorScripts-1.8.4-x64.msi'
$nombrePortableFinal = 'LanzadorScripts_Portable-1.8.4-x64.exe'
$msiCompilado = Join-Path $raiz "Instalador\Release\$nombreMsiFinal"
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

        if ($null -eq $cert -or
            -not $cert.HasPrivateKey -or
            $cert.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow -or
            $cert.EnhancedKeyUsageList.ObjectId -notcontains '1.3.6.1.5.5.7.3.3') {
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

function Get-VisualStudioDeveloperCommand {
    # Localiza las herramientas nativas x64 de Visual Studio Professional 2026.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'No se encontro vswhere.exe para localizar las herramientas nativas de Visual Studio.'
    }

    $instalacion = (& $vswhere `
        -products Microsoft.VisualStudio.Product.Professional `
        -version '[18.0,19.0)' `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($instalacion)) {
        throw 'No se encontraron las herramientas C++ x64 de Visual Studio Professional 2026.'
    }

    $comando = Join-Path $instalacion 'Common7\Tools\VsDevCmd.bat'
    if (-not (Test-Path -LiteralPath $comando -PathType Leaf)) {
        throw "No se encontro VsDevCmd.bat en $instalacion."
    }

    return $comando
}

function ConvertTo-RcLiteral {
    param(
        [string]$Valor
    )

    return $Valor.Replace('\', '\\').Replace('"', '\"')
}

function Initialize-NativeResourceReader {
    if ('LanzadorScripts.Publicacion.RecursosNativos' -as [type]) {
        return
    }

    # Lee recursos PE sin ejecutar el archivo publicado.
    Add-Type -TypeDefinition @'
// (Autor: Alex Roman)
// Descripcion: Lee recursos del lanzador nativo durante la publicacion.

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LanzadorScripts.Publicacion
{
    public static class RecursosNativos
    {
        private const uint LoadLibraryAsDataFile = 0x00000002;
        private const uint LoadLibraryAsImageResource = 0x00000020;
        private static readonly IntPtr TipoRcData = new IntPtr(10);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LockResource(IntPtr resourceData);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);

        public static long ObtenerTamano(string fileName, int resourceId)
        {
            IntPtr module = Abrir(fileName);
            try
            {
                IntPtr resource = Buscar(module, resourceId);
                return SizeofResource(module, resource);
            }
            finally
            {
                FreeLibrary(module);
            }
        }

        public static string LeerAscii(string fileName, int resourceId)
        {
            IntPtr module = Abrir(fileName);
            try
            {
                IntPtr resource = Buscar(module, resourceId);
                uint size = SizeofResource(module, resource);
                IntPtr loaded = LoadResource(module, resource);
                IntPtr data = loaded == IntPtr.Zero ? IntPtr.Zero : LockResource(loaded);
                if (data == IntPtr.Zero || size == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                int length = checked((int)size);
                byte[] bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                return Encoding.ASCII.GetString(bytes);
            }
            finally
            {
                FreeLibrary(module);
            }
        }

        public static string ObtenerSha256(string fileName, int resourceId)
        {
            IntPtr module = Abrir(fileName);
            try
            {
                IntPtr resource = Buscar(module, resourceId);
                uint size = SizeofResource(module, resource);
                IntPtr loaded = LoadResource(module, resource);
                IntPtr data = loaded == IntPtr.Zero ? IntPtr.Zero : LockResource(loaded);
                if (data == IntPtr.Zero || size == 0 || size > int.MaxValue)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                byte[] buffer = new byte[1024 * 1024];
                IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                try
                {
                    int offset = 0;
                    int remaining = checked((int)size);
                    while (remaining > 0)
                    {
                        int count = Math.Min(buffer.Length, remaining);
                        Marshal.Copy(IntPtr.Add(data, offset), buffer, 0, count);
                        hash.AppendData(buffer, 0, count);
                        offset += count;
                        remaining -= count;
                    }

                    return Convert.ToHexString(hash.GetHashAndReset());
                }
                finally
                {
                    hash.Dispose();
                }
            }
            finally
            {
                FreeLibrary(module);
            }
        }

        private static IntPtr Abrir(string fileName)
        {
            IntPtr module = LoadLibraryEx(
                fileName,
                IntPtr.Zero,
                LoadLibraryAsDataFile | LoadLibraryAsImageResource);
            if (module == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return module;
        }

        private static IntPtr Buscar(IntPtr module, int resourceId)
        {
            IntPtr resource = FindResource(module, new IntPtr(resourceId), TipoRcData);
            if (resource == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return resource;
        }
    }
}
'@
}

function New-NativeLauncher {
    param(
        [string]$RutaPayload,
        [string]$HashPayload,
        [string]$RutaRuntimeWebView2,
        [string]$HashRuntimeWebView2,
        [string]$RutaSalida
    )

    # Genera el lanzador que prepara las rutas antes de iniciar .NET.
    $carpetaFuentes = Join-Path $raiz 'LanzadorNativo'
    $fuente = Join-Path $carpetaFuentes 'LanzadorNativo.cpp'
    $plantillaRecursos = Join-Path $carpetaFuentes 'LanzadorNativo.rc.in'
    $cabeceraRecursos = Join-Path $carpetaFuentes 'RecursosLanzador.h'
    $icono = Join-Path $raiz 'Recursos\IconoLanzador.ico'
    foreach ($archivo in @($fuente, $plantillaRecursos, $cabeceraRecursos, $icono, (Join-Path $raiz 'manifiesto.manifest'))) {
        if (-not (Test-Path -LiteralPath $archivo -PathType Leaf)) {
            throw "No se encontro un archivo del lanzador nativo: $archivo"
        }
    }

    if (Test-Path -LiteralPath $lanzadorNativoCompleta) {
        Remove-Item -LiteralPath $lanzadorNativoCompleta -Recurse -Force
    }

    New-Item -ItemType Directory -Path $lanzadorNativoCompleta | Out-Null
    $archivoHash = Join-Path $lanzadorNativoCompleta 'payload.sha256'
    [System.IO.File]::WriteAllText(
        $archivoHash,
        $HashPayload,
        [System.Text.Encoding]::ASCII)
    $archivoHashWebView2 = Join-Path $lanzadorNativoCompleta 'webview2.sha256'
    [System.IO.File]::WriteAllText(
        $archivoHashWebView2,
        $HashRuntimeWebView2,
        [System.Text.Encoding]::ASCII)

    $partesVersion = $versionArchivoEsperada.Split('.')
    if ($partesVersion.Count -ne 4 -or
        @($partesVersion | Where-Object { $_ -notmatch '^[0-9]+$' }).Count -gt 0) {
        throw "La version de archivo no es valida para VERSIONINFO: $versionArchivoEsperada"
    }

    $nombreArchivo = Split-Path -Leaf $RutaSalida
    $nombreInterno = [System.IO.Path]::GetFileNameWithoutExtension($nombreArchivo)
    $descripcionArchivo = 'Lanzador de Scripts Portable'
    $producto = "$versionProductoEsperada+$revisionGitEsperada.portable"
    $contenidoRecursos = Get-Content -LiteralPath $plantillaRecursos -Raw
    $contenidoRecursos = $contenidoRecursos.Replace(
        '__RUTA_MANIFIESTO__',
        (ConvertTo-RcLiteral (Join-Path $raiz 'manifiesto.manifest')))
    $contenidoRecursos = $contenidoRecursos.Replace(
        '__RUTA_ICONO__',
        (ConvertTo-RcLiteral $icono))
    $contenidoRecursos = $contenidoRecursos.Replace(
        '__RUTA_PAYLOAD__',
        (ConvertTo-RcLiteral $RutaPayload))
    $contenidoRecursos = $contenidoRecursos.Replace(
        '__RUTA_HASH_PAYLOAD__',
        (ConvertTo-RcLiteral $archivoHash))
    $contenidoRecursos = $contenidoRecursos.Replace(
        '__RUTA_WEBVIEW2_RUNTIME__',
        (ConvertTo-RcLiteral $RutaRuntimeWebView2))
    $contenidoRecursos = $contenidoRecursos.Replace(
        '__RUTA_HASH_WEBVIEW2_RUNTIME__',
        (ConvertTo-RcLiteral $archivoHashWebView2))
    $contenidoRecursos = $contenidoRecursos.Replace(
        '__VERSION_ARCHIVO_COMAS__',
        ($partesVersion -join ','))
    $contenidoRecursos = $contenidoRecursos.Replace(
        '__VERSION_ARCHIVO__',
        $versionArchivoEsperada)
    $contenidoRecursos = $contenidoRecursos.Replace(
        '__VERSION_PRODUCTO__',
        $producto)
    $contenidoRecursos = $contenidoRecursos.Replace(
        '__DESCRIPCION_ARCHIVO__',
        $descripcionArchivo)
    $contenidoRecursos = $contenidoRecursos.Replace(
        '__NOMBRE_INTERNO__',
        $nombreInterno)
    $contenidoRecursos = $contenidoRecursos.Replace(
        '__NOMBRE_ARCHIVO__',
        $nombreArchivo)
    $archivoRecursos = Join-Path $lanzadorNativoCompleta 'LanzadorNativo.rc'
    [System.IO.File]::WriteAllText(
        $archivoRecursos,
        $contenidoRecursos,
        [System.Text.Encoding]::Unicode)

    $recursoCompilado = Join-Path $lanzadorNativoCompleta 'LanzadorNativo.res'
    $objetoCompilado = Join-Path $lanzadorNativoCompleta 'LanzadorNativo.obj'
    $comandoCompilacion = Join-Path $lanzadorNativoCompleta 'CompilarLanzador.cmd'
    $vsDevCmd = Get-VisualStudioDeveloperCommand
    $lineaCompilacion = @(
        'cl.exe /nologo /std:c++20 /O2 /MT /EHsc /W4 /WX /utf-8 /permissive- /sdl /guard:cf',
        '/DUNICODE /D_UNICODE',
        "/Fo:`"$objetoCompilado`"",
        "/Fe:`"$RutaSalida`"",
        "`"$fuente`"",
        "`"$recursoCompilado`"",
        '/link /WX /SUBSYSTEM:WINDOWS /MACHINE:X64',
        '/DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /GUARD:CF /CETCOMPAT',
        '/INCREMENTAL:NO /MANIFEST:NO /Brepro'
    ) -join ' '
    $lineas = @(
        '@echo off',
        "call `"$vsDevCmd`" -no_logo -arch=x64 -host_arch=x64",
        'if errorlevel 1 exit /b %errorlevel%',
        "rc.exe /nologo /I `"$carpetaFuentes`" /fo `"$recursoCompilado`" `"$archivoRecursos`"",
        'if errorlevel 1 exit /b %errorlevel%',
        $lineaCompilacion,
        'exit /b %errorlevel%'
    )
    [System.IO.File]::WriteAllLines(
        $comandoCompilacion,
        $lineas,
        [System.Text.Encoding]::ASCII)

    Invoke-NativeChecked -Descripcion 'compilacion del lanzador nativo' -Comando {
        & $env:ComSpec /d /c $comandoCompilacion
    }

    if (-not (Test-Path -LiteralPath $RutaSalida -PathType Leaf)) {
        throw "La compilacion nativa no genero $nombreArchivo."
    }

    $pruebaLimpieza = Start-Process `
        -FilePath $RutaSalida `
        -ArgumentList '--validar-limpieza-ruta-larga' `
        -Wait `
        -PassThru `
        -WindowStyle Hidden
    if ($pruebaLimpieza.ExitCode -ne 0) {
        throw 'El lanzador nativo no pudo eliminar una sesion con rutas superiores a MAX_PATH.'
    }

    $arquitectura = Get-PortableExecutableMachine -Ruta $RutaSalida
    if ($arquitectura -ne $arquitecturaPeX64) {
        throw ('El lanzador nativo no es x64. Arquitectura PE: 0x{0:X4}.' -f $arquitectura)
    }
}

function Assert-NativeLauncherPayload {
    param(
        [string]$RutaLanzador,
        [string]$RutaPayload,
        [string]$HashPayload,
        [string]$RutaRuntimeWebView2,
        [string]$HashRuntimeWebView2
    )

    # Comprueba que el EXE exterior contiene el runtime firmado esperado.
    Initialize-NativeResourceReader
    $tamanoRecurso = [LanzadorScripts.Publicacion.RecursosNativos]::ObtenerTamano(
        $RutaLanzador,
        101)
    $tamanoPayload = (Get-Item -LiteralPath $RutaPayload).Length
    if ($tamanoRecurso -ne $tamanoPayload) {
        throw "El recurso .NET embebido tiene un tamano inesperado: $tamanoRecurso."
    }

    $hashRecurso = [LanzadorScripts.Publicacion.RecursosNativos]::LeerAscii(
        $RutaLanzador,
        102).Trim()
    if (-not $hashRecurso.Equals($HashPayload, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "El lanzador nativo contiene un hash .NET inesperado: $hashRecurso."
    }

    $hashPayloadEmbebido = [LanzadorScripts.Publicacion.RecursosNativos]::ObtenerSha256(
        $RutaLanzador,
        101)
    if (-not $hashPayloadEmbebido.Equals($HashPayload, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "El recurso .NET embebido esta corrupto. Esperado: $HashPayload. Detectado: $hashPayloadEmbebido."
    }

    $tamanoWebView2 = [LanzadorScripts.Publicacion.RecursosNativos]::ObtenerTamano(
        $RutaLanzador,
        103)
    if ($tamanoWebView2 -ne (Get-Item -LiteralPath $RutaRuntimeWebView2).Length) {
        throw "El recurso WebView2 embebido tiene un tamano inesperado: $tamanoWebView2."
    }

    $hashWebView2Publicado = [LanzadorScripts.Publicacion.RecursosNativos]::LeerAscii(
        $RutaLanzador,
        104).Trim()
    if (-not $hashWebView2Publicado.Equals(
            $HashRuntimeWebView2,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "El lanzador nativo contiene un hash WebView2 inesperado: $hashWebView2Publicado."
    }

    $hashWebView2Embebido = [LanzadorScripts.Publicacion.RecursosNativos]::ObtenerSha256(
        $RutaLanzador,
        103)
    if (-not $hashWebView2Embebido.Equals(
            $HashRuntimeWebView2,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "El recurso WebView2 nativo esta corrupto. Esperado: $HashRuntimeWebView2. Detectado: $hashWebView2Embebido."
    }
}

function Set-ExecutableSignature {
    param(
        [string]$RutaExe,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificado,
        [string]$Descripcion
    )

    $firma = Set-AuthenticodeSignature `
        -FilePath $RutaExe `
        -Certificate $Certificado `
        -HashAlgorithm SHA256 `
        -TimestampServer $TimestampServer
    if ($firma.Status -ne 'Valid') {
        throw "No se pudo firmar $Descripcion correctamente: $($firma.Status)."
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

function ConvertTo-WindowsExtendedPath {
    param(
        [string]$Ruta
    )

    # Permite que las API Win32 lean metadatos en rutas superiores a MAX_PATH.
    $rutaCompleta = [System.IO.Path]::GetFullPath($Ruta)
    if ($rutaCompleta.StartsWith('\\?\', [System.StringComparison]::Ordinal)) {
        return $rutaCompleta
    }
    if ($rutaCompleta.StartsWith('\\', [System.StringComparison]::Ordinal)) {
        return '\\?\UNC\' + $rutaCompleta.Substring(2)
    }

    return '\\?\' + $rutaCompleta
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

    $rutaVersion = ConvertTo-WindowsExtendedPath -Ruta $ejecutableWebView2.FullName
    $informacionVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($rutaVersion)
    $versionEjecutable = $informacionVersion.FileVersion
    $versionProducto = $informacionVersion.ProductVersion
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
    $ejecutableMsi = Get-ChildItem `
        -LiteralPath $origen `
        -Filter 'msedgewebview2.exe' `
        -Recurse `
        -File |
        Select-Object -First 1
    if ($null -eq $ejecutableMsi -or $null -eq $ejecutableMsi.Directory) {
        throw 'No se pudo resolver la carpeta WebView2 que debe instalar el MSI.'
    }

    return $ejecutableMsi.Directory.FullName
}

function Assert-PortableRuntimePayload {
    param(
        [string]$RutaPayload,
        [string]$RutaCarpeta
    )

    # Impide que el runtime WebView2 vuelva a duplicarse dentro del payload .NET.
    if (-not (Test-Path -LiteralPath $RutaPayload -PathType Leaf)) {
        throw "No se encontro el payload .NET portable: $RutaPayload"
    }

    $archivosRuntime = @(Get-ChildItem -LiteralPath $RutaCarpeta -Recurse -File)
    if ($archivosRuntime.Count -ne 1 -or
        -not $archivosRuntime[0].FullName.Equals(
            $RutaPayload,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "La portable debe contener un unico payload .NET. Archivos detectados: $($archivosRuntime.Count)."
    }

    $tamanoMaximoPayload = 160MB
    if ($archivosRuntime[0].Length -gt $tamanoMaximoPayload) {
        throw "El payload .NET portable supera 160 MB y puede contener WebView2 duplicado."
    }
}

function Assert-PublishedExecutable {
    param(
        [string]$RutaExe,
        [bool]$DebeEstarFirmado,
        [string]$SufijoProducto = ''
    )

    # Comprueba que el EXE corresponde al proyecto y al commit actuales.
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($RutaExe)
    if (-not $version.FileVersion.Equals($versionArchivoEsperada, [System.StringComparison]::Ordinal)) {
        throw "La version de archivo del EXE no coincide. Esperada: $versionArchivoEsperada. Detectada: $($version.FileVersion)."
    }

    $productoEsperado = "$versionProductoEsperada+$revisionGitEsperada$SufijoProducto"
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

function Assert-PublishedMsi {
    param(
        [string]$RutaMsi,
        [bool]$DebeEstarFirmado
    )

    # Comprueba firma, metadatos y huella del instalador final.
    if (-not (Test-Path -LiteralPath $RutaMsi -PathType Leaf)) {
        throw "No se encontro el MSI publicado: $RutaMsi"
    }

    $tamano = (Get-Item -LiteralPath $RutaMsi).Length
    if ($tamano -lt $tamanoMinimoMsi) {
        throw "El MSI generado parece incompleto. Tamano: $tamano bytes."
    }

    $firma = Get-AuthenticodeSignature -LiteralPath $RutaMsi
    if ($DebeEstarFirmado -and
        ($firma.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
            $null -eq $firma.TimeStamperCertificate)) {
        throw "La firma Authenticode del MSI no es valida: $($firma.Status)."
    }

    $instalador = New-Object -ComObject WindowsInstaller.Installer
    $baseDatos = $null
    $vista = $null
    try {
        $baseDatos = $instalador.OpenDatabase((Resolve-Path -LiteralPath $RutaMsi).Path, 0)
        $vista = $baseDatos.OpenView('SELECT `Property`, `Value` FROM `Property`')
        [void]$vista.Execute()
        $propiedades = @{}
        while ($true) {
            $fila = $vista.Fetch()
            if ($null -eq $fila) {
                break
            }

            try {
                $propiedades[[string]$fila.StringData(1)] = [string]$fila.StringData(2)
            }
            finally {
                [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($fila) | Out-Null
            }
        }

        if ($propiedades.ProductVersion -ne $versionProductoEsperada -or
            $propiedades.ALLUSERS -ne '1' -or
            $propiedades.UpgradeCode -ne '{24169C78-5164-45C8-AB1A-AFC281D86DE9}' -or
            $propiedades.LANZADOR_MSI_CONFIGURADO -ne '1') {
            throw 'Los metadatos del MSI publicado no coinciden con el contrato 1.8.4.'
        }
    }
    finally {
        if ($null -ne $vista) {
            try {
                [void]$vista.Close()
            }
            catch {
            }
            [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($vista) | Out-Null
        }
        if ($null -ne $baseDatos) {
            [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($baseDatos) | Out-Null
        }
        [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($instalador) | Out-Null
    }

    $hash = (Get-FileHash -LiteralPath $RutaMsi -Algorithm SHA256).Hash
    Write-Host "MSI final: $RutaMsi"
    Write-Host "SHA-256 final: $hash"
    return $hash
}

Write-Host 'Restaurando dependencias...'
Invoke-NativeChecked -Descripcion 'dotnet restore' -Comando {
    dotnet restore (Join-Path $raiz 'Pruebas\LanzadorScripts.Pruebas.csproj')
}

$runtimeWebView2Source = Initialize-WebView2EmbeddedRuntime

Write-Host 'Compilando aplicacion...'
Invoke-NativeChecked -Descripcion 'dotnet build' -Comando {
    dotnet build $proyecto -c Release --no-restore
}

Write-Host 'Ejecutando pruebas...'
Invoke-NativeChecked -Descripcion 'dotnet test' -Comando {
    dotnet test (Join-Path $raiz 'Pruebas\LanzadorScripts.Pruebas.csproj') -c Release --no-restore
}

Write-Host 'Publicando runtime .NET interno...'
if (Test-Path -LiteralPath $stagingCompleta) {
    Remove-Item -LiteralPath $stagingCompleta -Recurse -Force
}

if (Test-Path -LiteralPath $runtimeStagingCompleta) {
    Remove-Item -LiteralPath $runtimeStagingCompleta -Recurse -Force
}

if (Test-Path -LiteralPath $salidaAnteriorCompleta) {
    Remove-Item -LiteralPath $salidaAnteriorCompleta -Recurse -Force
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
        -p:EmbedWebView2Runtime=false `
        -p:IncludeInstalledWebView2Runtime=false `
        -p:PublishReadyToRun=true `
        -p:PublishTrimmed=false `
        -p:UseAppHost=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $runtimeStagingCompleta
}

$runtimeExe = Join-Path $runtimeStagingCompleta 'LanzadorScripts.exe'
Assert-PortableRuntimePayload `
    -RutaPayload $runtimeExe `
    -RutaCarpeta $runtimeStagingCompleta

$certificadoFirma = Get-SigningCertificate
if ($null -ne $certificadoFirma) {
    Write-Host 'Firmando runtime .NET interno...'
    Set-ExecutableSignature `
        -RutaExe $runtimeExe `
        -Certificado $certificadoFirma `
        -Descripcion 'el runtime .NET interno'
} else {
    if (-not $AllowUnsignedForDev) {
        throw 'No se indico certificado Authenticode. Use -AllowUnsignedForDev solo para pruebas locales.'
    }

    Write-Warning 'Publicacion local sin firma permitida explicitamente para desarrollo.'
}

$hashRuntimeExe = Assert-PublishedExecutable `
    -RutaExe $runtimeExe `
    -DebeEstarFirmado ($null -ne $certificadoFirma)

Write-Host 'Creando el lanzador nativo portable...'
New-Item -ItemType Directory -Path $stagingCompleta | Out-Null
$exePortable = Join-Path $stagingCompleta $nombrePortableFinal
New-NativeLauncher `
    -RutaPayload $runtimeExe `
    -HashPayload $hashRuntimeExe `
    -RutaRuntimeWebView2 $runtimeZipIntermedio `
    -HashRuntimeWebView2 $hashZipWebView2Fijado `
    -RutaSalida $exePortable
Assert-NativeLauncherPayload `
    -RutaLanzador $exePortable `
    -RutaPayload $runtimeExe `
    -HashPayload $hashRuntimeExe `
    -RutaRuntimeWebView2 $runtimeZipIntermedio `
    -HashRuntimeWebView2 $hashZipWebView2Fijado

if ($null -ne $certificadoFirma) {
    Write-Host 'Firmando el lanzador portable final...'
    Set-ExecutableSignature `
        -RutaExe $exePortable `
        -Certificado $certificadoFirma `
        -Descripcion (Split-Path -Leaf $exePortable)
}

Write-Host 'Validando el arranque y la limpieza de la portable...'
$pruebaPortable = Start-Process `
    -FilePath $exePortable `
    -ArgumentList '--validar-distribucion-portable' `
    -Wait `
    -PassThru `
    -WindowStyle Hidden
if ($pruebaPortable.ExitCode -ne 0) {
    throw "La portable no supero la validacion de arranque protegido: $($pruebaPortable.ExitCode)."
}

Write-Host 'Compilando el instalador MSI con Visual Studio Professional 2026...'
if (-not (Test-Path -LiteralPath $scriptCompilarMsi -PathType Leaf)) {
    throw "No se encontro el compilador MSI: $scriptCompilarMsi"
}

if ($null -ne $certificadoFirma) {
    $certificadoEnAlmacen = Get-ChildItem -Path Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Thumbprint -eq $certificadoFirma.Thumbprint -and
            $_.HasPrivateKey
        } |
        Select-Object -First 1
    if ($null -eq $certificadoEnAlmacen) {
        throw 'El certificado Authenticode debe estar importado con clave privada para firmar el MSI.'
    }

    & $scriptCompilarMsi `
        -CertThumbprint $certificadoFirma.Thumbprint `
        -TimestampServer $TimestampServer `
        -RutaRuntimeWebView2 $runtimeWebView2Source
}
else {
    & $scriptCompilarMsi `
        -RutaRuntimeWebView2 $runtimeWebView2Source `
        -DesarrolloSinFirma
}

if (-not (Test-Path -LiteralPath $msiCompilado -PathType Leaf)) {
    throw 'No se genero el instalador MSI final.'
}

$msiPublicado = Join-Path $stagingCompleta $nombreMsiFinal
Copy-Item -LiteralPath $msiCompilado -Destination $msiPublicado

$archivosPublicados = @(Get-ChildItem -LiteralPath $stagingCompleta -Recurse -File)
$rutasEsperadas = @($msiPublicado, $exePortable)
$inesperados = @($archivosPublicados | Where-Object {
    $_.FullName -notin $rutasEsperadas
})
if ($archivosPublicados.Count -ne 2 -or $inesperados.Count -gt 0) {
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

$tamanoExe = (Get-Item -LiteralPath $exePortable).Length
if ($tamanoExe -lt $tamanoMinimoExe) {
    throw "El EXE portable parece incompleto. Tamano: $tamanoExe bytes."
}

$hashesValidados = @{}
$hashesValidados[$nombrePortableFinal] = Assert-PublishedExecutable `
    -RutaExe $exePortable `
    -DebeEstarFirmado ($null -ne $certificadoFirma) `
    -SufijoProducto '.portable'
$hashesValidados[$nombreMsiFinal] = Assert-PublishedMsi `
    -RutaMsi $msiPublicado `
    -DebeEstarFirmado ($null -ne $certificadoFirma)

# Sustituye la publicacion solo despues de validar todo el staging.
$habiaPublicacionAnterior = Test-Path -LiteralPath $salidaCompleta
$publicacionNuevaInstalada = $false
try {
    if ($habiaPublicacionAnterior) {
        Move-Item -LiteralPath $salidaCompleta -Destination $salidaAnteriorCompleta
    }

    Move-Item -LiteralPath $stagingCompleta -Destination $salidaCompleta
    $publicacionNuevaInstalada = $true

    foreach ($nombreArchivo in $hashesValidados.Keys) {
        $archivoFinal = Join-Path $salidaCompleta $nombreArchivo
        $hashFinal = (Get-FileHash -LiteralPath $archivoFinal -Algorithm SHA256).Hash
        $hashEsperado = $hashesValidados[$nombreArchivo]
        if (-not $hashFinal.Equals($hashEsperado, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "El archivo $nombreArchivo cambio durante la sustitucion final. Esperado: $hashEsperado. Detectado: $hashFinal."
        }
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

foreach ($temporalPublicacion in @($runtimeStagingCompleta, $lanzadorNativoCompleta)) {
    try {
        if (Test-Path -LiteralPath $temporalPublicacion) {
            Remove-Item -LiteralPath $temporalPublicacion -Recurse -Force
        }
    } catch {
        Write-Warning "No se pudo retirar la carpeta temporal $temporalPublicacion."
    }
}

Write-Host "MSI instalado generado: $(Join-Path $salidaCompleta $nombreMsiFinal)"
Write-Host "EXE portable generado: $(Join-Path $salidaCompleta $nombrePortableFinal)"
