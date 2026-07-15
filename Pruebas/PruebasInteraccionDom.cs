// (Autor: Alex Roman)
// Descripcion: Pruebas de regresion para las mejoras DOM inyectadas en WebView2.

using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasInteraccionDom
{
    [Fact]
    public void ObservadorInterfazAgrupaMutacionesYSeDesconectaAlActualizar()
    {
        var codigo = LeerVentanaPrincipal();
        var script = ExtraerSeccion(codigo, "private static string ObtenerMejorasInterfazScripts()", "private static string ObtenerPanelPermisosSubcarpetas()");

        Assert.Contains("new MutationObserver(programarActualizacionInterfaz)", script, StringComparison.Ordinal);
        Assert.Contains("frameActualizacionInterfaz !== null", script, StringComparison.Ordinal);
        Assert.Contains("window.requestAnimationFrame(aplicarActualizacionInterfaz)", script, StringComparison.Ordinal);
        Assert.Contains("observadorInterfaz?.disconnect();", script, StringComparison.Ordinal);
        Assert.Contains("observadorInterfaz.observe(document.body, opcionesObservadorInterfaz);", script, StringComparison.Ordinal);
        Assert.DoesNotContain("new MutationObserver(() =>", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ContenedorScriptsSeLocalizaDesdeElBuscadorSinDependerDeTarjetas()
    {
        var codigo = LeerVentanaPrincipal();
        var script = ExtraerSeccion(codigo, "function obtenerContenedorScripts()", "function obtenerTituloTarjeta(tarjeta)");

        Assert.Contains("input[placeholder=\"Buscar scripts...\"]", script, StringComparison.Ordinal);
        Assert.Contains("buscador?.closest('aside')", script, StringComparison.Ordinal);
        Assert.Contains("Array.from(panelScripts.children)", script, StringComparison.Ordinal);
        Assert.Contains("hijo.classList.contains('custom-scrollbar')", script, StringComparison.Ordinal);
        Assert.Contains("hijo.classList.contains('flex-1')", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ejecutar script", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abrir carpeta", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NavegacionCarpetasSeCreaUnaVezYActualizaSoloValores()
    {
        var codigo = LeerVentanaPrincipal();
        var script = ExtraerSeccion(codigo, "function crearNavegacionCarpetas(contenedor)", "function aplicarVistaCarpetasScripts()");

        Assert.Contains("if (!panel)", script, StringComparison.Ordinal);
        Assert.Contains("document.createElement('button')", script, StringComparison.Ordinal);
        Assert.Contains("botonVolver.textContent = '← Volver'", script, StringComparison.Ordinal);
        Assert.Contains("botonRaiz.textContent = 'Principal'", script, StringComparison.Ordinal);
        Assert.Contains("panel.dataset.lsCarpetaActual !== carpeta", script, StringComparison.Ordinal);
        Assert.Contains("ruta.textContent !== textoRuta", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PanelNavegacionNoSeComprimeDentroDelLateral()
    {
        var codigo = LeerVentanaPrincipal();
        var script = ExtraerSeccion(codigo, "private static string ObtenerMejorasInterfazScripts()", "private static string ObtenerPanelPermisosSubcarpetas()");

        Assert.Contains(".ls-navegacion-carpetas {", script, StringComparison.Ordinal);
        Assert.Contains("flex: 0 0 auto;", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TarjetaCarpetaSeMarcaAntesDeModificarSuContenido()
    {
        var codigo = LeerVentanaPrincipal();
        var script = ExtraerSeccion(codigo, "function aplicarVistaCarpetasScripts()", "function observarCambiosInterfaz()");
        var indiceMarca = script.IndexOf("tarjeta.dataset.lsCarpetaPreparada = '1';", StringComparison.Ordinal);
        var indiceClase = script.IndexOf("tarjeta.classList.add('ls-tarjeta-carpeta');", StringComparison.Ordinal);

        Assert.True(indiceMarca >= 0);
        Assert.True(indiceClase > indiceMarca);
        Assert.Contains("tarjeta.dataset.lsCarpetaPreparada === '1'", script, StringComparison.Ordinal);
        Assert.Contains("!tarjeta.classList.contains('ls-tarjeta-carpeta')", script, StringComparison.Ordinal);
        Assert.Contains("boton.textContent !== 'Abrir carpeta'", script, StringComparison.Ordinal);
        Assert.Contains("boton.title !== tituloBoton", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservadorConfiguracionActualizaLaEtiquetaDeFormaIdempotente()
    {
        var codigo = LeerVentanaPrincipal();
        var script = ExtraerSeccion(codigo, "private static string ObtenerAvisosConfiguracionApp()", "private static string ObtenerExportacionConfiguracionGestionada()");

        Assert.Contains("new MutationObserver(programarActualizacionConfiguracion)", script, StringComparison.Ordinal);
        Assert.Contains("window.requestAnimationFrame(aplicarActualizacionConfiguracion)", script, StringComparison.Ordinal);
        Assert.Contains("observadorConfiguracion?.disconnect();", script, StringComparison.Ordinal);
        Assert.Contains("observadorConfiguracion.observe(document.body, opcionesObservadorConfiguracion);", script, StringComparison.Ordinal);
        Assert.Contains("etiqueta.textContent !== textoEtiqueta", script, StringComparison.Ordinal);
        Assert.Contains("entrada.placeholder !== placeholder", script, StringComparison.Ordinal);
        Assert.Contains("ayuda.textContent !== textoAyuda", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservadorSubcarpetasAgrupaMutacionesYSeDesconectaAlActualizar()
    {
        var codigo = LeerVentanaPrincipal();
        var script = ExtraerSeccion(codigo, "private static string ObtenerPanelPermisosSubcarpetas()", "private static string ObtenerAvisosConfiguracionApp()");

        Assert.Contains("new MutationObserver(programarActualizacionSubcarpetas)", script, StringComparison.Ordinal);
        Assert.Contains("frameActualizacionSubcarpetas !== null", script, StringComparison.Ordinal);
        Assert.Contains("window.requestAnimationFrame(aplicarActualizacionSubcarpetas)", script, StringComparison.Ordinal);
        Assert.Contains("observadorSubcarpetas?.disconnect();", script, StringComparison.Ordinal);
        Assert.Contains("observadorSubcarpetas.observe(document.body, opcionesObservadorSubcarpetas);", script, StringComparison.Ordinal);
        Assert.DoesNotContain("new MutationObserver(crearPanel)", script, StringComparison.Ordinal);
    }

    private static string LeerVentanaPrincipal()
    {
        return File.ReadAllText(Path.Combine(ObtenerRaizProyecto(), "VentanaPrincipal.xaml.cs"));
    }

    private static string ExtraerSeccion(string codigo, string inicio, string fin)
    {
        var indiceInicio = codigo.IndexOf(inicio, StringComparison.Ordinal);
        var indiceFin = codigo.IndexOf(fin, indiceInicio + inicio.Length, StringComparison.Ordinal);
        Assert.True(indiceInicio >= 0, $"No se encontro el inicio: {inicio}");
        Assert.True(indiceFin > indiceInicio, $"No se encontro el final: {fin}");
        return codigo[indiceInicio..indiceFin];
    }

    private static string ObtenerRaizProyecto()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null)
        {
            if (File.Exists(Path.Combine(directorio.FullName, "manifiesto.manifest")))
            {
                return directorio.FullName;
            }

            directorio = directorio.Parent;
        }

        throw new DirectoryNotFoundException("No se encontro la raiz del proyecto.");
    }
}
