// (Autor: Alex Roman)
// Descripcion: Comprueba la lectura segura y la migracion de artefactos firmados v1.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanzadorScripts.Servicios;
using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasCompatibilidadArtefactos
{
    private const string Algoritmo = "AES-256-GCM+RSA-PSS-SHA256";
    private const string Autor = "Alex Roman";
    private const string Descripcion = "Artefacto cifrado y firmado de LanzadorScripts.";

    [Fact]
    public void ContenedorLegadoValidoSeLeeYLaEscrituraNuevaUsaVersionDos()
    {
        var clave = RandomNumberGenerator.GetBytes(32);
        using var firmaActual = RSA.Create(3072);
        using var firmaLegada = RSA.Create(3072);
        var legado = CrearContenedor(
            clave,
            firmaLegada,
            version: 1,
            ServicioArtefactosProtegidos.TipoPermisos,
            "{\"usuarios\":[]}");
        var servicio = CrearServicio(
            clave,
            firmaActual,
            firmaLegada,
            huellaPermisos: ObtenerHuella(legado));

        Assert.True(servicio.IntentarDesprotegerTexto(
            ServicioArtefactosProtegidos.TipoPermisos,
            legado,
            out var claro,
            out _));
        Assert.Equal("{\"usuarios\":[]}", claro);
        Assert.True(servicio.IntentarObtenerKeyIdFirmado(
            ServicioArtefactosProtegidos.TipoPermisos,
            legado,
            out var keyId,
            out _));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(clave))[..16], keyId);

        var actualizado = servicio.ProtegerTexto(
            ServicioArtefactosProtegidos.TipoPermisos,
            claro);
        using var documento = JsonDocument.Parse(actualizado);
        Assert.Equal(2, documento.RootElement.GetProperty("Version").GetInt32());
        CryptographicOperations.ZeroMemory(clave);
    }

    [Fact]
    public void CadaVersionExigeSuClavePublicaCorrespondiente()
    {
        var clave = RandomNumberGenerator.GetBytes(32);
        using var firmaActual = RSA.Create(3072);
        using var firmaLegada = RSA.Create(3072);
        var v1FirmadaComoActual = CrearContenedor(
            clave,
            firmaActual,
            version: 1,
            ServicioArtefactosProtegidos.TipoCatalogoScripts,
            "{\"scripts\":[]}");
        var v2FirmadaComoLegada = CrearContenedor(
            clave,
            firmaLegada,
            version: 2,
            ServicioArtefactosProtegidos.TipoCatalogoScripts,
            "{\"scripts\":[]}");
        var servicio = CrearServicio(
            clave,
            firmaActual,
            firmaLegada,
            huellaCatalogo: ObtenerHuella(v1FirmadaComoActual));

        Assert.False(servicio.IntentarDesprotegerTexto(
            ServicioArtefactosProtegidos.TipoCatalogoScripts,
            v1FirmadaComoActual,
            out _,
            out var errorV1));
        Assert.False(servicio.IntentarDesprotegerTexto(
            ServicioArtefactosProtegidos.TipoCatalogoScripts,
            v2FirmadaComoLegada,
            out _,
            out var errorV2));
        Assert.Equal("La firma del contenedor protegido no es valida.", errorV1);
        Assert.Equal("La firma del contenedor protegido no es valida.", errorV2);
        CryptographicOperations.ZeroMemory(clave);
    }

    [Fact]
    public void ContenedorLegadoManipuladoSeRechaza()
    {
        var clave = RandomNumberGenerator.GetBytes(32);
        using var firmaActual = RSA.Create(3072);
        using var firmaLegada = RSA.Create(3072);
        var legado = CrearContenedor(
            clave,
            firmaLegada,
            version: 1,
            ServicioArtefactosProtegidos.TipoPermisos,
            "{\"usuarios\":[]}");
        var servicio = CrearServicio(
            clave,
            firmaActual,
            firmaLegada,
            huellaPermisos: ObtenerHuella(legado));
        var contenedor = JsonSerializer.Deserialize<Dictionary<string, object>>(legado)!;
        contenedor["KeyId"] = "0000000000000000";
        var manipulado = JsonSerializer.Serialize(contenedor);

        Assert.False(servicio.IntentarDesprotegerTexto(
            ServicioArtefactosProtegidos.TipoPermisos,
            manipulado,
            out _,
            out var error));
        Assert.Equal("El contenedor v1 no pertenece a la migracion autorizada.", error);
        CryptographicOperations.ZeroMemory(clave);
    }

    [Fact]
    public void OtroContenedorLegadoConFirmaValidaSeRechaza()
    {
        var clave = RandomNumberGenerator.GetBytes(32);
        using var firmaActual = RSA.Create(3072);
        using var firmaLegada = RSA.Create(3072);
        var autorizado = CrearContenedor(
            clave,
            firmaLegada,
            version: 1,
            ServicioArtefactosProtegidos.TipoPermisos,
            "{\"usuarios\":[]}");
        var noAutorizado = CrearContenedor(
            clave,
            firmaLegada,
            version: 1,
            ServicioArtefactosProtegidos.TipoPermisos,
            "{\"usuarios\":[\"otro\"]}");
        var servicio = CrearServicio(
            clave,
            firmaActual,
            firmaLegada,
            huellaPermisos: ObtenerHuella(autorizado));

        Assert.False(servicio.IntentarDesprotegerTexto(
            ServicioArtefactosProtegidos.TipoPermisos,
            noAutorizado,
            out _,
            out var error));
        Assert.Equal("El contenedor v1 no pertenece a la migracion autorizada.", error);
        CryptographicOperations.ZeroMemory(clave);
    }

    private static ServicioArtefactosProtegidos CrearServicio(
        byte[] clave,
        RSA firmaActual,
        RSA firmaLegada,
        string? huellaPermisos = null,
        string? huellaCatalogo = null)
    {
        return new ServicioArtefactosProtegidos(
            clave,
            new ServicioFirmaArtefactos(
                firmaActual,
                firmaActual,
                firmaLegada),
            huellaPermisos ?? new string('0', 64),
            huellaCatalogo ?? new string('0', 64));
    }

    private static string ObtenerHuella(string texto)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto)));
    }

    private static string CrearContenedor(
        byte[] clave,
        RSA firma,
        int version,
        string tipo,
        string claro)
    {
        var keyId = Convert.ToHexString(SHA256.HashData(clave))[..16];
        var nonce = RandomNumberGenerator.GetBytes(12);
        var claroBytes = Encoding.UTF8.GetBytes(claro);
        var cifrado = new byte[claroBytes.Length];
        var etiqueta = new byte[16];
        try
        {
            var asociados = Encoding.UTF8.GetBytes(
                $"LanzadorScripts|artefacto|v{version}|{tipo}|{Algoritmo}|{keyId}");
            using (var aes = new AesGcm(clave, etiqueta.Length))
            {
                aes.Encrypt(nonce, claroBytes, cifrado, etiqueta, asociados);
            }

            var nonceBase64 = Convert.ToBase64String(nonce);
            var etiquetaBase64 = Convert.ToBase64String(etiqueta);
            var datosBase64 = Convert.ToBase64String(cifrado);
            var dominio = Encoding.UTF8.GetBytes(
                $"LanzadorScripts|firma|{Autor}|{Descripcion}|v{version}|{tipo}|{Algoritmo}|{keyId}|{nonceBase64}|{etiquetaBase64}|{datosBase64}");
            var firmaBase64 = Convert.ToBase64String(firma.SignData(
                dominio,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss));

            return JsonSerializer.Serialize(new
            {
                Autor,
                Descripcion,
                Version = version,
                Tipo = tipo,
                Algoritmo,
                KeyId = keyId,
                Nonce = nonceBase64,
                Etiqueta = etiquetaBase64,
                Datos = datosBase64,
                Firma = firmaBase64
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claroBytes);
        }
    }
}
