// (Autor: Alex Roman)
// Descripcion: Comprueba el aprovisionamiento automatico y firmado de la clave de artefactos.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LanzadorScripts.Servicios;
using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasAprovisionamientoClave
{
    [Fact]
    public void PaqueteValidoAprovisionaLaMismaClaveDeLosDosArtefactos()
    {
        using var entorno = EntornoTemporal.Crear();
        using var rsa = RSA.Create(3072);
        var clave = RandomNumberGenerator.GetBytes(32);
        var claveAutor = new ServicioClaveArtefactos(
            Path.Combine(entorno.Raiz, "autor", "artefactos.key"));
        var claveCliente = new ServicioClaveArtefactos(
            Path.Combine(entorno.Raiz, "cliente", "artefactos.key"));
        ServicioClaveArtefactos.Aprovisionar(claveAutor.RutaClave, clave, aplicarAcl: false);
        var artefactos = CrearArtefactos(entorno.Permisos, clave, rsa);
        var protector = new ProtectorDpapiNgPruebas();

        CrearServicio(claveAutor, rsa, artefactos, protector)
            .CrearPaquete(entorno.Permisos, "SID=S-1-5-21-1-2-3-1001");
        var resultado = CrearServicio(claveCliente, rsa, artefactos, protector)
            .IntentarAprovisionar(entorno.Permisos);

        Assert.Equal(EstadoAprovisionamientoClave.Aprovisionada, resultado.Estado);
        Assert.True(claveCliente.Existe);
        using var material = claveCliente.ObtenerMaterial();
        Assert.True(material.Clave.SequenceEqual(clave));
        var textoPaquete = File.ReadAllText(
            Path.Combine(entorno.Permisos, ServicioAprovisionamientoClaveArtefactos.NombrePaquete),
            Encoding.UTF8);
        var paquete = JsonNode.Parse(textoPaquete)!.AsObject();
        Assert.Equal(
            "DPAPI-NG+RSA-PSS-SHA256",
            paquete["Algoritmo"]!.GetValue<string>());
        Assert.DoesNotContain(Convert.ToBase64String(clave), textoPaquete, StringComparison.Ordinal);
        CryptographicOperations.ZeroMemory(clave);
    }

    [Fact]
    public void PaqueteManipuladoNoCreaLaClaveLocal()
    {
        using var entorno = EntornoTemporal.Crear();
        using var rsa = RSA.Create(3072);
        var clave = RandomNumberGenerator.GetBytes(32);
        var claveAutor = new ServicioClaveArtefactos(
            Path.Combine(entorno.Raiz, "autor", "artefactos.key"));
        var claveCliente = new ServicioClaveArtefactos(
            Path.Combine(entorno.Raiz, "cliente", "artefactos.key"));
        ServicioClaveArtefactos.Aprovisionar(claveAutor.RutaClave, clave, aplicarAcl: false);
        var artefactos = CrearArtefactos(entorno.Permisos, clave, rsa);
        var protector = new ProtectorDpapiNgPruebas();
        var servicioAutor = CrearServicio(claveAutor, rsa, artefactos, protector);
        servicioAutor.CrearPaquete(entorno.Permisos, "SID=S-1-5-21-1-2-3-1001");

        var rutaPaquete = Path.Combine(
            entorno.Permisos,
            ServicioAprovisionamientoClaveArtefactos.NombrePaquete);
        var paquete = JsonNode.Parse(File.ReadAllText(rutaPaquete, Encoding.UTF8))!.AsObject();
        var protegido = Convert.FromBase64String(paquete["ClaveProtegida"]!.GetValue<string>());
        protegido[0] ^= 0x01;
        paquete["ClaveProtegida"] = Convert.ToBase64String(protegido);
        File.WriteAllText(rutaPaquete, paquete.ToJsonString(), Encoding.UTF8);

        var resultado = CrearServicio(claveCliente, rsa, artefactos, protector)
            .IntentarAprovisionar(entorno.Permisos);

        Assert.Equal(EstadoAprovisionamientoClave.Error, resultado.Estado);
        Assert.False(claveCliente.Existe);
        CryptographicOperations.ZeroMemory(clave);
        CryptographicOperations.ZeroMemory(protegido);
    }

    [Fact]
    public void KeyIdDistintoEnCatalogoImpideElAprovisionamiento()
    {
        using var entorno = EntornoTemporal.Crear();
        using var rsa = RSA.Create(3072);
        var claveCorrecta = RandomNumberGenerator.GetBytes(32);
        var claveDistinta = RandomNumberGenerator.GetBytes(32);
        var claveAutor = new ServicioClaveArtefactos(
            Path.Combine(entorno.Raiz, "autor", "artefactos.key"));
        var claveCliente = new ServicioClaveArtefactos(
            Path.Combine(entorno.Raiz, "cliente", "artefactos.key"));
        ServicioClaveArtefactos.Aprovisionar(
            claveAutor.RutaClave,
            claveCorrecta,
            aplicarAcl: false);
        var artefactosCorrectos = CrearArtefactos(entorno.Permisos, claveCorrecta, rsa);
        var protector = new ProtectorDpapiNgPruebas();
        CrearServicio(claveAutor, rsa, artefactosCorrectos, protector)
            .CrearPaquete(entorno.Permisos, "SID=S-1-5-21-1-2-3-1001");

        var artefactosDistintos = new ServicioArtefactosProtegidos(
            claveDistinta,
            rsa,
            rsa);
        artefactosDistintos.GuardarTextoProtegido(
            Path.Combine(entorno.Permisos, RutasArtefactosProtegidos.NombreCatalogo),
            ServicioArtefactosProtegidos.TipoCatalogoScripts,
            "{\"scripts\":[]}");

        var resultado = CrearServicio(
            claveCliente,
            rsa,
            artefactosCorrectos,
            protector).IntentarAprovisionar(entorno.Permisos);

        Assert.Equal(EstadoAprovisionamientoClave.Error, resultado.Estado);
        Assert.False(claveCliente.Existe);
        CryptographicOperations.ZeroMemory(claveCorrecta);
        CryptographicOperations.ZeroMemory(claveDistinta);
    }

    [Fact]
    public void PaqueteAusenteMantieneElFalloCerrado()
    {
        using var entorno = EntornoTemporal.Crear();
        using var rsa = RSA.Create(3072);
        var claveCliente = new ServicioClaveArtefactos(
            Path.Combine(entorno.Raiz, "cliente", "artefactos.key"));
        var clave = RandomNumberGenerator.GetBytes(32);
        var artefactos = CrearArtefactos(entorno.Permisos, clave, rsa);

        var resultado = CrearServicio(
            claveCliente,
            rsa,
            artefactos,
            new ProtectorDpapiNgPruebas()).IntentarAprovisionar(entorno.Permisos);

        Assert.Equal(EstadoAprovisionamientoClave.PaqueteAusente, resultado.Estado);
        Assert.False(claveCliente.Existe);
        CryptographicOperations.ZeroMemory(clave);
    }

    [Fact]
    public void PaqueteFirmadoActualizaUnaClaveLocalAntigua()
    {
        using var entorno = EntornoTemporal.Crear();
        using var rsa = RSA.Create(3072);
        var claveAntigua = RandomNumberGenerator.GetBytes(32);
        var claveNueva = RandomNumberGenerator.GetBytes(32);
        var claveAutor = new ServicioClaveArtefactos(
            Path.Combine(entorno.Raiz, "autor", "artefactos.key"));
        var claveCliente = new ServicioClaveArtefactos(
            Path.Combine(entorno.Raiz, "cliente", "artefactos.key"));
        ServicioClaveArtefactos.Aprovisionar(
            claveAutor.RutaClave,
            claveNueva,
            aplicarAcl: false);
        ServicioClaveArtefactos.Aprovisionar(
            claveCliente.RutaClave,
            claveAntigua,
            aplicarAcl: false);
        var artefactos = CrearArtefactos(entorno.Permisos, claveNueva, rsa);
        var protector = new ProtectorDpapiNgPruebas();
        CrearServicio(claveAutor, rsa, artefactos, protector)
            .CrearPaquete(entorno.Permisos, "SID=S-1-5-21-1-2-3-1001");

        var resultado = CrearServicio(claveCliente, rsa, artefactos, protector)
            .IntentarAprovisionar(entorno.Permisos);

        Assert.Equal(EstadoAprovisionamientoClave.Actualizada, resultado.Estado);
        using var material = claveCliente.ObtenerMaterial();
        Assert.True(material.Clave.SequenceEqual(claveNueva));
        CryptographicOperations.ZeroMemory(claveAntigua);
        CryptographicOperations.ZeroMemory(claveNueva);
    }

    [Fact]
    public void DescriptorRechazaProteccionLocalOTextoArbitrario()
    {
        ServicioDpapiNg.ValidarDescriptor("SID=S-1-5-21-1-2-3-1001");
        ServicioDpapiNg.ValidarDescriptor(
            "SID=S-1-5-21-1-2-3-1001 OR SID=S-1-5-21-1-2-3-1002");

        Assert.Throws<ArgumentException>(() =>
            ServicioDpapiNg.ValidarDescriptor("LOCAL=machine"));
        Assert.Throws<ArgumentException>(() =>
            ServicioDpapiNg.ValidarDescriptor("SID=../Administrators"));
    }

    [Fact]
    public void ArranqueYHerramientaNoIncluyenLaClaveAes()
    {
        var raiz = ObtenerRaizProyecto();
        var aplicacion = File.ReadAllText(
            Path.Combine(raiz, "Aplicacion.xaml.cs"),
            Encoding.UTF8);
        var herramienta = File.ReadAllText(
            Path.Combine(raiz, "Herramientas", "CrearPaqueteAprovisionamientoClave.ps1"),
            Encoding.UTF8);

        Assert.Contains("IntentarAprovisionarClaveArtefactos", aplicacion, StringComparison.Ordinal);
        Assert.Contains("--descriptor-base64", herramienta, StringComparison.Ordinal);
        Assert.DoesNotContain("ClaveAesBase64", herramienta, StringComparison.Ordinal);
        Assert.DoesNotContain("-ClaveAES", herramienta, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Read-Host", herramienta, StringComparison.Ordinal);
    }

    private static ServicioArtefactosProtegidos CrearArtefactos(
        string carpeta,
        byte[] clave,
        RSA rsa)
    {
        var artefactos = new ServicioArtefactosProtegidos(clave, rsa, rsa);
        artefactos.GuardarTextoProtegido(
            Path.Combine(carpeta, RutasArtefactosProtegidos.NombrePermisos),
            ServicioArtefactosProtegidos.TipoPermisos,
            "{\"usuarios\":[]}");
        artefactos.GuardarTextoProtegido(
            Path.Combine(carpeta, RutasArtefactosProtegidos.NombreCatalogo),
            ServicioArtefactosProtegidos.TipoCatalogoScripts,
            "{\"scripts\":[]}");
        return artefactos;
    }

    private static ServicioAprovisionamientoClaveArtefactos CrearServicio(
        ServicioClaveArtefactos claveLocal,
        RSA rsa,
        ServicioArtefactosProtegidos artefactos,
        IProtectorDpapiNg protector)
    {
        return new ServicioAprovisionamientoClaveArtefactos(
            claveLocal,
            new ServicioFirmaArtefactos(rsa, rsa),
            artefactos,
            protector,
            aplicarAcl: false);
    }

    private static string ObtenerRaizProyecto()
    {
        var carpeta = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(carpeta)
            && !File.Exists(Path.Combine(carpeta, "LanzadorScripts.csproj")))
        {
            carpeta = Directory.GetParent(carpeta)?.FullName ?? string.Empty;
        }

        return carpeta;
    }

    private sealed class ProtectorDpapiNgPruebas : IProtectorDpapiNg
    {
        private const byte Mascara = 0xA5;

        public byte[] Proteger(ReadOnlySpan<byte> datos, string descriptor)
        {
            ServicioDpapiNg.ValidarDescriptor(descriptor);
            return datos.ToArray().Select(valor => (byte)(valor ^ Mascara)).ToArray();
        }

        public byte[] Desproteger(ReadOnlySpan<byte> datosProtegidos)
        {
            return datosProtegidos.ToArray().Select(valor => (byte)(valor ^ Mascara)).ToArray();
        }
    }

    private sealed class EntornoTemporal : IDisposable
    {
        private EntornoTemporal(string raiz)
        {
            Raiz = raiz;
            Permisos = Path.Combine(raiz, "permisos");
            Directory.CreateDirectory(Permisos);
        }

        public string Raiz { get; }

        public string Permisos { get; }

        public static EntornoTemporal Crear()
        {
            return new EntornoTemporal(
                Path.Combine(Path.GetTempPath(), $"LanzadorScriptsAprovisionamiento_{Guid.NewGuid():N}"));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Raiz, recursive: true);
            }
            catch
            {
            }
        }
    }
}
