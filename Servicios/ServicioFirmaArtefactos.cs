// (Autor: Alex Roman)
// Descripcion: Firma artefactos con el certificado privado y valida con el certificado publico.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LanzadorScripts.Servicios;

public sealed class ServicioFirmaArtefactos
{
    // Conserva solo la clave publica necesaria para validar artefactos v1 existentes.
    private const string ClavePublicaLegadaBase64 = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAwPGTdfQwv/lxBbAoc8BQV+LQ5Wn/99JfIyQgz87kkSN/rHRWlKWWnE7Z8eOqDElxbWcElcbjE4N56RoRYXAx73nJ3h7YOy33P4rHRz1/K6kwMrLvQvZecdqpmny2qhc55fi4cP4uF+UOl3klt80bJCVpXlEx9VQVR/FZbmpX09yiVHXzWDl+k4UsEMH7XCaRY8zj4ueBNpll5vDDTySCPjVbgIlo7M0lRdm3WzQqcpjb+4CN7w5HUyXrVCGBo/iDPkJNsE5dbRUAdCsZaIGpZbXZtWrGet+TEcbf0aPp6a+dkkoXk3otIE1JSAVDDS5fbnoupl7tuB3LutODzXCK8BQPVH1p9Of6JdVW8wmTlwMYAhMKbqk94GTC9/fmnrr76+kv5UWZiewyx6ocqBCwXGDS/ji74rCGyaaivFh460Wg01n0s0oDG333SY3YmpBwckZtUtK4au2WosoILTvpFCkOVQUOHxgfp37IJeuRlBop0vqlHGBvhMCiVzLDCMKnAgMBAAE=";

    private readonly RSA? _firmaPruebas;
    private readonly RSA? _verificacionPruebas;
    private readonly RSA? _verificacionLegadaPruebas;

    public ServicioFirmaArtefactos()
    {
    }

    internal ServicioFirmaArtefactos(RSA firmaPruebas, RSA verificacionPruebas)
        : this(firmaPruebas, verificacionPruebas, verificacionPruebas)
    {
    }

    internal ServicioFirmaArtefactos(
        RSA firmaPruebas,
        RSA verificacionPruebas,
        RSA verificacionLegadaPruebas)
    {
        _firmaPruebas = firmaPruebas;
        _verificacionPruebas = verificacionPruebas;
        _verificacionLegadaPruebas = verificacionLegadaPruebas;
    }

    public byte[] Firmar(ReadOnlySpan<byte> datos)
    {
        if (_firmaPruebas is not null)
        {
            return _firmaPruebas.SignData(
                datos,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
        }

        using var certificado = BuscarCertificadoPrivado()
            ?? throw new InvalidOperationException(
                $"No se encontro el certificado privado de firma {ServicioTokenMaestro.HuellaCertificado} en CurrentUser\\My ni LocalMachine\\My.");
        using var rsa = certificado.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("El certificado de firma no tiene una clave privada RSA.");
        return rsa.SignData(datos, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    public bool Verificar(ReadOnlySpan<byte> datos, ReadOnlySpan<byte> firma)
    {
        if (_verificacionPruebas is not null)
        {
            return _verificacionPruebas.VerifyData(
                datos,
                firma,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
        }

        using var certificado = X509CertificateLoader.LoadCertificate(
            Convert.FromBase64String(ServicioTokenMaestro.CertificadoPublicoBase64));
        using var rsa = certificado.GetRSAPublicKey();
        return rsa is not null
            && rsa.VerifyData(datos, firma, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    internal bool VerificarLegada(ReadOnlySpan<byte> datos, ReadOnlySpan<byte> firma)
    {
        if (_verificacionLegadaPruebas is not null)
        {
            return _verificacionLegadaPruebas.VerifyData(
                datos,
                firma,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
        }

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(ClavePublicaLegadaBase64),
            out _);
        return rsa.VerifyData(
            datos,
            firma,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
    }

    private static X509Certificate2? BuscarCertificadoPrivado()
    {
        using var usuario = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        using var equipo = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        usuario.Open(OpenFlags.ReadOnly);
        equipo.Open(OpenFlags.ReadOnly);
        return BuscarCertificadoPrivado(usuario) ?? BuscarCertificadoPrivado(equipo);
    }

    private static X509Certificate2? BuscarCertificadoPrivado(X509Store almacen)
    {
        return almacen.Certificates
            .Find(
                X509FindType.FindByThumbprint,
                ServicioTokenMaestro.HuellaCertificado,
                validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault(certificado => certificado.HasPrivateKey);
    }
}
