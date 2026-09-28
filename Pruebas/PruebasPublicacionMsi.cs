// (Autor: Alex Roman)
// Descripcion: Prueba el staging, la inmutabilidad y la validacion de los paquetes publicados.

using System.Security.Cryptography;
using LanzadorScripts.Protocolo;
using LanzadorScripts.Servidor.Core;
using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasPublicacionMsi : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "LanzadorScripts-publicacion-test", Guid.NewGuid().ToString("N"));
    private PublicadorActualizacionesServidor CrearPublicador() => new(Path.Combine(_raiz, "staging"), Path.Combine(_raiz, "publicados"), ruta =>
    {
        var archivo = new FileInfo(ruta);
        using var flujo = File.OpenRead(ruta);
        return new(true, archivo.Name, new Version(1, 10, 0), archivo.Length, Convert.ToHexString(SHA256.HashData(flujo)),
            archivo.LastWriteTimeUtc, "Firma simulada", "");
    });

    private string CrearOrigen()
    {
        Directory.CreateDirectory(_raiz);
        var ruta = Path.Combine(_raiz, "LanzadorScripts-1.10.0-x64.msi");
        File.WriteAllBytes(ruta, [1, 2, 3, 4]);
        return ruta;
    }

    [Fact]
    public void PublicarEsAtomicoEIdempotenteYNoSustituyeUnaVersionDistinta()
    {
        var origen = CrearOrigen();
        var publicador = CrearPublicador();
        var preparado = publicador.Preparar(origen);
        Assert.False(Directory.Exists(Path.Combine(_raiz, "publicados")));
        Assert.Equal(preparado.Paquete.Sha256, publicador.Publicar(preparado.Solicitud).Sha256);
        publicador.Descartar(preparado.Solicitud);
        var repetido = publicador.Preparar(origen);
        Assert.Equal(preparado.Paquete.Sha256, publicador.Publicar(repetido.Solicitud).Sha256);
        publicador.Descartar(repetido.Solicitud);
        File.WriteAllBytes(origen, [9, 8, 7, 6]);
        var diferente = publicador.Preparar(origen);
        Assert.Throws<InvalidDataException>(() => publicador.Publicar(diferente.Solicitud));
        publicador.Descartar(diferente.Solicitud);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(_raiz, "publicados", Path.GetFileName(origen))));
    }

    [Fact]
    public void PublicarRechazaAlteracionPosteriorALaConfirmacion()
    {
        var publicador = CrearPublicador();
        var preparado = publicador.Preparar(CrearOrigen());
        File.AppendAllText(Path.Combine(_raiz, "staging", preparado.Solicitud.PreparacionId.ToString("N"), preparado.Solicitud.NombreArchivo), "alterado");
        Assert.Throws<InvalidDataException>(() => publicador.Publicar(preparado.Solicitud));
    }

    [Fact]
    public void PrepararNoLeeUnMsiQueSeEstaEscribiendo()
    {
        var origen = CrearOrigen();
        using var bloqueo = new FileStream(origen, FileMode.Open, FileAccess.Write, FileShare.None);
        Assert.Throws<IOException>(() => CrearPublicador().Preparar(origen));
    }

    [Theory]
    [InlineData("../LanzadorScripts-1.10.0-x64.msi")]
    [InlineData(@"..\LanzadorScripts-1.10.0-x64.msi")]
    [InlineData(@"C:\LanzadorScripts-1.10.0-x64.msi")]
    [InlineData("LanzadorScripts-1.10.0-x64.msi:stream")]
    public void PublicarRechazaRutasArbitrarias(string nombre)
    {
        Assert.Throws<InvalidDataException>(() => CrearPublicador().Publicar(new(Guid.NewGuid(), nombre, "")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
    }
}
