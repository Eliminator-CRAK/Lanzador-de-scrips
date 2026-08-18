// (Autor: Alex Roman)
// Descripcion: Aprovisiona una cuenta administradora de un solo uso para crear la base.

using System.Security.Cryptography;
using System.Text;

namespace LanzadorScripts.Servidor.Core;

public sealed class AlmacenAdministradorInicialServidor
{
    private static readonly byte[] Cabecera = "LSADMIN1"u8.ToArray();
    private static readonly UTF8Encoding Utf8Estricto = new(false, true);
    private readonly RutasServidor _rutas;
    private readonly IProtectorClaveServidor _protector;

    public AlmacenAdministradorInicialServidor(
        RutasServidor rutas,
        IProtectorClaveServidor? protector = null)
    {
        _rutas = rutas;
        _protector = protector ?? new ProtectorClaveDpapi();
    }

    public void Preparar(string cuenta)
    {
        // Guarda la identidad protegida hasta el primer arranque correcto.
        var normalizada = ConfiguracionServidor.NormalizarCuenta(cuenta);
        if (normalizada.Length == 0)
        {
            throw new InvalidDataException("La cuenta administradora inicial no es valida.");
        }

        _rutas.PrepararDirectorios();
        var claro = Utf8Estricto.GetBytes(normalizada);
        var protegido = _protector.Proteger(claro);
        var contenido = new byte[Cabecera.Length + protegido.Length];
        var temporal = _rutas.RutaAdministradorInicialProtegido + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Cabecera.CopyTo(contenido, 0);
            protegido.CopyTo(contenido, Cabecera.Length);
            using (var flujo = new FileStream(
                temporal,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                flujo.Write(contenido);
                flujo.Flush(flushToDisk: true);
            }

            File.Move(temporal, _rutas.RutaAdministradorInicialProtegido, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claro);
            CryptographicOperations.ZeroMemory(protegido);
            CryptographicOperations.ZeroMemory(contenido);
            if (File.Exists(temporal))
            {
                File.Delete(temporal);
            }
        }
    }

    public string? Leer()
    {
        // Recupera la cuenta sin eliminarla hasta confirmar la inicializacion.
        if (!File.Exists(_rutas.RutaAdministradorInicialProtegido))
        {
            return null;
        }

        RutasServidor.RechazarPuntoReanalisis(_rutas.RutaAdministradorInicialProtegido);
        var contenido = File.ReadAllBytes(_rutas.RutaAdministradorInicialProtegido);
        if (contenido.Length <= Cabecera.Length || contenido.Length > 64 * 1024
            || !contenido.AsSpan(0, Cabecera.Length).SequenceEqual(Cabecera))
        {
            CryptographicOperations.ZeroMemory(contenido);
            throw new CryptographicException("El aprovisionamiento administrativo no tiene un formato valido.");
        }

        var protegido = contenido.AsSpan(Cabecera.Length).ToArray();
        byte[]? claro = null;
        try
        {
            claro = _protector.Desproteger(protegido);
            if (claro.Length is <= 0 or > 1024)
            {
                throw new CryptographicException("La identidad administrativa protegida no es valida.");
            }

            var cuenta = ConfiguracionServidor.NormalizarCuenta(Utf8Estricto.GetString(claro));
            return cuenta.Length > 0
                ? cuenta
                : throw new CryptographicException("La identidad administrativa protegida no es valida.");
        }
        finally
        {
            if (claro is not null)
            {
                CryptographicOperations.ZeroMemory(claro);
            }

            CryptographicOperations.ZeroMemory(protegido);
            CryptographicOperations.ZeroMemory(contenido);
        }
    }

    public void Eliminar()
    {
        // Retira el aprovisionamiento despues de crear o validar la base.
        if (!File.Exists(_rutas.RutaAdministradorInicialProtegido))
        {
            return;
        }

        RutasServidor.RechazarPuntoReanalisis(_rutas.RutaAdministradorInicialProtegido);
        File.Delete(_rutas.RutaAdministradorInicialProtegido);
    }
}
