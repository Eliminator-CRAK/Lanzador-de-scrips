// (Autor: Alex Roman)
// Descripcion: Protege claves para identidades de Active Directory mediante DPAPI-NG.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace LanzadorScripts.Servicios;

internal interface IProtectorDpapiNg
{
    byte[] Proteger(ReadOnlySpan<byte> datos, string descriptor);

    byte[] Desproteger(ReadOnlySpan<byte> datosProtegidos);
}

internal sealed class ServicioDpapiNg : IProtectorDpapiNg
{
    private const int CodigoCorrecto = 0;
    private const int CodigoFalloCifrado = unchecked((int)0x80090034);
    private const uint BanderaSilenciosa = 0x00000040;
    private const int LongitudMaximaResultado = 1024 * 1024;

    private static readonly Regex PatronDescriptorSid = new(
        @"^SID=S-\d+(?:-\d+)+(?: (?:AND|OR) SID=S-\d+(?:-\d+)+)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public byte[] Proteger(ReadOnlySpan<byte> datos, string descriptor)
    {
        ValidarDescriptor(descriptor);
        if (datos.IsEmpty)
        {
            throw new ArgumentException("Los datos que se van a proteger no pueden estar vacios.", nameof(datos));
        }

        var copia = datos.ToArray();
        IntPtr descriptorNativo = IntPtr.Zero;
        IntPtr resultadoNativo = IntPtr.Zero;
        uint longitudResultado = 0;
        try
        {
            ValidarCodigo(
                NCryptCreateProtectionDescriptor(descriptor, 0, out descriptorNativo),
                "crear el descriptor DPAPI-NG");
            ValidarCodigo(
                NCryptProtectSecret(
                    descriptorNativo,
                    BanderaSilenciosa,
                    copia,
                    checked((uint)copia.Length),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out resultadoNativo,
                    out longitudResultado),
                "proteger la clave con DPAPI-NG");
            return CopiarResultado(resultadoNativo, longitudResultado);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copia);
            LiberarMemoriaLocal(resultadoNativo, longitudResultado, borrarContenido: false);
            if (descriptorNativo != IntPtr.Zero)
            {
                _ = NCryptCloseProtectionDescriptor(descriptorNativo);
            }
        }
    }

    public byte[] Desproteger(ReadOnlySpan<byte> datosProtegidos)
    {
        if (datosProtegidos.IsEmpty)
        {
            throw new ArgumentException("El paquete DPAPI-NG no puede estar vacio.", nameof(datosProtegidos));
        }

        var copia = datosProtegidos.ToArray();
        IntPtr resultadoNativo = IntPtr.Zero;
        uint longitudResultado = 0;
        try
        {
            ValidarCodigo(
                NCryptUnprotectSecret(
                    IntPtr.Zero,
                    BanderaSilenciosa,
                    copia,
                    checked((uint)copia.Length),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out resultadoNativo,
                    out longitudResultado),
                "recuperar la clave con DPAPI-NG");
            return CopiarResultado(resultadoNativo, longitudResultado);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copia);
            LiberarMemoriaLocal(resultadoNativo, longitudResultado, borrarContenido: true);
        }
    }

    internal static void ValidarDescriptor(string descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor)
            || descriptor.Length > 2048
            || !PatronDescriptorSid.IsMatch(descriptor))
        {
            throw new ArgumentException(
                "El descriptor debe contener uno o varios SID de Active Directory unidos por AND u OR.",
                nameof(descriptor));
        }

        try
        {
            foreach (var protector in Regex.Split(descriptor, " (?:AND|OR) "))
            {
                _ = new SecurityIdentifier(protector[4..]);
            }
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                "El descriptor contiene un SID no valido.",
                nameof(descriptor),
                ex);
        }
    }

    private static byte[] CopiarResultado(IntPtr origen, uint longitud)
    {
        if (origen == IntPtr.Zero || longitud == 0 || longitud > LongitudMaximaResultado)
        {
            throw new CryptographicException("DPAPI-NG devolvio un resultado con longitud no valida.");
        }

        var resultado = new byte[checked((int)longitud)];
        Marshal.Copy(origen, resultado, 0, resultado.Length);
        return resultado;
    }

    private static void LiberarMemoriaLocal(IntPtr memoria, uint longitud, bool borrarContenido)
    {
        if (memoria == IntPtr.Zero)
        {
            return;
        }

        if (borrarContenido && longitud > 0 && longitud <= LongitudMaximaResultado)
        {
            var ceros = new byte[checked((int)longitud)];
            Marshal.Copy(ceros, 0, memoria, ceros.Length);
        }

        _ = LocalFree(memoria);
    }

    private static void ValidarCodigo(int codigo, string operacion)
    {
        if (codigo != CodigoCorrecto)
        {
            // Explica el fallo habitual cuando el dominio no esta disponible.
            var detalleDominio = codigo == CodigoFalloCifrado
                ? " Compruebe la conexion al dominio y la disponibilidad de un controlador de dominio."
                : string.Empty;
            throw new CryptographicException(
                $"Windows no pudo {operacion}. Codigo 0x{unchecked((uint)codigo):X8}.{detalleDominio}");
        }
    }

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptCreateProtectionDescriptor(
        string descriptor,
        uint banderas,
        out IntPtr descriptorNativo);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptProtectSecret(
        IntPtr descriptor,
        uint banderas,
        byte[] datos,
        uint longitudDatos,
        IntPtr parametrosMemoria,
        IntPtr ventana,
        out IntPtr datosProtegidos,
        out uint longitudDatosProtegidos);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptUnprotectSecret(
        IntPtr descriptor,
        uint banderas,
        byte[] datosProtegidos,
        uint longitudDatosProtegidos,
        IntPtr parametrosMemoria,
        IntPtr ventana,
        out IntPtr datos,
        out uint longitudDatos);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptCloseProtectionDescriptor(IntPtr descriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memoria);
}
