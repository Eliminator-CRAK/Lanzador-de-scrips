// (Autor: Alex Roman)
// Descripcion: Comprueba el arranque en segundo plano y los avisos de rutas remotas.

using LanzadorScripts.Servicios;
using System.Text.Json.Nodes;
using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasDiagnosticoArranque
{
    [Fact]
    public async Task RuntimeSePreparaFueraDelHiloLlamador()
    {
        var hiloLlamador = 0;
        var hiloPreparacion = 0;
        Task<ResultadoRuntimeWebView2Embebido>? preparacion = null;
        var hilo = new Thread(() =>
        {
            hiloLlamador = Environment.CurrentManagedThreadId;
            preparacion = ServicioArranqueWebView2.PrepararRuntimeEnSegundoPlanoAsync(() =>
            {
                hiloPreparacion = Environment.CurrentManagedThreadId;
                return ResultadoRuntimeWebView2Embebido.NoDisponible("Prueba sin recurso.");
            });
        });

        hilo.Start();
        Assert.True(hilo.Join(TimeSpan.FromSeconds(5)));
        var resultado = await Assert.IsType<Task<ResultadoRuntimeWebView2Embebido>>(preparacion);

        Assert.False(resultado.Exito);
        Assert.NotEqual(hiloLlamador, hiloPreparacion);
    }

    [Fact]
    public void AvisosDiferencianBackendPermisosYScripts()
    {
        var avisoPermisos = ServidorLocalWeb.CrearAvisoConexion(permisosInaccesibles: true, scriptsInaccesibles: false);
        var avisoScripts = ServidorLocalWeb.CrearAvisoConexion(permisosInaccesibles: false, scriptsInaccesibles: true);
        var avisoAmbos = ServidorLocalWeb.CrearAvisoConexion(permisosInaccesibles: true, scriptsInaccesibles: true);

        Assert.Equal(ServidorLocalWeb.MensajeCarpetaPermisosNoDisponible, avisoPermisos);
        Assert.Equal(ServidorLocalWeb.MensajeCarpetaScriptsNoDisponible, avisoScripts);
        Assert.Contains(ServidorLocalWeb.MensajeCarpetaPermisosNoDisponible, avisoAmbos, StringComparison.Ordinal);
        Assert.Contains(ServidorLocalWeb.MensajeCarpetaScriptsNoDisponible, avisoAmbos, StringComparison.Ordinal);
        Assert.NotEqual(ServidorLocalWeb.MensajeBackendLocalNoDisponible, avisoPermisos);
        Assert.NotEqual(ServidorLocalWeb.MensajeBackendLocalNoDisponible, avisoScripts);
    }

    [Fact]
    public void ErrorBackendIncluyeRutaYDetalleSaneado()
    {
        var excepcion = new InvalidOperationException("No se pudo leer permisos.json con token=abc123.");

        var mensaje = ServidorLocalWeb.CrearMensajeErrorBackend("/api/ajustes", excepcion);

        Assert.Contains(ServidorLocalWeb.MensajeBackendLocalNoDisponible, mensaje, StringComparison.Ordinal);
        Assert.Contains("/api/ajustes", mensaje, StringComparison.Ordinal);
        Assert.Contains("No se pudo leer permisos.json", mensaje, StringComparison.Ordinal);
        Assert.Contains("token=[oculto]", mensaje, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", mensaje, StringComparison.Ordinal);
    }

    [Fact]
    public void AjustesPriorizanElEndpointVisibleDelServidorCentral()
    {
        var cuerpo = new JsonObject
        {
            ["rutaPermisos"] = "servidor-nuevo:49000",
            ["servidorCentral"] = "servidor-antiguo",
            ["puertoServidorCentral"] = 47831
        };

        var endpoint = ServidorLocalWeb.SeleccionarEndpointCentral(
            cuerpo,
            "servidor-nuevo:49000",
            "servidor-antiguo",
            47831);

        Assert.Equal("servidor-nuevo:49000", endpoint);
    }

    [Fact]
    public void DiagnosticoScriptsNoCreaLaCarpetaConfigurada()
    {
        var carpeta = Path.Combine(Path.GetTempPath(), "LanzadorScripts_Diagnostico_" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(ServidorLocalWeb.RutaScriptsInaccesible(carpeta));
            Assert.False(Directory.Exists(carpeta));

            Directory.CreateDirectory(carpeta);
            Assert.False(ServidorLocalWeb.RutaScriptsInaccesible(carpeta));
        }
        finally
        {
            if (Directory.Exists(carpeta))
            {
                Directory.Delete(carpeta, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DiagnosticoScriptsAsincronoMantieneLaCarpetaSinCambios()
    {
        var carpeta = Path.Combine(Path.GetTempPath(), "LanzadorScripts_DiagnosticoAsync_" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(await ServidorLocalWeb.RutaScriptsInaccesibleAsync(carpeta));
            Assert.False(Directory.Exists(carpeta));
        }
        finally
        {
            if (Directory.Exists(carpeta))
            {
                Directory.Delete(carpeta, recursive: true);
            }
        }
    }

    [Fact]
    public void AperturaAjustesAgrupaYLimitaLaLecturaRemota()
    {
        var codigo = File.ReadAllText(Path.Combine(ObtenerRaizProyecto(), "Servicios", "ServidorLocalWeb.cs"));

        Assert.Contains("_tareaDiagnosticoAjustes ??= Task.Run(ObtenerDiagnosticoPermisos)", codigo, StringComparison.Ordinal);
        Assert.Contains("Task.WhenAny(tarea, Task.Delay(TimeSpan.FromSeconds(2)))", codigo, StringComparison.Ordinal);
        Assert.Contains("_diagnosticoAjustesValidoHasta = DateTimeOffset.UtcNow.AddSeconds(2)", codigo, StringComparison.Ordinal);
        Assert.Contains("return CrearDiagnosticoPermisosNoDisponible();", codigo, StringComparison.Ordinal);
        Assert.DoesNotContain("_ultimoDiagnosticoPermisosDisponible", codigo, StringComparison.Ordinal);
        Assert.DoesNotContain("ObtenerDiagnosticoPermisosFallback()", codigo, StringComparison.Ordinal);
        Assert.DoesNotContain("_diagnosticoAjustesAgotoEspera", codigo, StringComparison.Ordinal);
        Assert.Contains("Evita consultar la red durante una sesion de emergencia activa", codigo, StringComparison.Ordinal);
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
