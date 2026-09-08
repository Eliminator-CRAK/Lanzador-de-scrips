// (Autor: Alex Roman)
// Descripcion: Comprueba privacidad, exportacion OTLP y aislamiento de fallos del monitor.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using LanzadorScripts.Monitorizacion;
using LanzadorScripts.Protocolo;
using Xunit;

namespace LanzadorScripts.Pruebas;

[CollectionDefinition("Monitorizacion", DisableParallelization = true)]
public sealed class ColeccionMonitorizacion { }

[Collection("Monitorizacion")]
public sealed class PruebasMonitorizacion
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("error", false)]
    public void ActivacionExplicitaSinInterrumpirAplicacion(string? valor, bool habilitada)
    {
        var config = ConfiguracionMonitorizacion.Leer(nombre =>
            nombre == "LANZADOR_MONITORIZACION_HABILITADA" ? valor : null);
        Assert.Equal(habilitada, config.Habilitada);
        Assert.Equal(ConfiguracionMonitorizacion.DestinoGitLab, config.Destino.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://destino.test/")]
    [InlineData("https://usuario:clave@destino.test/")]
    [InlineData("https://destino.test/?token=privado")]
    [InlineData("https://destino.test/#privado")]
    [InlineData("https://destino.test/v1/traces")]
    [InlineData("file:///C:/secreto")]
    [InlineData("https://127.0.0.1/")]
    public void RechazaDestinoInseguro(string destino)
    {
        Assert.False(ConfiguracionMonitorizacion.Leer(nombre =>
            nombre == "LANZADOR_MONITORIZACION_ENDPOINT" ? destino : null).Habilitada);
    }

    [Fact]
    public void NoActivaPersistenciaOtelEnDisco()
    {
        Assert.False(ConfiguracionMonitorizacion.Leer(nombre =>
            nombre == "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY" ? "disk" : null).Habilitada);
    }

    [Fact]
    public void TodasLasOperacionesDelProtocoloTienenNombreAcotado()
    {
        foreach (var campo in typeof(OperacionesServidor).GetFields())
        {
            var nombre = (string)campo.GetRawConstantValue()!;
            Assert.Equal(nombre, MonitorizacionAplicacion.NormalizarOperacion(nombre));
        }
        Assert.Equal("operacion.desconocida", MonitorizacionAplicacion.NormalizarOperacion("DOMINIO\\usuario"));
        Assert.Equal("operacion.desconocida", MonitorizacionAplicacion.NormalizarOperacion(new string('x', 20000)));
    }

    [Fact]
    public void ExportaTresSenalesSinIdentidadContenidoNiCredenciales()
    {
        using var receptor = new ReceptorOtlp();
        using var monitor = Crear(receptor);
        Assert.True(monitor.Habilitada);
        using (var medicion = monitor.Medir("webview2.inicio")) medicion.Completar();
        using (monitor.Medir("DOMINIO\\usuario C:\\secreto.ps1 token=privado")) { }
        Assert.True(monitor.Vaciar());
        Assert.Contains(receptor.Envios, e => e.Ruta == "/v1/traces");
        Assert.Contains(receptor.Envios, e => e.Ruta == "/v1/metrics");
        Assert.Contains(receptor.Envios, e => e.Ruta == "/v1/logs");
        var contenido = string.Join("\n", receptor.Envios.Select(e => Encoding.UTF8.GetString(e.Cuerpo)));
        Assert.Contains("LanzadorScripts.Cliente", contenido);
        Assert.Contains("84894342", contenido);
        Assert.Contains("webview2.inicio", contenido);
        Assert.Contains("operacion.desconocida", contenido);
        Assert.Contains("lanzador.operacion.duracion", contenido);
        Assert.DoesNotContain("DOMINIO", contenido);
        Assert.DoesNotContain("secreto", contenido);
        Assert.DoesNotContain("privado", contenido);
        Assert.DoesNotContain("host.name", contenido);
        Assert.DoesNotContain("process.command_line", contenido);
        Assert.All(receptor.Envios, e => Assert.Equal("application/x-protobuf", e.Tipo));
        Assert.All(receptor.Envios, e => Assert.False(e.Autorizacion));
    }

    [Fact]
    public void DesactivadaNoCreaExportadoresNiSolicitudes()
    {
        var creados = 0;
        using var monitor = MonitorizacionAplicacion.Crear(ComponenteMonitorizado.ClientePortable,
            new(false, new Uri(ConfiguracionMonitorizacion.DestinoGitLab), "test"),
            () => { creados++; throw new InvalidOperationException(); });
        using (var medicion = monitor.Medir("aplicacion.inicio")) medicion.Completar();
        Assert.False(monitor.Habilitada);
        Assert.True(monitor.Vaciar());
        Assert.Equal(0, creados);
    }

    [Fact]
    public void NoHeredaAtributosNiHeadersOtelDelEntorno()
    {
        const string atributos = "OTEL_RESOURCE_ATTRIBUTES";
        const string headers = "OTEL_EXPORTER_OTLP_HEADERS";
        var anteriores = new[] { Environment.GetEnvironmentVariable(atributos), Environment.GetEnvironmentVariable(headers) };
        try
        {
            Environment.SetEnvironmentVariable(atributos, "user.name=privado,host.name=privado");
            Environment.SetEnvironmentVariable(headers, "Authorization=Bearer fixture");
            using var receptor = new ReceptorOtlp();
            using var monitor = Crear(receptor);
            using (monitor.Medir("monitorizacion.prueba")) { }
            Assert.True(monitor.Vaciar());
            Assert.NotEmpty(receptor.Envios);
            Assert.All(receptor.Envios, e =>
            {
                Assert.DoesNotContain("privado", Encoding.UTF8.GetString(e.Cuerpo));
                Assert.False(e.Autorizacion);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(atributos, anteriores[0]);
            Environment.SetEnvironmentVariable(headers, anteriores[1]);
        }
    }

    [Fact]
    public void FalloCreandoExportadorNoImpideTrabajoNiCierre()
    {
        using var monitor = MonitorizacionAplicacion.Crear(ComponenteMonitorizado.ServicioServidor,
            new(true, new Uri(ConfiguracionMonitorizacion.DestinoGitLab), "test"),
            () => throw new InvalidOperationException("Error de prueba"));
        Assert.False(monitor.Habilitada);
        using var medicion = monitor.Medir("salud");
        medicion.Completar();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public void FalloRemotoNoInterrumpeOperacionesYCierreEsAcotado(HttpStatusCode estado)
    {
        using var receptor = new ReceptorOtlp(estado);
        var monitor = Crear(receptor);
        using (var medicion = monitor.Medir("salud")) medicion.Completar();
        var reloj = Stopwatch.StartNew();
        monitor.Vaciar(300);
        monitor.Dispose();
        monitor.Dispose();
        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void OperacionesConcurrentesNoPropaganExcepciones()
    {
        using var receptor = new ReceptorOtlp();
        using var monitor = Crear(receptor);
        Parallel.For(0, 300, _ =>
        {
            using var medicion = monitor.Medir("salud");
            medicion.Completar();
        });
        Assert.True(monitor.Vaciar());
        Assert.NotEmpty(receptor.Envios);
    }

    private static MonitorizacionAplicacion Crear(ReceptorOtlp receptor) =>
        MonitorizacionAplicacion.Crear(ComponenteMonitorizado.ClienteInstalado,
            new(true, new Uri("https://receptor.test/"), "test"),
            () => new HttpClient(receptor, disposeHandler: false));

    private sealed record Envio(string Ruta, byte[] Cuerpo, string? Tipo, bool Autorizacion);

    private sealed class ReceptorOtlp(HttpStatusCode estado = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal ConcurrentQueue<Envio> Envios { get; } = new();

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var cuerpo = new MemoryStream();
            request.Content!.CopyTo(cuerpo, null, cancellationToken);
            Envios.Enqueue(new(request.RequestUri!.AbsolutePath, cuerpo.ToArray(),
                request.Content.Headers.ContentType?.MediaType, request.Headers.Authorization is not null));
            return new(estado) { Content = new ByteArrayContent([]) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }
}
