// (Autor: Alex Roman)
// Descripcion: Comprueba el desbloqueo visual y los diagnosticos del cliente WebView2.

using LanzadorScripts.Servicios;
using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasDiagnosticoClienteWeb
{
    [Fact]
    public void NavegacionSoloHabilitaLaInterfazParaElBackendActual()
    {
        var codigo = LeerVentanaPrincipal();

        Assert.Contains("e.NavigationId == _idNavegacionActual", codigo, StringComparison.Ordinal);
        Assert.Contains("EsOrigenBackendEsperado(_origenNavegacionActual)", codigo, StringComparison.Ordinal);
        Assert.Contains("PanelArranque.IsHitTestVisible = false", codigo, StringComparison.Ordinal);
        Assert.Contains("cliente.navegacion.correcta", codigo, StringComparison.Ordinal);
        Assert.Contains("cliente.navegacion.error", codigo, StringComparison.Ordinal);
        Assert.Contains("duracionMs", codigo, StringComparison.Ordinal);
    }

    [Fact]
    public void ErroresJavaScriptSeEnvianDeFormaLimitadaAlLogLocal()
    {
        var codigo = LeerVentanaPrincipal();

        Assert.Contains("ObtenerDiagnosticoErroresCliente()", codigo, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener('error'", codigo, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener('unhandledrejection'", codigo, StringComparison.Ordinal);
        Assert.Contains("const maximoErrores = 10", codigo, StringComparison.Ordinal);
        Assert.Contains("cliente.javascript_error", codigo, StringComparison.Ordinal);
    }

    [Fact]
    public void ReintentoNoDuplicaScriptsNiObservadoresDelDocumento()
    {
        var codigo = LeerVentanaPrincipal();

        Assert.Contains("!ReferenceEquals(_webViewConfigurada, coreWebView2)", codigo, StringComparison.Ordinal);
        Assert.Contains("_webViewConfigurada = coreWebView2", codigo, StringComparison.Ordinal);
        Assert.Contains("NavigationStarting -= VistaCliente_NavigationStarting", codigo, StringComparison.Ordinal);
        Assert.Contains("NavigationCompleted -= VistaCliente_NavigationCompleted", codigo, StringComparison.Ordinal);
    }

    [Fact]
    public void AvisoVisualUsaLaCausaEnviadaPorElBackend()
    {
        var codigo = LeerVentanaPrincipal();

        Assert.Contains("ultimoAvisoConexion = datos.avisoConexion", codigo, StringComparison.Ordinal);
        Assert.Contains("function actualizarAvisoConexion()", codigo, StringComparison.Ordinal);
        Assert.Contains("aviso.textContent !== ultimoAvisoConexion", codigo, StringComparison.Ordinal);
        Assert.Contains("El backend local no pudo procesar la solicitud.", codigo, StringComparison.Ordinal);
    }

    [Fact]
    public void AjustesMuestraCargaYErrorAntesDeCambiarDeVista()
    {
        var codigo = LeerVentanaPrincipal();

        Assert.Contains("ObtenerEstadoCargaAjustes()", codigo, StringComparison.Ordinal);
        Assert.Contains("Abriendo Configuración Avanzada...", codigo, StringComparison.Ordinal);
        Assert.Contains("Comprobando el servidor central y la carpeta de scripts", codigo, StringComparison.Ordinal);
        Assert.Contains("No se pudo abrir Ajustes a tiempo", codigo, StringComparison.Ordinal);
        Assert.Contains("rutasPendientes = new Set(rutasEsperadas)", codigo, StringComparison.Ordinal);
        Assert.Contains("respuesta.clone().json()", codigo, StringComparison.Ordinal);
    }

    [Fact]
    public void ClienteMuestraLaVersionRealDelEjecutable()
    {
        var carpetaAssets = Path.Combine(ObtenerRaizProyecto(), "ClienteWeb", "assets");
        var rutaBundle = Directory.GetFiles(carpetaAssets, "index-*.js").Single();
        var bundle = File.ReadAllText(rutaBundle);

        var versionado = ServidorLocalWeb.AplicarVersionVisualCliente(bundle, new Version(9, 8, 7, 6));

        Assert.Contains("children:\"v9.8.7\"", versionado, StringComparison.Ordinal);
        Assert.DoesNotContain("children:\"v1.2.0\"", versionado, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServidorEntregaElBundleConLaVersionReal()
    {
        using var entorno = EntornoPruebas.Crear();
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(entorno.CrearConfiguracion());
        using var cliente = new HttpClient { BaseAddress = servidor.UrlBase };
        var nombreBundle = Path.GetFileName(
            Directory.GetFiles(Path.Combine(ObtenerRaizProyecto(), "ClienteWeb", "assets"), "index-*.js").Single());

        var respuesta = await cliente.GetAsync($"/assets/{nombreBundle}");
        respuesta.EnsureSuccessStatusCode();
        var contenido = await respuesta.Content.ReadAsStringAsync();
        var version = typeof(ServidorLocalWeb).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var etiquetaEsperada = $"children:\"v{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}\"";

        Assert.Contains(etiquetaEsperada, contenido, StringComparison.Ordinal);
        Assert.DoesNotContain("children:\"v1.2.0\"", contenido, StringComparison.Ordinal);
    }

    private static string LeerVentanaPrincipal()
    {
        return File.ReadAllText(Path.Combine(ObtenerRaizProyecto(), "VentanaPrincipal.xaml.cs"));
    }

    private static string ObtenerRaizProyecto()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null)
        {
            if (File.Exists(Path.Combine(directorio.FullName, "LanzadorScripts.csproj")))
            {
                return directorio.FullName;
            }

            directorio = directorio.Parent;
        }

        throw new DirectoryNotFoundException("No se encontro la raiz del proyecto LanzadorScripts.");
    }
}
