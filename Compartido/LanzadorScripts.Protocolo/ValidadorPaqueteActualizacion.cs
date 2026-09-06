// (Autor: Alex Roman)
// Descripcion: Valida de forma estricta los paquetes MSI publicados para actualizar LanzadorScripts.

using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace LanzadorScripts.Protocolo;

public sealed record ResultadoValidacionPaqueteActualizacion(
    bool Valido,
    string NombreArchivo,
    Version? Version,
    long Longitud,
    string Sha256,
    DateTimeOffset FechaUtc,
    string EstadoFirma,
    string Mensaje)
{
    public static ResultadoValidacionPaqueteActualizacion Rechazado(
        string nombreArchivo,
        long longitud,
        DateTimeOffset fechaUtc,
        string mensaje)
    {
        return new ResultadoValidacionPaqueteActualizacion(
            false,
            nombreArchivo,
            null,
            longitud,
            string.Empty,
            fechaUtc,
            "Rechazada",
            mensaje);
    }
}

public static partial class ValidadorPaqueteActualizacion
{
    public const string NombreProductoEsperado = "LanzadorScripts";
    public const string UpgradeCodeEsperado = "{24169C78-5164-45C8-AB1A-AFC281D86DE9}";
    public const string HuellaFirmaEsperada = "6C654649369000DDE0AA70F62645058D9A3437F5";
    public const long LongitudMaxima = 2L * 1024 * 1024 * 1024;

    private const uint ErrorCorrecto = 0;
    private const uint ErrorMasDatos = 234;
    private const uint CertificadoRaizNoConfiable = 0x800B0109;
    private const uint InterfazNinguna = 2;
    private const uint EleccionArchivo = 1;
    private const uint AccionEstadoIgnorar = 0;
    private const uint SoloCacheRevocacion = 0x00001000;
    private const uint PropiedadPlantilla = 7;

    private static readonly Guid AccionVerificacionGenerica =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    [GeneratedRegex(
        "^LanzadorScripts-(?<version>[0-9]+\\.[0-9]+\\.[0-9]+)-x64\\.msi$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        500)]
    private static partial Regex PatronNombre();

    public static ResultadoValidacionPaqueteActualizacion Validar(string rutaArchivo)
    {
        ArgumentNullException.ThrowIfNull(rutaArchivo);
        var nombre = Path.GetFileName(rutaArchivo);
        try
        {
            var ruta = ValidarRuta(rutaArchivo);
            var coincidencia = PatronNombre().Match(nombre);
            if (!coincidencia.Success
                || !Version.TryParse(coincidencia.Groups["version"].Value, out var versionNombre)
                || versionNombre.Build < 0
                || versionNombre.Revision >= 0)
            {
                throw new InvalidDataException(
                    "El nombre debe seguir el formato LanzadorScripts-X.Y.Z-x64.msi.");
            }

            var inicial = new FileInfo(ruta);
            if (inicial.Length is <= 0 or > LongitudMaxima)
            {
                throw new InvalidDataException("El MSI tiene un tamano no permitido.");
            }

            using var bloqueo = new FileStream(
                ruta,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan);
            var sha256 = Convert.ToHexString(SHA256.HashData(bloqueo));
            bloqueo.Position = 0;

            var propiedades = LeerPropiedadesMsi(ruta);
            if (!string.Equals(
                    propiedades.NombreProducto,
                    NombreProductoEsperado,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("El MSI no pertenece a LanzadorScripts.");
            }

            if (!Version.TryParse(propiedades.VersionProducto, out var versionMsi)
                || versionMsi.Build < 0
                || versionMsi.Revision >= 0
                || versionMsi != versionNombre)
            {
                throw new InvalidDataException("La version interna del MSI no coincide con su nombre.");
            }

            if (!string.Equals(
                    propiedades.UpgradeCode,
                    UpgradeCodeEsperado,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("El UpgradeCode del MSI no es el autorizado.");
            }

            if (!EsPlantillaX64(propiedades.Plantilla))
            {
                throw new InvalidDataException("El paquete MSI no declara arquitectura x64.");
            }

            var firma = ValidarFirma(ruta);
            var final = new FileInfo(ruta);
            if (final.Length != inicial.Length
                || final.LastWriteTimeUtc != inicial.LastWriteTimeUtc)
            {
                throw new IOException("El MSI cambio durante la validacion.");
            }

            return new ResultadoValidacionPaqueteActualizacion(
                true,
                nombre,
                versionMsi,
                final.Length,
                sha256,
                new DateTimeOffset(final.LastWriteTimeUtc, TimeSpan.Zero),
                firma,
                string.Empty);
        }
        catch (Exception ex) when (ex is ArgumentException
            or CryptographicException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or Win32Exception)
        {
            var informacion = IntentarObtenerInformacion(rutaArchivo);
            return ResultadoValidacionPaqueteActualizacion.Rechazado(
                nombre,
                informacion.Longitud,
                informacion.FechaUtc,
                LimitarMensaje(ex.Message));
        }
    }

    public static bool EsVersionPosterior(string versionDisponible, string versionActual)
    {
        return Version.TryParse(versionDisponible, out var disponible)
            && Version.TryParse(versionActual, out var actual)
            && disponible > actual;
    }

    public static string ValidarFirmaAuthenticode(string rutaArchivo)
    {
        ArgumentNullException.ThrowIfNull(rutaArchivo);
        var ruta = Path.GetFullPath(rutaArchivo);
        if (!File.Exists(ruta))
        {
            throw new FileNotFoundException("No se encontro el archivo firmado.", ruta);
        }

        RechazarPuntosReanalisis(ruta);
        return ValidarFirma(ruta);
    }

    private static string ValidarRuta(string rutaArchivo)
    {
        if (string.IsNullOrWhiteSpace(rutaArchivo)
            || rutaArchivo.IndexOf('\0') >= 0
            || rutaArchivo.Length > 32_000)
        {
            throw new ArgumentException("La ruta del MSI no es valida.", nameof(rutaArchivo));
        }

        var ruta = Path.GetFullPath(rutaArchivo);
        if (!File.Exists(ruta))
        {
            throw new FileNotFoundException("No se encontro el MSI publicado.", ruta);
        }

        RechazarPuntosReanalisis(ruta);
        return ruta;
    }

    private static void RechazarPuntosReanalisis(string ruta)
    {
        var actual = ruta;
        while (!string.IsNullOrWhiteSpace(actual))
        {
            if ((File.GetAttributes(actual) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("La ruta del MSI no puede atravesar enlaces.");
            }

            var padre = Path.GetDirectoryName(actual);
            if (string.IsNullOrWhiteSpace(padre)
                || string.Equals(padre, actual, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            actual = padre;
        }
    }

    private static PropiedadesMsi LeerPropiedadesMsi(string ruta)
    {
        var resultado = MsiOpenDatabase(ruta, IntPtr.Zero, out var baseDatos);
        if (resultado != ErrorCorrecto)
        {
            throw new Win32Exception((int)resultado, "Windows Installer no pudo abrir el MSI.");
        }

        try
        {
            var nombre = LeerPropiedadMsi(baseDatos, "ProductName");
            var version = LeerPropiedadMsi(baseDatos, "ProductVersion");
            var upgradeCode = LeerPropiedadMsi(baseDatos, "UpgradeCode");
            var plantilla = LeerPlantillaMsi(baseDatos);
            return new PropiedadesMsi(nombre, version, upgradeCode, plantilla);
        }
        finally
        {
            _ = MsiCloseHandle(baseDatos);
        }
    }

    private static string LeerPropiedadMsi(uint baseDatos, string propiedad)
    {
        const string consulta = "SELECT `Value` FROM `Property` WHERE `Property` = ?";
        ComprobarMsi(MsiDatabaseOpenView(baseDatos, consulta, out var vista));
        try
        {
            var registroParametro = MsiCreateRecord(1);
            if (registroParametro == 0)
            {
                throw new Win32Exception("Windows Installer no pudo crear el registro de consulta.");
            }

            try
            {
                ComprobarMsi(MsiRecordSetString(registroParametro, 1, propiedad));
                ComprobarMsi(MsiViewExecute(vista, registroParametro));
                ComprobarMsi(MsiViewFetch(vista, out var registro));
                try
                {
                    return LeerCadenaRegistro(registro, 1);
                }
                finally
                {
                    _ = MsiCloseHandle(registro);
                }
            }
            finally
            {
                _ = MsiCloseHandle(registroParametro);
            }
        }
        finally
        {
            _ = MsiViewClose(vista);
            _ = MsiCloseHandle(vista);
        }
    }

    private static string LeerPlantillaMsi(uint baseDatos)
    {
        ComprobarMsi(MsiGetSummaryInformation(baseDatos, null, 0, out var resumen));
        try
        {
            uint tipo = 0;
            var entero = 0;
            var fecha = new FileTime();
            uint longitud = 0;
            var resultado = MsiSummaryInfoGetProperty(
                resumen,
                PropiedadPlantilla,
                ref tipo,
                ref entero,
                ref fecha,
                null,
                ref longitud);
            if (resultado is not ErrorCorrecto and not ErrorMasDatos)
            {
                ComprobarMsi(resultado);
            }

            var texto = new StringBuilder(checked((int)longitud + 1));
            longitud = (uint)texto.Capacity;
            ComprobarMsi(MsiSummaryInfoGetProperty(
                resumen,
                PropiedadPlantilla,
                ref tipo,
                ref entero,
                ref fecha,
                texto,
                ref longitud));
            return texto.ToString();
        }
        finally
        {
            _ = MsiCloseHandle(resumen);
        }
    }

    private static string LeerCadenaRegistro(uint registro, uint campo)
    {
        uint longitud = 0;
        var resultado = MsiRecordGetString(registro, campo, new StringBuilder(1), ref longitud);
        if (resultado is not ErrorCorrecto and not ErrorMasDatos)
        {
            ComprobarMsi(resultado);
        }

        var texto = new StringBuilder(checked((int)longitud + 1));
        // Windows Installer devuelve el tamano sin incluir el terminador nulo.
        longitud = (uint)texto.Capacity;
        ComprobarMsi(MsiRecordGetString(registro, campo, texto, ref longitud));
        return texto.ToString();
    }

    private static string ValidarFirma(string ruta)
    {
        var codigo = VerificarFirmaWindows(ruta);
        if (codigo is not ErrorCorrecto and not CertificadoRaizNoConfiable)
        {
            throw new CryptographicException(
                $"Windows rechazo la firma Authenticode del MSI (0x{codigo:X8}).");
        }

#pragma warning disable SYSLIB0057
        using var certificado = new X509Certificate2(X509Certificate.CreateFromSignedFile(ruta));
#pragma warning restore SYSLIB0057
        var huella = certificado.Thumbprint?.Replace(" ", string.Empty, StringComparison.Ordinal)
            ?? string.Empty;
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(huella.ToUpperInvariant()),
                Encoding.ASCII.GetBytes(HuellaFirmaEsperada)))
        {
            throw new CryptographicException("El certificado firmante del MSI no es el autorizado.");
        }

        return codigo == ErrorCorrecto ? "Valida" : "Valida, raiz no confiable";
    }

    private static uint VerificarFirmaWindows(string ruta)
    {
        var archivo = new WinTrustFileInfo(ruta);
        var archivoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(archivo, archivoPtr, false);
            var datos = new WinTrustData(archivoPtr);
            return unchecked((uint)WinVerifyTrust(
                IntPtr.Zero,
                AccionVerificacionGenerica,
                ref datos));
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(archivoPtr);
            Marshal.FreeHGlobal(archivoPtr);
        }
    }

    private static bool EsPlantillaX64(string plantilla)
    {
        var arquitectura = plantilla.Split(';', 2)[0].Trim();
        return arquitectura.Equals("x64", StringComparison.OrdinalIgnoreCase);
    }

    private static (long Longitud, DateTimeOffset FechaUtc) IntentarObtenerInformacion(string? ruta)
    {
        try
        {
            var archivo = new FileInfo(Path.GetFullPath(ruta ?? string.Empty));
            return archivo.Exists
                ? (archivo.Length, new DateTimeOffset(archivo.LastWriteTimeUtc, TimeSpan.Zero))
                : (0, DateTimeOffset.MinValue);
        }
        catch
        {
            return (0, DateTimeOffset.MinValue);
        }
    }

    private static string LimitarMensaje(string mensaje)
    {
        var valor = mensaje.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return valor.Length <= 300 ? valor : valor[..300];
    }

    private static void ComprobarMsi(uint resultado)
    {
        if (resultado != ErrorCorrecto)
        {
            throw new Win32Exception((int)resultado, "Windows Installer rechazo la consulta del paquete.");
        }
    }

    private sealed record PropiedadesMsi(
        string NombreProducto,
        string VersionProducto,
        string UpgradeCode,
        string Plantilla);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        public readonly uint Bajo;
        public readonly uint Alto;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo
    {
        public uint Tamano = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        [MarshalAs(UnmanagedType.LPWStr)]
        public string RutaArchivo;
        public IntPtr Archivo = IntPtr.Zero;
        public IntPtr Asunto = IntPtr.Zero;

        public WinTrustFileInfo(string rutaArchivo)
        {
            RutaArchivo = rutaArchivo;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint Tamano;
        public IntPtr DatosPolitica;
        public IntPtr ClienteSip;
        public uint Interfaz;
        public uint Revocacion;
        public uint EleccionUnion;
        public IntPtr InformacionArchivo;
        public uint AccionEstado;
        public IntPtr DatosEstado;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UrlReferencia;
        public uint OpcionesProveedor;
        public uint ContextoInterfaz;
        public IntPtr Firma;

        public WinTrustData(IntPtr informacionArchivo)
        {
            Tamano = (uint)Marshal.SizeOf<WinTrustData>();
            DatosPolitica = IntPtr.Zero;
            ClienteSip = IntPtr.Zero;
            Interfaz = InterfazNinguna;
            Revocacion = 0;
            EleccionUnion = EleccionArchivo;
            InformacionArchivo = informacionArchivo;
            AccionEstado = AccionEstadoIgnorar;
            DatosEstado = IntPtr.Zero;
            UrlReferencia = null;
            OpcionesProveedor = SoloCacheRevocacion;
            ContextoInterfaz = 0;
            Firma = IntPtr.Zero;
        }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(
        string rutaBaseDatos,
        IntPtr persistencia,
        out uint baseDatos);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(
        uint baseDatos,
        string consulta,
        out uint vista);

    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(uint vista, uint registro);

    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(uint vista, out uint registro);

    [DllImport("msi.dll")]
    private static extern uint MsiViewClose(uint vista);

    [DllImport("msi.dll")]
    private static extern uint MsiCreateRecord(uint campos);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordSetStringW")]
    private static extern uint MsiRecordSetString(uint registro, uint campo, string valor);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")]
    private static extern uint MsiRecordGetString(
        uint registro,
        uint campo,
        StringBuilder? valor,
        ref uint longitud);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetSummaryInformationW")]
    private static extern uint MsiGetSummaryInformation(
        uint baseDatos,
        string? rutaBaseDatos,
        uint actualizaciones,
        out uint resumen);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiSummaryInfoGetPropertyW")]
    private static extern uint MsiSummaryInfoGetProperty(
        uint resumen,
        uint propiedad,
        ref uint tipo,
        ref int entero,
        ref FileTime fecha,
        StringBuilder? valor,
        ref uint longitud);

    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(uint identificador);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(
        IntPtr ventana,
        [MarshalAs(UnmanagedType.LPStruct)] Guid accion,
        ref WinTrustData datos);
}
