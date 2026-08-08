// (Autor: Alex Roman)
// Descripcion: Firma artefactos con el certificado privado y valida con el certificado publico.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LanzadorScripts.Servicios;

public sealed class ServicioFirmaArtefactos
{
    private readonly RSA? _firmaPruebas;
    private readonly RSA? _verificacionPruebas;

    public ServicioFirmaArtefactos()
    {
    }

    internal ServicioFirmaArtefactos(RSA firmaPruebas, RSA verificacionPruebas)
    {
        _firmaPruebas = firmaPruebas;
        _verificacionPruebas = verificacionPruebas;
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
