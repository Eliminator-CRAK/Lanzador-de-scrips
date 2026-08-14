// (Autor: Alex Roman)
// Descripcion: Valida el aislamiento de distribuciones y la auditoria remota.

using System.Security.Principal;
using System.Text.Json;
using LanzadorScripts.Servicios;
using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasDistribucionAuditoria
{
    [Fact]
    public void ContextoDistingueInstaladaYPortable()
    {
        using var temporal = CarpetaTemporal.Crear();
        var sesiones = Path.Combine(temporal.Ruta, "Portable");
        var sesion = Path.Combine(sesiones, $"Sesion-{Guid.NewGuid():N}");

        var instalada = ContextoDistribucion.Resolver(null, null, sesiones);
        var portable = ContextoDistribucion.Resolver("portable", sesion, sesiones);

        Assert.Equal(TipoDistribucion.Instalada, instalada.Tipo);
        Assert.Null(instalada.RaizPortable);
        Assert.Equal(TipoDistribucion.Portable, portable.Tipo);
        Assert.Equal(Path.GetFullPath(sesion), portable.RaizPortable, ignoreCase: true);
        portable.ValidarEjecutablePortable(Path.Combine(
            sesion,
            "Aplicacion",
            ContextoDistribucion.NombreEjecutableInternoPortable));
        Assert.Throws<InvalidOperationException>(() =>
            portable.ValidarEjecutablePortable(Path.Combine(
                temporal.Ruta,
                ContextoDistribucion.NombreEjecutableInternoPortable)));
    }

    [Fact]
    public void ContextoPortableRechazaRaicesManipuladas()
    {
        using var temporal = CarpetaTemporal.Crear();
        var sesiones = Path.Combine(temporal.Ruta, "Portable");
        var fuera = Path.Combine(temporal.Ruta, $"Sesion-{Guid.NewGuid():N}");

        Assert.Throws<InvalidOperationException>(() =>
            ContextoDistribucion.Resolver("portable", fuera, sesiones));
        Assert.Throws<InvalidOperationException>(() =>
            ContextoDistribucion.Resolver(
                "portable",
                Path.Combine(sesiones, "..", $"Sesion-{Guid.NewGuid():N}"),
                sesiones));
        Assert.Throws<InvalidOperationException>(() =>
            ContextoDistribucion.Resolver(
                "portable",
                Path.Combine(sesiones, $"Sesion-{Guid.NewGuid():N}"),
                Path.Combine(sesiones, "..", "Portable")));
        Assert.Throws<InvalidOperationException>(() =>
            ContextoDistribucion.Resolver("desconocida", null, sesiones));
    }

    [Fact]
    public void RutasPortableQuedanDentroDeLaSesion()
    {
        using var temporal = CarpetaTemporal.Crear();
        var sesiones = Path.Combine(temporal.Ruta, "Portable");
        var sesion = Path.Combine(sesiones, $"Sesion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sesion);
        using var entorno = VariablesDistribucion.Aplicar("portable", sesion, sesiones);

        string[] rutas =
        [
            RutasAplicacion.RaizDatosUsuario,
            RutasAplicacion.RutaConfiguracionUsuario,
            RutasAplicacion.RutaLogsUsuario,
            RutasAplicacion.RutaTokensUsuario,
            RutasAplicacion.RutaStaging,
            RutasAplicacion.RutaRaizWebView2Usuario,
            RutasAplicacion.RutaRaizWebView2RecuperacionLocal,
            RutasAplicacion.RutaRuntimesWebView2
        ];

        Assert.All(rutas, ruta =>
            Assert.True(ServicioRutasSeguras.EstaDentroDeCarpeta(sesion, ruta), ruta));
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Assert.All(rutas, ruta =>
            Assert.False(ServicioRutasSeguras.EstaDentroDeCarpeta(programData, ruta), ruta));
    }

    [Fact]
    public void LimpiezaConfinadaEliminaSoloLaSesionAutorizada()
    {
        using var temporal = CarpetaTemporal.Crear();
        var raiz = Path.Combine(temporal.Ruta, "WebView2");
        var sesion = Path.Combine(raiz, $"Sesion-{Guid.NewGuid():N}");
        var fuera = Path.Combine(temporal.Ruta, "Conservar");
        Directory.CreateDirectory(Path.Combine(sesion, "Datos"));
        Directory.CreateDirectory(fuera);
        File.WriteAllText(Path.Combine(sesion, "Datos", "estado.txt"), "temporal");
        File.WriteAllText(Path.Combine(fuera, "conservar.txt"), "estable");

        ServicioDirectoriosAplicacion.EliminarArbolSinAtravesarReanalisis(raiz, sesion);

        Assert.False(Directory.Exists(sesion));
        Assert.True(File.Exists(Path.Combine(fuera, "conservar.txt")));
        Assert.Throws<InvalidOperationException>(() =>
            ServicioDirectoriosAplicacion.EliminarArbolSinAtravesarReanalisis(raiz, raiz));
        Assert.Throws<InvalidOperationException>(() =>
            ServicioDirectoriosAplicacion.EliminarArbolSinAtravesarReanalisis(raiz, fuera));
    }

    [Fact]
    public void LimpiezaConfinadaNoSigueEnlacesDeDirectorio()
    {
        using var temporal = CarpetaTemporal.Crear();
        var raiz = Path.Combine(temporal.Ruta, "WebView2");
        var sesion = Path.Combine(raiz, $"Sesion-{Guid.NewGuid():N}");
        var destino = Path.Combine(temporal.Ruta, "DestinoExterno");
        var enlace = Path.Combine(sesion, "EnlaceExterno");
        Directory.CreateDirectory(sesion);
        Directory.CreateDirectory(destino);
        var protegido = Path.Combine(destino, "conservar.txt");
        File.WriteAllText(protegido, "estable");

        try
        {
            Directory.CreateSymbolicLink(enlace, destino);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // El entorno no permite crear enlaces simbolicos.
            return;
        }

        ServicioDirectoriosAplicacion.EliminarArbolSinAtravesarReanalisis(raiz, sesion);

        Assert.False(Directory.Exists(sesion));
        Assert.True(File.Exists(protegido));
    }

    [Fact]
    public void AuditoriaDerivaLaRutaYRechazaTraversal()
    {
        using var temporal = CarpetaTemporal.Crear();
        var permisos = Path.Combine(temporal.Ruta, "Permisos");
        var esperada = Path.Combine(permisos, ServicioAuditoria.NombreCarpetaAuditoria);

        Assert.Equal(
            Path.GetFullPath(esperada),
            ServicioAuditoria.ResolverRutaAuditoria(permisos),
            ignoreCase: true);
        Assert.Throws<InvalidOperationException>(() =>
            ServicioAuditoria.ResolverRutaAuditoria(
                Path.Combine(permisos, "..", "Fuera")));
    }

    [Fact]
    public async Task AuditoriaEscribeUnJsonExclusivoConIdentidadYHash()
    {
        using var escenario = EscenarioAuditoria.Crear(prepararAuditoria: true);
        using var auditoria = escenario.CrearServicio();
        var ejecucionId = Guid.NewGuid();
        var hash = new string('A', 64);

        var resultado = await auditoria.RegistrarInicioEjecucionAsync(
            ejecucionId,
            escenario.Script,
            escenario.Usuario,
            hash);

        Assert.True(resultado.Exito, resultado.Mensaje);
        Assert.NotNull(resultado.RutaArchivo);
        Assert.True(File.Exists(resultado.RutaArchivo));
        using var documento = JsonDocument.Parse(File.ReadAllBytes(resultado.RutaArchivo!));
        var raiz = documento.RootElement;
        Assert.Equal(1, raiz.GetProperty("versionEsquema").GetInt32());
        Assert.Equal(ejecucionId, raiz.GetProperty("ejecucionId").GetGuid());
        Assert.Equal(escenario.Identidad.Sid, raiz.GetProperty("usuarioSid").GetString());
        Assert.Equal(hash, raiz.GetProperty("scriptSha256").GetString());
        Assert.Equal("ejecucion.inicio", raiz.GetProperty("accion").GetString());
        Assert.Equal("permitido", raiz.GetProperty("resultado").GetString());
    }

    [Fact]
    public async Task AuditoriaConcurrenteNoSobrescribeEventos()
    {
        using var escenario = EscenarioAuditoria.Crear(prepararAuditoria: true);
        using var auditoria = escenario.CrearServicio();
        var tareas = Enumerable.Range(0, 24)
            .Select(indice => auditoria.RegistrarEventoSeguridadAsync(
                "prueba.concurrente",
                escenario.Usuario.NombreUsuario,
                escenario.Script.Id,
                "correcto",
                $"Evento {indice}"));

        var resultados = await Task.WhenAll(tareas);
        var archivos = Directory.GetFiles(
            Path.Combine(escenario.RutaPermisos, ServicioAuditoria.NombreCarpetaAuditoria),
            "*.json",
            SearchOption.AllDirectories);

        Assert.All(resultados, resultado => Assert.True(resultado.Exito, resultado.Mensaje));
        Assert.Equal(24, archivos.Length);
        Assert.Equal(24, archivos.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task InicioSeBloqueaSiElServidorNoEstaPreparado()
    {
        using var escenario = EscenarioAuditoria.Crear(prepararAuditoria: false);
        using var auditoria = escenario.CrearServicio();

        var resultado = await auditoria.RegistrarInicioEjecucionAsync(
            Guid.NewGuid(),
            escenario.Script,
            escenario.Usuario,
            new string('B', 64));

        Assert.False(resultado.Exito);
        Assert.Equal(0, auditoria.TotalPendientes);
        Assert.False(auditoria.Disponible);
    }

    [Fact]
    public async Task ResultadoPendienteBloqueaHastaPoderConfirmarse()
    {
        using var escenario = EscenarioAuditoria.Crear(prepararAuditoria: false);
        using var auditoria = escenario.CrearServicio();
        var hash = new string('C', 64);
        var final = await auditoria.RegistrarFinEjecucionAsync(
            Guid.NewGuid(),
            escenario.Script,
            escenario.Usuario,
            hash,
            "correcto",
            0,
            null);

        Assert.False(final.Exito);
        Assert.Equal(1, auditoria.TotalPendientes);
        Directory.CreateDirectory(Path.Combine(
            escenario.RutaPermisos,
            ServicioAuditoria.NombreCarpetaAuditoria));

        var siguiente = await auditoria.RegistrarInicioEjecucionAsync(
            Guid.NewGuid(),
            escenario.Script,
            escenario.Usuario,
            hash);

        Assert.True(siguiente.Exito, siguiente.Mensaje);
        Assert.Equal(0, auditoria.TotalPendientes);
    }

    [Fact]
    public async Task CierreRespetaElLimiteSiElServidorQuedaBloqueado()
    {
        using var escenario = EscenarioAuditoria.Crear(prepararAuditoria: false);
        using var liberarServidor = new ManualResetEventSlim(false);
        var llamadasRuta = 0;
        var auditoria = new ServicioAuditoria(
            () =>
            {
                if (Interlocked.Increment(ref llamadasRuta) > 1)
                {
                    liberarServidor.Wait();
                }

                return escenario.RutaPermisos;
            },
            () => escenario.Identidad,
            () => new DateTimeOffset(2026, 8, 8, 13, 0, 0, TimeSpan.FromHours(2)),
            protegerEventos: false);

        try
        {
            var final = await auditoria.RegistrarFinEjecucionAsync(
                Guid.NewGuid(),
                escenario.Script,
                escenario.Usuario,
                new string('D', 64),
                "correcto",
                0,
                null);
            Assert.False(final.Exito);
            Assert.Equal(1, auditoria.TotalPendientes);

            var cronometro = System.Diagnostics.Stopwatch.StartNew();
            auditoria.Cerrar(TimeSpan.FromMilliseconds(100));
            cronometro.Stop();

            Assert.True(
                cronometro.Elapsed < TimeSpan.FromSeconds(2),
                $"El cierre tardo {cronometro.Elapsed}.");
        }
        finally
        {
            liberarServidor.Set();
        }
    }

    [Fact]
    public void CodigoCentralCifraAuditoriaYProtegeDatosConAclAdministrativa()
    {
        var repositorio = File.ReadAllText(ObtenerRutaProyecto(
            "Servidor",
            "LanzadorScripts.Servidor.Core",
            "RepositorioServidor.cs"));
        var rutas = File.ReadAllText(ObtenerRutaProyecto(
            "Servidor",
            "LanzadorScripts.Servidor.Core",
            "RutasServidor.cs"));

        Assert.Contains("_cifrador.Cifrar(TablaAuditoria", repositorio, StringComparison.Ordinal);
        Assert.Contains("PRAGMA secure_delete = ON", repositorio, StringComparison.Ordinal);
        Assert.Contains("SetAccessRuleProtection(isProtected: true", rutas, StringComparison.Ordinal);
        Assert.Contains("BuiltinAdministratorsSid", rutas, StringComparison.Ordinal);
        Assert.Contains("LocalSystemSid", rutas, StringComparison.Ordinal);
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

    private sealed class EscenarioAuditoria : IDisposable
    {
        private readonly CarpetaTemporal _temporal;

        private EscenarioAuditoria(
            CarpetaTemporal temporal,
            string rutaPermisos,
            ScriptInterno script,
            UsuarioCliente usuario,
            IdentidadAuditoria identidad)
        {
            _temporal = temporal;
            RutaPermisos = rutaPermisos;
            Script = script;
            Usuario = usuario;
            Identidad = identidad;
        }

        public string RutaPermisos { get; }

        public ScriptInterno Script { get; }

        public UsuarioCliente Usuario { get; }

        public IdentidadAuditoria Identidad { get; }

        public static EscenarioAuditoria Crear(bool prepararAuditoria)
        {
            var temporal = CarpetaTemporal.Crear();
            var scripts = Path.Combine(temporal.Ruta, "Scripts");
            var permisos = Path.Combine(temporal.Ruta, "Permisos");
            Directory.CreateDirectory(scripts);
            Directory.CreateDirectory(permisos);
            if (prepararAuditoria)
            {
                Directory.CreateDirectory(Path.Combine(
                    permisos,
                    ServicioAuditoria.NombreCarpetaAuditoria));
            }

            File.WriteAllText(Path.Combine(scripts, "prueba.cmd"), "@echo off\r\nexit /b 0");
            var script = new ServicioValidacionScripts()
                .ValidarScriptParaEjecucion(scripts, "prueba.cmd")
                .Script!;
            using var windows = WindowsIdentity.GetCurrent();
            var identidad = new IdentidadAuditoria(
                windows.Name,
                windows.User?.Value ?? throw new InvalidOperationException("SID no disponible."),
                Environment.MachineName);
            var usuario = new UsuarioCliente(
                identidad.NombreUsuario,
                "admin",
                5,
                true);
            return new EscenarioAuditoria(temporal, permisos, script, usuario, identidad);
        }

        public ServicioAuditoria CrearServicio()
        {
            return new ServicioAuditoria(
                () => RutaPermisos,
                () => Identidad,
                () => new DateTimeOffset(2026, 8, 8, 13, 0, 0, TimeSpan.FromHours(2)),
                protegerEventos: false);
        }

        public void Dispose()
        {
            _temporal.Dispose();
        }
    }

    private sealed class VariablesDistribucion : IDisposable
    {
        private readonly string? _variante;
        private readonly string? _raiz;
        private readonly string? _sesiones;

        private VariablesDistribucion(string variante, string raiz, string sesiones)
        {
            _variante = Environment.GetEnvironmentVariable(ContextoDistribucion.VariableVariante);
            _raiz = Environment.GetEnvironmentVariable(ContextoDistribucion.VariableRaizPortable);
            _sesiones = Environment.GetEnvironmentVariable(ContextoDistribucion.VariableRaizSesionesPortable);
            Environment.SetEnvironmentVariable(ContextoDistribucion.VariableVariante, variante);
            Environment.SetEnvironmentVariable(ContextoDistribucion.VariableRaizPortable, raiz);
            Environment.SetEnvironmentVariable(ContextoDistribucion.VariableRaizSesionesPortable, sesiones);
        }

        public static VariablesDistribucion Aplicar(string variante, string raiz, string sesiones)
        {
            return new VariablesDistribucion(variante, raiz, sesiones);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(ContextoDistribucion.VariableVariante, _variante);
            Environment.SetEnvironmentVariable(ContextoDistribucion.VariableRaizPortable, _raiz);
            Environment.SetEnvironmentVariable(ContextoDistribucion.VariableRaizSesionesPortable, _sesiones);
        }
    }

    private sealed class CarpetaTemporal : IDisposable
    {
        private CarpetaTemporal(string ruta)
        {
            Ruta = ruta;
        }

        public string Ruta { get; }

        public static CarpetaTemporal Crear()
        {
            var ruta = Path.Combine(
                Path.GetTempPath(),
                "LanzadorScripts-Pruebas-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ruta);
            return new CarpetaTemporal(ruta);
        }

        public void Dispose()
        {
            if (Directory.Exists(Ruta))
            {
                Directory.Delete(Ruta, recursive: true);
            }
        }
    }
}
