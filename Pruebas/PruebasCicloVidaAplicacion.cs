// (Autor: Alex Roman)
// Descripcion: Comprueba el ciclo de vida, la bandeja y la limpieza de runtimes.

using LanzadorScripts.Servicios;
using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasCicloVidaAplicacion
{
    [Fact]
    public void ProtocoloInstanciaAdmiteMostrarEImportar()
    {
        var mostrar = new MensajeInstanciaAplicacion(AccionInstanciaAplicacion.Mostrar);
        var importar = new MensajeInstanciaAplicacion(
            AccionInstanciaAplicacion.ImportarPaquete,
            @"C:\Paquetes\config.lsconfig");

        Assert.True(ProtocoloInstanciaAplicacion.IntentarDeserializar(
            ProtocoloInstanciaAplicacion.Serializar(mostrar),
            out var mostrarLeido));
        Assert.Equal(AccionInstanciaAplicacion.Mostrar, mostrarLeido?.Accion);
        Assert.True(ProtocoloInstanciaAplicacion.IntentarDeserializar(
            ProtocoloInstanciaAplicacion.Serializar(importar),
            out var importarLeido));
        Assert.Equal(importar.Ruta, importarLeido?.Ruta);
    }

    [Fact]
    public void ProtocoloInstanciaRechazaMensajesInvalidos()
    {
        var sobredimensionado = new string('x', ProtocoloInstanciaAplicacion.LongitudMaximaMensaje + 1);

        Assert.False(ProtocoloInstanciaAplicacion.IntentarDeserializar("", out _));
        Assert.False(ProtocoloInstanciaAplicacion.IntentarDeserializar("{", out _));
        Assert.False(ProtocoloInstanciaAplicacion.IntentarDeserializar(sobredimensionado, out _));
    }

    [Fact]
    public void VentanaArrancaDespuesDeMostrarseYSeOcultaAlCerrar()
    {
        var ventana = File.ReadAllText(ObtenerRutaProyecto("VentanaPrincipal.xaml.cs"));

        Assert.Contains("protected override void OnContentRendered", ventana, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.ContextIdle", ventana, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true;", ventana, StringComparison.Ordinal);
        Assert.Contains("OcultarEnSegundoPlano();", ventana, StringComparison.Ordinal);
        Assert.Contains("ShowInTaskbar = false;", ventana, StringComparison.Ordinal);
        Assert.Contains("SolicitarCierreDesdeBandeja", ventana, StringComparison.Ordinal);
        Assert.Contains("ObtenerEjecucionesActivas()", ventana, StringComparison.Ordinal);
        Assert.Contains("while (ejecuciones.Count > 0)", ventana, StringComparison.Ordinal);
        Assert.Contains("Shutdown(CodigoSalidaCierreDefinitivo)", ventana, StringComparison.Ordinal);

        var dialogo = File.ReadAllText(ObtenerRutaProyecto("DialogoCerrarAplicacion.xaml"));
        Assert.DoesNotContain("TextoSinEjecuciones", dialogo, StringComparison.Ordinal);
    }

    [Fact]
    public void InstanciaSecundariaSiempreSolicitaMostrar()
    {
        var aplicacion = File.ReadAllText(ObtenerRutaProyecto("Aplicacion.xaml.cs"));

        Assert.Contains("new(AccionInstanciaAplicacion.Mostrar)", aplicacion, StringComparison.Ordinal);
        Assert.Contains("$\"{PrefijoPipe}_{sufijoDistribucion}\"", aplicacion, StringComparison.Ordinal);
        Assert.Contains("$\"{PrefijoMutex}_{sufijoDistribucion}\"", aplicacion, StringComparison.Ordinal);
        Assert.Contains("PipeOptions.CurrentUserOnly", aplicacion, StringComparison.Ordinal);
        Assert.Contains("LeerMensajeLimitadoAsync", aplicacion, StringComparison.Ordinal);
        Assert.Contains("MostrarDesdeInstanciaSecundaria", aplicacion, StringComparison.Ordinal);
    }

    [Fact]
    public void LanzadorPortableAislaYLimpiaCadaSesion()
    {
        var nativo = File.ReadAllText(ObtenerRutaProyecto("LanzadorNativo", "LanzadorNativo.cpp"));
        var publicacion = File.ReadAllText(ObtenerRutaProyecto("Herramientas", "PublicarPortable.ps1"));

        Assert.Contains("constexpr DWORD CodigoCierreDefinitivo = 42", nativo, StringComparison.Ordinal);
        Assert.DoesNotContain("DialogoProgreso", nativo, StringComparison.Ordinal);
        Assert.DoesNotContain("IProgressDialog", nativo, StringComparison.Ordinal);
        Assert.DoesNotContain("TieneVentanaVisible", nativo, StringComparison.Ordinal);
        Assert.Contains("CREATE_NO_WINDOW", nativo, StringComparison.Ordinal);
        Assert.Contains("WaitForSingleObject(proceso.hProcess, INFINITE)", nativo, StringComparison.Ordinal);
        Assert.Contains("EliminarArbolSeguroConReintentos", nativo, StringComparison.Ordinal);
        Assert.Contains("FILE_ATTRIBUTE_REPARSE_POINT", nativo, StringComparison.Ordinal);
        Assert.Contains("HayProcesoEnRuta", nativo, StringComparison.Ordinal);
        Assert.Contains("LANZADOR_PORTABLE_ROOT", nativo, StringComparison.Ordinal);
        Assert.Contains("LANZADOR_PORTABLE_SESSIONS_ROOT", nativo, StringComparison.Ordinal);
        Assert.Contains("Sesion-", nativo, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts_Portable-1.7.1-x64.exe", publicacion, StringComparison.Ordinal);

        var tokens = File.ReadAllText(ObtenerRutaProyecto("Servicios", "ServicioTokensAdmin.cs"));
        Assert.Contains("RutasAplicacion.Distribucion.EsPortable", tokens, StringComparison.Ordinal);
    }

    [Fact]
    public void WebView2ConservaSoloElRuntimeActual()
    {
        var servicio = File.ReadAllText(
            ObtenerRutaProyecto("Servicios", "ServicioRuntimeWebView2Embebido.cs"));

        Assert.Contains("MaximoVersionesConservadas = 1", servicio, StringComparison.Ordinal);
    }

    [Fact]
    public void LogDeArranqueNoCapturaElContextoVisual()
    {
        var servicio = File.ReadAllText(
            ObtenerRutaProyecto("Servicios", "ServicioLogInicio.cs"));

        Assert.Contains(
            "await Bloqueo.WaitAsync().ConfigureAwait(false)",
            servicio,
            StringComparison.Ordinal);
        Assert.Contains(
            ".ConfigureAwait(false);",
            servicio,
            StringComparison.Ordinal);
    }

    private static string ObtenerRutaProyecto(params string[] partes)
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null)
        {
            if (File.Exists(Path.Combine(directorio.FullName, "LanzadorScripts.csproj")))
            {
                return Path.Combine([directorio.FullName, .. partes]);
            }

            directorio = directorio.Parent;
        }

        throw new DirectoryNotFoundException("No se encontro la raiz del proyecto LanzadorScripts.");
    }
}
