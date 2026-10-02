// (Autor: Alex Roman)
// Descripcion: Pruebas automatizadas de seguridad, permisos y ejecucion.

using System.Net;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using LanzadorScripts.Modelos;
using LanzadorScripts.Servicios;
using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasLanzadorScripts
{
    [Fact]
    public void ValidadorBloqueaRutasNoPermitidas()
    {
        using var entorno = EntornoPruebas.Crear();
        var validador = new ServicioValidacionScripts();

        Assert.True(validador.ValidarScriptParaEjecucion(entorno.Raiz, "ok.ps1").EsValido);
        Assert.True(validador.ValidarScriptParaEjecucion(entorno.Raiz, "sub/ok.cmd").EsValido);
        Assert.Equal(CodigoValidacionScript.IdentificadorNoPermitido, validador.ValidarScriptParaEjecucion(entorno.Raiz, "../fuera.ps1").Codigo);
        Assert.Equal(CodigoValidacionScript.CarpetaExcluida, validador.ValidarScriptParaEjecucion(entorno.Raiz, "PERMISOS/bloqueado.ps1").Codigo);
        Assert.Equal(CodigoValidacionScript.ExtensionNoPermitida, validador.ValidarScriptParaEjecucion(entorno.Raiz, "texto.txt").Codigo);
        Assert.Equal(CodigoValidacionScript.MetacaracterPeligroso, validador.ValidarScriptParaEjecucion(entorno.Raiz, "bad&name.ps1").Codigo);

        var descubiertos = validador.DescubrirScripts(entorno.Raiz);
        Assert.Contains(descubiertos, script => script.Id == "ok.ps1");
        Assert.Contains(descubiertos, script => script.Id == "sub/ok.cmd");
        Assert.DoesNotContain(descubiertos, script => script.Id.Contains("PERMISOS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidadorBloqueaEnlacesDeSistema()
    {
        using var entorno = EntornoPruebas.Crear();
        var destino = Path.Combine(
            Path.GetTempPath(),
            "LanzadorScripts_Destino_" + Guid.NewGuid().ToString("N"));
        var enlace = Path.Combine(entorno.Raiz, "enlace");
        Directory.CreateDirectory(destino);
        File.WriteAllText(Path.Combine(destino, "a.ps1"), "Write-Output 1");

        try
        {
            try
            {
                Directory.CreateSymbolicLink(enlace, destino);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                or IOException
                or PlatformNotSupportedException)
            {
                return;
            }

            var resultado = new ServicioValidacionScripts()
                .ValidarScriptParaEjecucion(entorno.Raiz, "enlace/a.ps1");
            Assert.Equal(CodigoValidacionScript.EnlaceNoPermitido, resultado.Codigo);
        }
        finally
        {
            if (Directory.Exists(enlace))
            {
                Directory.Delete(enlace);
            }

            Directory.Delete(destino, recursive: true);
        }
    }

    [Fact]
    public void ValidadorDescubreCarpetasAunqueNoTenganScripts()
    {
        using var entorno = EntornoPruebas.Crear();
        var validador = new ServicioValidacionScripts();

        var carpetas = validador.DescubrirCarpetasScripts(entorno.Raiz);

        Assert.Contains("sub", carpetas);
        Assert.Contains("vacia", carpetas);
        Assert.DoesNotContain("PERMISOS", carpetas);
        Assert.DoesNotContain(".git", carpetas);
    }

    [Fact]
    public void ConfiguracionPermiteAdminShareOperativo()
    {
        var validador = new ServicioValidacionScripts();

        Assert.True(validador.ValidarConfiguracionBasica(@"\\SERVIDOR\C$\REPO", @"\\SERVIDOR\C$\REPO\PERMISOS").EsValida);
        Assert.True(validador.ValidarConfiguracionBasica(@"\\SERVIDOR\REPO", @"\\SERVIDOR\REPO\PERMISOS").EsValida);
        Assert.False(validador.ValidarConfiguracionBasica(
            @"\\SERVIDOR\REPO",
            @"\\SERVIDOR\REPO\PERMISOS\permisos.json").EsValida);
        Assert.False(validador.ValidarConfiguracionBasica(
            @"\\SERVIDOR\REPO",
            "PERMISOS").EsValida);
    }

    [Fact]
    public void ConfiguracionAvisaRutasNoDisponiblesSinBloquear()
    {
        var validador = new ServicioValidacionScripts();
        var raizNoDisponible = Path.Combine(Path.GetTempPath(), "LanzadorScripts_RutaAusente_" + Guid.NewGuid().ToString("N"));
        var rutaPermisos = Path.Combine(raizNoDisponible, "PERMISOS");

        Assert.True(validador.ValidarConfiguracionBasica(raizNoDisponible, rutaPermisos).EsValida);
        var aviso = validador.CrearAvisoConfiguracionNoDisponible(raizNoDisponible, rutaPermisos);

        Assert.Contains("carpeta de scripts no esta disponible", aviso, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("carpeta de permisos no esta disponible", aviso, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManifiestoSolicitaAdministrador()
    {
        var rutaManifiesto = Path.Combine(ObtenerRaizProyecto(), "manifiesto.manifest");
        var manifiesto = File.ReadAllText(rutaManifiesto);

        Assert.Contains("requireAdministrator", manifiesto, StringComparison.Ordinal);
        Assert.DoesNotContain("asInvoker", manifiesto, StringComparison.Ordinal);
    }

    [Fact]
    public void AsociacionPriorizaElExeUnicoDistribuido()
    {
        const string distribuido = @"C:\Distribucion\LanzadorScripts.exe";
        const string interno = @"C:\Program Files\LanzadorScripts\Aplicacion\LanzadorScripts.Runtime.exe";
        var broker = File.ReadAllText(Path.Combine(
            ObtenerRaizProyecto(),
            "Servicios",
            "ServicioBrokerElevado.cs"));

        var seleccionado = ServicioEjecutableAplicacion.SeleccionarRutaEjecutable(
            distribuido,
            interno,
            ruta => string.Equals(ruta, distribuido, StringComparison.OrdinalIgnoreCase));
        var alternativo = ServicioEjecutableAplicacion.SeleccionarRutaEjecutable(
            @"LanzadorScripts.exe",
            interno,
            _ => true);
        var manipulado = ServicioEjecutableAplicacion.SeleccionarRutaEjecutable(
            @"C:\Distribucion\..\Otro\LanzadorScripts.exe",
            interno,
            _ => true);

        Assert.Equal(distribuido, seleccionado, ignoreCase: true);
        Assert.Equal(interno, alternativo, ignoreCase: true);
        Assert.Equal(interno, manipulado, ignoreCase: true);
        Assert.Contains("ServicioEjecutableAplicacion.ResolverRutaRelanzable()", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.ProcessPath", broker, StringComparison.Ordinal);
    }

    [Fact]
    public void AplicacionNoIncluyeModoServicio()
    {
        var raiz = ObtenerRaizProyecto();
        var aplicacion = File.ReadAllText(Path.Combine(raiz, "Aplicacion.xaml.cs"));
        var proyecto = File.ReadAllText(Path.Combine(raiz, "LanzadorScripts.csproj"));

        Assert.DoesNotContain("ServicioWindowsLanzador", aplicacion, StringComparison.Ordinal);
        Assert.DoesNotContain("System.ServiceProcess", proyecto, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(raiz, "Servicios", "ServicioWindowsLanzador.cs")));
        Assert.False(File.Exists(Path.Combine(raiz, "Servicios", "ServicioDescubrimientoLocal.cs")));
    }

    [Fact]
    public void PerfilAplicacionNormalizaUsuario()
    {
        Assert.Equal("aroperez", PerfilAplicacion.Normalizar("AROPEREZ"));
        Assert.Equal("usuario_micro", PerfilAplicacion.Normalizar("usuario micro"));
        Assert.Equal("default", PerfilAplicacion.Normalizar(null));
        Assert.NotEqual(
            PerfilAplicacion.CrearIdentificadorSid("S-1-5-21-1000"),
            PerfilAplicacion.CrearIdentificadorSid("S-1-5-21-1001"));
    }

    [Fact]
    public void PanelAjustesNoDuplicaExecutionPolicyUnrestricted()
    {
        var vista = System.Xml.Linq.XDocument.Load(Path.Combine(ObtenerRaizProyecto(), "Vistas", "ClienteNativo.xaml"));
        Assert.Single(vista.Descendants(), e => (string?)e.Attribute("Click") == "ExecutionPolicy_Click");
        var motor = File.ReadAllText(Path.Combine(ObtenerRaizProyecto(), "Servicios", "ServidorLocalWeb.Nativo.cs"));
        Assert.Contains("ExigirAdministradorNativoAsync", motor, StringComparison.Ordinal);
    }

    [Fact]
    public void AplicacionNoSolicitaNiAprovisionaClavesDeArtefactos()
    {
        var raiz = ObtenerRaizProyecto();
        var rutaVentana = Path.Combine(raiz, "VentanaPrincipal.xaml.cs");
        var rutaDialogo = Path.Combine(raiz, "DialogoClaveArtefactos.xaml");
        var rutaCodigoDialogo = Path.Combine(raiz, "DialogoClaveArtefactos.xaml.cs");
        var ventana = File.ReadAllText(rutaVentana);
        var aplicacion = File.ReadAllText(Path.Combine(raiz, "Aplicacion.xaml.cs"));
        var rutas = File.ReadAllText(Path.Combine(raiz, "Servicios", "RutasAplicacion.cs"));

        Assert.DoesNotContain("Instalar clave", ventana, StringComparison.Ordinal);
        Assert.DoesNotContain("aprovisionarClaveArtefactos", ventana, StringComparison.Ordinal);
        Assert.DoesNotContain("ServicioClaveArtefactos.Aprovisionar", ventana, StringComparison.Ordinal);
        Assert.DoesNotContain("AprovisionamientoClave", aplicacion, StringComparison.Ordinal);
        Assert.DoesNotContain("artefactos.key", rutas, StringComparison.Ordinal);
        Assert.False(File.Exists(rutaDialogo));
        Assert.False(File.Exists(rutaCodigoDialogo));
    }

    [Fact]
    public void PanelAjustesPublicaCatalogoUnificado()
    {
        var vista = File.ReadAllText(Path.Combine(ObtenerRaizProyecto(), "Vistas", "ClienteNativo.xaml"));
        var modelo = File.ReadAllText(Path.Combine(ObtenerRaizProyecto(), "ModelosVista", "ClienteNativoModelo.cs"));
        Assert.Contains("PublicarCatalogo_Click", vista, StringComparison.Ordinal);
        Assert.Contains("DataGridCheckBoxColumn", vista, StringComparison.Ordinal);
        Assert.Contains("_cliente.PublicarCatalogoAsync", modelo, StringComparison.Ordinal);
        Assert.Contains("Servidor central", vista, StringComparison.Ordinal);
    }

    [Fact]
    public void InterfazNavegaScriptsPorCarpetas()
    {
        var modelo = File.ReadAllText(Path.Combine(ObtenerRaizProyecto(), "ModelosVista", "ClienteNativoModelo.cs"));
        Assert.Contains("AbrirCarpetaAsync", modelo, StringComparison.Ordinal);
        Assert.Contains("SubirCarpetaAsync", modelo, StringComparison.Ordinal);
        Assert.Contains("_cliente.ListarScriptsAsync", modelo, StringComparison.Ordinal);
    }

    [Fact]
    public void EjecucionPowerShellUtilizaHostAisladoSinAdaptadoresDeTexto()
    {
        var rutaGestor = Path.Combine(ObtenerRaizProyecto(), "Servicios", "GestorEjecucionesWeb.cs");
        var codigo = File.ReadAllText(rutaGestor);

        Assert.Contains("ServicioProcesoScript", codigo, StringComparison.Ordinal);
        Assert.DoesNotContain("CrearPlanPowerShell", codigo, StringComparison.Ordinal);
        var host = File.ReadAllText(Path.Combine(ObtenerRaizProyecto(), "EjecutorPowerShell", "Programa.cs"));
        Assert.Contains("PSAuthorizationManager", host, StringComparison.Ordinal);
        Assert.Contains("InitialSessionState.CreateDefault", host, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProfileCommands", host, StringComparison.Ordinal);
    }

    [Fact]
    public void AplicacionNoExponeTareasDeInicioWindows()
    {
        Assert.False(File.Exists(Path.Combine(ObtenerRaizProyecto(), "Servicios", "ServicioInicioAutomaticoPerfil.cs")));
        Assert.False(File.Exists(Path.Combine(ObtenerRaizProyecto(), "Servicios", "PerfilServicioLanzador.cs")));
    }

    // Comprueba que no regresen las implementaciones WPF sustituidas por el backend web.
    [Fact]
    public void ProyectoNoConservaImplementacionesWpfObsoletas()
    {
        var archivosObsoletos = new[]
        {
            Path.Combine("Modelos", "ConfiguracionPermisos.cs"),
            Path.Combine("Modelos", "EstadoEjecucion.cs"),
            Path.Combine("Modelos", "InformacionScript.cs"),
            Path.Combine("Modelos", "PermisosLanzador.cs"),
            Path.Combine("ModelosVista", "ModeloEjecucionScript.cs"),
            Path.Combine("ModelosVista", "ModeloVentanaPrincipal.cs"),
            Path.Combine("ModelosVista", "ObjetoNotificable.cs"),
            Path.Combine("ModelosVista", "ObjetoObservable.cs"),
            Path.Combine("Servicios", "GestorEjecucionScripts.cs")
        };

        foreach (var archivo in archivosObsoletos)
        {
            Assert.False(File.Exists(Path.Combine(ObtenerRaizProyecto(), archivo)));
        }
    }

    [Fact]
    public void DirectorioPrivadoSoloConcedeModificacionAlUsuarioActual()
    {
        using var entorno = EntornoPruebas.Crear();
        var carpeta = Path.Combine(entorno.Raiz, "privado");

        ServicioDirectoriosAplicacion.PrepararDirectorioPrivado(carpeta);

        var reglas = new DirectoryInfo(carpeta)
            .GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(regla => regla.AccessControlType == AccessControlType.Allow)
            .ToList();
        var usuario = WindowsIdentity.GetCurrent().User?.Value;
        Assert.Contains(reglas, regla =>
            string.Equals(regla.IdentityReference.Value, usuario, StringComparison.Ordinal)
            && regla.FileSystemRights.HasFlag(FileSystemRights.Modify));
        Assert.DoesNotContain(reglas, regla =>
            string.Equals(regla.IdentityReference.Value, "S-1-5-32-545", StringComparison.Ordinal));
    }

    [Fact]
    public void DirectorioBaseImpideEscrituraAUsuariosNormales()
    {
        using var entorno = EntornoPruebas.Crear();
        var carpeta = Path.Combine(entorno.Raiz, "base-segura");

        ServicioDirectoriosAplicacion.PrepararDirectorioBase(carpeta);

        var reglas = new DirectoryInfo(carpeta)
            .GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(regla => regla.AccessControlType == AccessControlType.Allow)
            .ToList();
        var reglaUsuarios = Assert.Single(reglas, regla =>
            string.Equals(regla.IdentityReference.Value, "S-1-5-32-545", StringComparison.Ordinal));
        Assert.True(reglaUsuarios.FileSystemRights.HasFlag(FileSystemRights.ReadAndExecute));
        Assert.False(reglaUsuarios.FileSystemRights.HasFlag(FileSystemRights.Write));
    }

    [Fact]
    public void TokenAdministradorSePuedeLeerDesdeProgramData()
    {
        var servicio = new ServicioTokensAdmin();

        var token = servicio.ObtenerOCrear(WindowsIdentity.GetCurrent().Name);

        Assert.False(string.IsNullOrWhiteSpace(token.Valor));
        Assert.True(servicio.Validar(token.UsuarioWindows, token.Valor));
    }

    [Fact]
    public void ConfiguracionPredeterminadaUsaRutasOperativas()
    {
        var rutaConfiguracion = Path.Combine(ObtenerRaizProyecto(), "ConfiguracionPredeterminada.json");
        var configuracion = File.ReadAllText(rutaConfiguracion);
        var modelo = new ConfiguracionLanzador();

        Assert.Contains(@"\\\\MAD002MICROPRU.mad.ae.aena.es\\R$\\SCRIPS", configuracion, StringComparison.Ordinal);
        Assert.Contains(@"\\\\MAD002MICROPRU.mad.ae.aena.es\\R$\\PERMISOS", configuracion, StringComparison.Ordinal);
        Assert.Contains("\"VersionConfiguracion\": 3", configuracion, StringComparison.Ordinal);
        Assert.Contains("\"ServidorCentral\": \"MAD002MICROPRU.mad.ae.aena.es\"", configuracion, StringComparison.Ordinal);
        Assert.Contains("\"PuertoServidorCentral\": 47831", configuracion, StringComparison.Ordinal);
        Assert.Equal(@"\\MAD002MICROPRU.mad.ae.aena.es\R$\SCRIPS", modelo.RutaScripts);
        Assert.Equal(RutasArtefactosProtegidos.CarpetaPredeterminada, modelo.RutaPermisos);
        var rutas = new ServicioValidacionScripts().ResolverRutasArtefactos(modelo.RutaPermisos);
        Assert.Equal(
            Path.Combine(RutasArtefactosProtegidos.CarpetaPredeterminada, "permisos.json"),
            rutas.RutaPermisos);
        Assert.Equal(
            Path.Combine(RutasArtefactosProtegidos.CarpetaPredeterminada, "catalogo-scripts.json"),
            rutas.RutaCatalogo);
        Assert.StartsWith(
            rutas.Carpeta + Path.DirectorySeparatorChar,
            rutas.RutaPermisos,
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            rutas.Carpeta + Path.DirectorySeparatorChar,
            rutas.RutaCatalogo,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfiguracionMigraRutaArchivoALaCarpetaDePermisos()
    {
        var carpeta = Path.Combine(Path.GetTempPath(), "LanzadorScripts_Permisos");
        var configuracion = new ConfiguracionLanzador
        {
            RutaPermisos = Path.Combine(carpeta, "permisos.json")
        };

        configuracion.Normalizar();

        Assert.Equal(carpeta, configuracion.RutaPermisos);
        Assert.Equal(
            Path.Combine(carpeta, "catalogo-scripts.json"),
            RutasArtefactosProtegidos.Resolver(configuracion.RutaPermisos).RutaCatalogo);

        configuracion.RutaPermisos = "permisos.json";
        configuracion.Normalizar();
        Assert.Equal(RutasArtefactosProtegidos.CarpetaPredeterminada, configuracion.RutaPermisos);

        configuracion.RutaPermisos = Path.Combine(AppContext.BaseDirectory, "permisos.json");
        configuracion.Normalizar();
        Assert.Equal(RutasArtefactosProtegidos.CarpetaPredeterminada, configuracion.RutaPermisos);
    }

    [Fact]
    public void RutasProtegidasRechazanTraversalYSeparadoresNoPermitidos()
    {
        using var entorno = EntornoPruebas.Crear();

        Assert.Throws<InvalidOperationException>(() =>
            RutasArtefactosProtegidos.Resolver(Path.Combine(entorno.Raiz, "..", "PERMISOS")));
        Assert.Throws<InvalidOperationException>(() =>
            RutasArtefactosProtegidos.Resolver("C:/LanzadorScripts/PERMISOS"));
        Assert.False(ServicioRutasSeguras.EsArchivoAbsolutoValido(
            Path.Combine(entorno.Raiz, "..", "mal.lanzadorconfig"),
            "paquete de configuracion",
            ServicioPaquetesConfiguracion.ExtensionPaquete));
        var rutasUnc = RutasArtefactosProtegidos.Resolver(@"\\SERVIDOR\C$\PERMISOS");
        Assert.Equal(@"\\SERVIDOR\C$\PERMISOS\permisos.json", rutasUnc.RutaPermisos);
    }

    [Fact]
    public void ImportacionPorContenidoYRutaScriptValidadaRechazanTraversal()
    {
        using var entorno = EntornoPruebas.Crear();
        var servicio = new ServicioPaquetesConfiguracion();
        var rutaPaqueteTraversal = Path.Combine(entorno.Raiz, "..", "mal.lanzadorconfig");
        var validador = new ServicioValidacionScripts();

        Assert.False(ServicioPaquetesConfiguracion.EsRutaImportacionValida(rutaPaqueteTraversal));
        Assert.False(validador.ValidarScriptParaEjecucion(entorno.Raiz, "sub/../ok.ps1").EsValido);
        Assert.False(validador.ValidarScriptParaEjecucion(entorno.Raiz, @"sub/..\ok.ps1").EsValido);
        Assert.False(validador.ValidarScriptParaEjecucion(entorno.Raiz, "texto.txt").EsValido);
        var script = validador.ValidarScriptParaEjecucion(entorno.Raiz, "ok.ps1").Script!;
        Assert.Equal(64, ServicioSeguridadScripts.CalcularSha256(script.RutaValidada).Length);
        Assert.DoesNotContain(
            typeof(ServicioSeguridadScripts).GetMethods(),
            metodo => metodo.Name == nameof(ServicioSeguridadScripts.CalcularSha256)
                && metodo.GetParameters().Single().ParameterType == typeof(string));
    }

    [Fact]
    public void LectorPermisosObsoletoNoFormaParteDelBackend()
    {
        Assert.False(File.Exists(Path.Combine(ObtenerRaizProyecto(), "Servicios", "ServicioPermisos.cs")));
    }

    [Fact]
    public void ConfiguracionMigraLogsDeLocalAppDataAProgramData()
    {
        var configuracion = new ConfiguracionLanzador
        {
            RutaLogs = Path.Combine(RutasAplicacion.RaizLocalAppDataLegada, "Logs")
        };

        ServicioConfiguracion.MigrarRutaLogsLegada(configuracion);

        Assert.Equal(RutasAplicacion.RutaLogsUsuario, configuracion.RutaLogs);
    }

    [Fact]
    public void ConfiguracionAntiguaRestableceRutasPredeterminadas()
    {
        var antigua = new ConfiguracionLanzador
        {
            VersionConfiguracion = null,
            RutaScripts = @"C:\RUTA-ANTIGUA\SCRIPS",
            RutaPermisos = @"C:\RUTA-ANTIGUA\PERMISOS"
        };
        var predeterminada = new ConfiguracionLanzador
        {
            VersionConfiguracion = ConfiguracionLanzador.VersionActual
        };

        ServicioConfiguracion.MigrarRutasPredeterminadasAnteriores(antigua, predeterminada);

        Assert.Equal(ConfiguracionLanzador.VersionActual, antigua.VersionConfiguracion);
        Assert.Equal(predeterminada.RutaScripts, antigua.RutaScripts);
        Assert.Equal(predeterminada.RutaPermisos, antigua.RutaPermisos);
    }

    [Fact]
    public void ConfiguracionReintentaCuandoElArchivoEstaBloqueado()
    {
        using var entorno = EntornoPruebas.Crear();
        var ruta = Path.Combine(entorno.Raiz, "configuracion.dat");
        var servicio = new ServicioConfiguracion(ruta);
        servicio.Guardar(entorno.CrearConfiguracion());

        var bloqueo = new FileStream(ruta, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        // Usa un hilo dedicado para liberar el archivo sin depender del ThreadPool.
        var liberador = new Thread(() =>
        {
            Thread.Sleep(600);
            bloqueo.Dispose();
        })
        {
            IsBackground = true
        };
        liberador.Start();

        ConfiguracionLanzador configuracion;
        try
        {
            configuracion = new ServicioConfiguracion(ruta).Cargar();
        }
        finally
        {
            liberador.Join();
            bloqueo.Dispose();
        }

        Assert.Equal(entorno.Raiz, configuracion.RutaScripts, ignoreCase: true);
        Assert.Equal(entorno.CarpetaPermisos, configuracion.RutaPermisos, ignoreCase: true);
    }

    [Fact]
    public async Task ConfiguracionPermaneceValidaConAccesoConcurrente()
    {
        using var entorno = EntornoPruebas.Crear();
        var ruta = Path.Combine(entorno.Raiz, "configuracion.dat");
        new ServicioConfiguracion(ruta).Guardar(entorno.CrearConfiguracion());

        var tareas = Enumerable.Range(0, 16).Select(indice => Task.Run(() =>
        {
            var servicio = new ServicioConfiguracion(ruta);
            for (var iteracion = 0; iteracion < 8; iteracion++)
            {
                var configuracion = servicio.Cargar();
                configuracion.MaximoEjecucionesParalelas = ((indice + iteracion) % 50) + 1;
                servicio.Guardar(configuracion);
            }
        }));

        await Task.WhenAll(tareas);
        var resultado = new ServicioConfiguracion(ruta).Cargar();

        Assert.Equal(entorno.Raiz, resultado.RutaScripts, ignoreCase: true);
        Assert.Equal(entorno.CarpetaPermisos, resultado.RutaPermisos, ignoreCase: true);
        Assert.InRange(resultado.MaximoEjecucionesParalelas, 1, 50);
    }

    [Fact]
    public void ConfiguracionValidaNoSeReescribeAlCargar()
    {
        using var entorno = EntornoPruebas.Crear();
        var ruta = Path.Combine(entorno.Raiz, "configuracion.dat");
        var servicio = new ServicioConfiguracion(ruta);
        servicio.Guardar(entorno.CrearConfiguracion());
        var fechaControl = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(ruta, fechaControl);

        _ = servicio.Cargar();

        Assert.Equal(fechaControl, File.GetLastWriteTimeUtc(ruta));
        Assert.False(File.Exists(ruta + ".bak"));
    }

    [Fact]
    public void ConfiguracionRecuperaRespaldoSiLaPrincipalEstaDanada()
    {
        using var entorno = EntornoPruebas.Crear();
        var ruta = Path.Combine(entorno.Raiz, "configuracion.dat");
        var servicio = new ServicioConfiguracion(ruta);
        var anterior = entorno.CrearConfiguracion();
        anterior.MaximoEjecucionesParalelas = 3;
        servicio.Guardar(anterior);
        var actual = entorno.CrearConfiguracion();
        actual.MaximoEjecucionesParalelas = 8;
        servicio.Guardar(actual);
        File.WriteAllBytes(ruta, [1, 2, 3, 4, 5]);

        var recuperada = servicio.Cargar();

        Assert.Equal(3, recuperada.MaximoEjecucionesParalelas);
        Assert.Equal(3, servicio.Cargar().MaximoEjecucionesParalelas);
    }

    [Fact]
    public void ConfiguracionInvalidaNoSeSustituyePorValoresPredeterminados()
    {
        using var entorno = EntornoPruebas.Crear();
        var ruta = Path.Combine(entorno.Raiz, "configuracion.dat");
        var contenidoInvalido = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(ruta, contenidoInvalido);

        Assert.Throws<InvalidDataException>(() => new ServicioConfiguracion(ruta).Cargar());
        Assert.Equal(contenidoInvalido, File.ReadAllBytes(ruta));
    }


    [Fact]
    public void TokenMaestroFirmadoEsReutilizable()
    {
        using var rsa = RSA.Create(3072);
        var servicio = new ServicioTokenMaestro(rsa, rsa);
        var token = servicio.Generar();

        Assert.True(servicio.Validar(token, out var primerPayload, out var primerMotivo), primerMotivo);
        Assert.True(servicio.Validar(token, out var segundoPayload, out var segundoMotivo), segundoMotivo);
        Assert.Equal(primerPayload?.Id, segundoPayload?.Id);
    }

    [Fact]
    public void TokenMaestroRechazaFirmaManipulada()
    {
        using var rsa = RSA.Create(3072);
        var servicio = new ServicioTokenMaestro(rsa, rsa);
        var partes = servicio.Generar().Split('.');
        partes[2] = partes[2][..^1] + (partes[2][^1] == 'A' ? "B" : "A");

        Assert.False(servicio.Validar(string.Join(".", partes), out _, out var motivo));
        Assert.Equal("Firma de token no valida.", motivo);
    }

    [Fact]
    public void SeguridadBloqueaScriptsFueraDelCatalogo()
    {
        using var entorno = EntornoPruebas.Crear();
        var validador = new ServicioValidacionScripts();
        var seguridad = new ServicioSeguridadScripts();
        var permisosVacios = CrearPermisosBase();

        var ps1 = validador.ValidarScriptParaEjecucion(entorno.Raiz, "ok.ps1").Script!;
        var cmd = validador.ValidarScriptParaEjecucion(entorno.Raiz, "sub/ok.cmd").Script!;

        Assert.False(seguridad.Diagnosticar(ps1, permisosVacios, null, "Catalogo ausente.").Permitido);
        Assert.False(seguridad.Diagnosticar(cmd, permisosVacios, null, "Catalogo ausente.").Permitido);

        var catalogo = new ServicioCatalogoScripts(entorno.Artefactos).Crear(
            [ps1, cmd],
            [cmd.Id],
            entorno.ConjuntoId);
        Assert.True(seguridad.Diagnosticar(cmd, permisosVacios, catalogo, string.Empty).Permitido);
        Assert.False(seguridad.Diagnosticar(ps1, permisosVacios, catalogo, string.Empty).Permitido);
        Assert.True(seguridad.Diagnosticar(
            ps1,
            permisosVacios,
            null,
            "Catalogo ausente.",
            modoDesarrolloFirmas: true).Permitido);
    }

    [Fact]
    public void PoliticaNormalizaScriptsElevadosYBypass()
    {
        var permisos = CrearPermisosBase();
        permisos["seguridadScripts"]!["scriptsElevadosPermitidos"] = new JsonArray("sub/ok.cmd", "sub/ok.cmd");
        permisos["seguridadScripts"]!["permitirExecutionPolicyBypass"] = true;

        var politica = ServicioSeguridadScripts.LeerPolitica(permisos);
        var normalizada = ServicioSeguridadScripts.NormalizarPolitica(permisos["seguridadScripts"] as JsonObject);
        var elevados = normalizada["scriptsElevadosPermitidos"] as JsonArray;

        Assert.Contains("sub/ok.cmd", politica.ScriptsElevadosPermitidos);
        Assert.True(politica.PermitirExecutionPolicyBypass);
        Assert.NotNull(elevados);
        Assert.Single(elevados!);
    }

    [Fact]
    public void CodigoNoContieneClavesPrivadasIntegradas()
    {
        var raiz = ObtenerRaizProyecto();
        var artefactos = File.ReadAllText(
            Path.Combine(raiz, "Servicios", "ServicioArtefactosFirmados.cs"),
            Encoding.UTF8);

        Assert.DoesNotContain("ClaveAesBase64", artefactos, StringComparison.Ordinal);
        Assert.DoesNotContain("ClavePrivadaBase64", artefactos, StringComparison.Ordinal);
        Assert.DoesNotContain("ImportPkcs8PrivateKey", artefactos, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(raiz, "Servicios", "ServicioClaveArtefactos.cs")));
        Assert.False(File.Exists(Path.Combine(raiz, "Servicios", "ServicioDpapiNg.cs")));
        Assert.False(File.Exists(Path.Combine(raiz, "Herramientas", "AprovisionarClaveArtefactos.ps1")));
    }

    [Fact]
    public void CatalogoBloqueaScriptModificado()
    {
        using var entorno = EntornoPruebas.Crear();
        var validador = new ServicioValidacionScripts();
        var seguridad = new ServicioSeguridadScripts();
        var script = validador.ValidarScriptParaEjecucion(entorno.Raiz, "sub/ok.cmd").Script!;
        var catalogo = new ServicioCatalogoScripts(entorno.Artefactos).Crear(
            [script],
            [script.Id],
            entorno.ConjuntoId);

        Assert.True(seguridad.Diagnosticar(
            script,
            CrearPermisosBase(),
            catalogo,
            string.Empty).Permitido);

        File.AppendAllText(script.RutaCompleta, Environment.NewLine + "echo modificado");
        var diagnostico = seguridad.Diagnosticar(
            script,
            CrearPermisosBase(),
            catalogo,
            string.Empty);
        Assert.False(diagnostico.Permitido);
        Assert.Equal("modificado", diagnostico.CatalogoEstado);
    }

    [Fact]
    public void CatalogoAceptaExtensionEnMayusculas()
    {
        using var entorno = EntornoPruebas.Crear();
        var ruta = Path.Combine(entorno.Raiz, "INSTALADOR.BAT");
        File.WriteAllText(ruta, "@echo off");
        var script = new ServicioValidacionScripts()
            .ValidarScriptParaEjecucion(entorno.Raiz, "INSTALADOR.BAT")
            .Script!;
        var servicio = new ServicioCatalogoScripts(entorno.Artefactos);
        var catalogo = servicio.Crear([script], [script.Id], entorno.ConjuntoId);
        var rutaCatalogo = Path.Combine(entorno.Raiz, ServicioCatalogoScripts.NombreArchivo);

        servicio.Guardar(rutaCatalogo, catalogo);

        Assert.True(servicio.IntentarCargar(rutaCatalogo, out var cargado, out _));
        Assert.Contains(cargado!.Scripts, entrada => entrada.ScriptId == "INSTALADOR.BAT");
    }

    [Fact]
    public void GeneracionInicialIncluyeDosAdministradoresYCatalogoFirmado()
    {
        using var entorno = EntornoPruebas.Crear();
        var salida = Path.Combine(entorno.Raiz, "salida-publicacion");
        ServicioGeneracionArtefactosIniciales.Generar(
            entorno.Raiz,
            salida,
            ServicioGeneracionArtefactosIniciales.AdministradoresPredeterminados,
            entorno.Artefactos,
            entorno.ConjuntoId);
        var artefactos = entorno.Artefactos;

        var permisosFirmados = File.ReadAllText(Path.Combine(salida, "permisos.json"));
        Assert.Contains("\"Contenido\"", permisosFirmados, StringComparison.Ordinal);
        Assert.Contains("MAD00\\\\aroperez_micro", permisosFirmados, StringComparison.Ordinal);
        Assert.True(artefactos.IntentarValidarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            permisosFirmados,
            out var permisosJson,
            out var conjuntoPermisos,
            out _));
        var permisos = JsonNode.Parse(permisosJson)!.AsObject();
        var usuarios = permisos["usuarios"]!.AsArray();
        Assert.Equal(2, usuarios.Count);
        Assert.Contains(usuarios, usuario => usuario?["nombreUsuario"]?.GetValue<string>() == @"MAD00\aroperez_micro");
        Assert.Contains(usuarios, usuario => usuario?["nombreUsuario"]?.GetValue<string>() == @"PCERA\alero");
        Assert.All(usuarios, usuario => Assert.Equal("admin", usuario?["rol"]?.GetValue<string>()));

        var rutaCatalogo = Path.Combine(salida, ServicioCatalogoScripts.NombreArchivo);
        Assert.True(new ServicioCatalogoScripts(entorno.Artefactos).IntentarCargar(rutaCatalogo, out var catalogo, out _));
        Assert.NotNull(catalogo);
        Assert.Equal(conjuntoPermisos, catalogo!.ConjuntoId);
        Assert.Contains(catalogo.Scripts, script => script.ScriptId == "ok.ps1");
        Assert.Contains(catalogo.Scripts, script => script.ScriptId == "sub/ok.cmd");
    }

    [Fact]
    public void ContenedorFirmadoRechazaManipulacion()
    {
        using var rsa = RSA.Create(3072);
        var servicio = new ServicioCifradoAplicacion(rsa, rsa, "Pruebas");
        var firmado = servicio.CifrarTexto("configuracion-exportada", "{\"ok\":true}");

        Assert.True(servicio.IntentarDescifrarTexto("configuracion-exportada", firmado, out var claro));
        Assert.Contains("\"ok\":true", claro, StringComparison.Ordinal);
        var manipulado = JsonNode.Parse(firmado)!.AsObject();
        manipulado["Datos"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"ok\":false}"));
        Assert.False(servicio.IntentarDescifrarTexto("configuracion-exportada", manipulado.ToJsonString(), out _));
        Assert.False(servicio.IntentarDescifrarTexto("permisos", firmado, out _));
    }

    [Fact]
    public void ImportacionContenidoLimitaTamanoYRechazaCamposDesconocidos()
    {
        using var entorno = EntornoPruebas.Crear();
        using var rsa = RSA.Create(3072);
        var firma = new ServicioCifradoAplicacion(rsa, rsa, "Pruebas");
        var servicio = new ServicioPaquetesConfiguracion(firma);
        var configuracion = entorno.CrearConfiguracion();
        var paquete = servicio.Exportar(configuracion, CrearPermisosAdmin());
        var contenido = Encoding.UTF8.GetString(Convert.FromBase64String(paquete.ContenidoBase64));
        var manipulado = JsonNode.Parse(contenido)!.AsObject();
        manipulado["secreto"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        Assert.Throws<InvalidOperationException>(() =>
            servicio.ImportarContenido(manipulado.ToJsonString(), new ConfiguracionLanzador()));
        Assert.Throws<InvalidOperationException>(() =>
            servicio.ImportarContenido(
                new string('A', ServicioPaquetesConfiguracion.LongitudMaximaContenido + 1),
                new ConfiguracionLanzador()));
    }

    [Fact]
    public void PaqueteConfiguracionImportaConexionSinPermisosNiSecretos()
    {
        using var entorno = EntornoPruebas.Crear();
        using var rsa = RSA.Create(3072);
        var firma = new ServicioCifradoAplicacion(rsa, rsa, "Pruebas");
        var servicio = new ServicioPaquetesConfiguracion(firma);
        var configuracion = new ConfiguracionLanzador
        {
            RutaScripts = entorno.Raiz,
            RutaPermisos = Path.Combine(entorno.Raiz, "PERMISOS")
        };

        var paquete = servicio.Exportar(configuracion, CrearPermisosAdmin());
        var contenido = Encoding.UTF8.GetString(Convert.FromBase64String(paquete.ContenidoBase64));
        var importacion = servicio.ImportarContenido(contenido, new ConfiguracionLanzador());
        Assert.Equal(configuracion.RutaScripts, importacion.Configuracion.RutaScripts);
        Assert.Null(importacion.Permisos);
        Assert.DoesNotContain("permisos", contenido, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("firma", contenido, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clave", contenido, StringComparison.OrdinalIgnoreCase);

        var configuracionAdminShare = new ConfiguracionLanzador
        {
            RutaScripts = @"\\SERVIDOR\C$\REPO",
            RutaPermisos = @"\\SERVIDOR\C$\REPO\PERMISOS"
        };
        var paqueteAdminShare = servicio.Exportar(configuracionAdminShare, CrearPermisosAdmin());
        var contenidoAdminShare = Encoding.UTF8.GetString(
            Convert.FromBase64String(paqueteAdminShare.ContenidoBase64));
        var importacionAdminShare = servicio.ImportarContenido(
            contenidoAdminShare,
            new ConfiguracionLanzador());
        Assert.Equal(configuracionAdminShare.RutaScripts, importacionAdminShare.Configuracion.RutaScripts);
    }

    [Fact]
    public async Task ApiBloqueaPermisosAusentesOCorruptos()
    {
        using var entorno = EntornoPruebas.Crear();
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(
            entorno.CrearConfiguracionPermisosAusentes(),
            entorno.Artefactos);
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);

        var usuario = await LeerJsonAsync(await cliente.GetAsync("/api/usuario"));
        Assert.True(usuario?["bloqueado"]?.GetValue<bool>());
        Assert.Equal("No se encontro el archivo de permisos.", usuario?["motivoBloqueo"]?.GetValue<string>());

        var scripts = await LeerJsonAsync(await cliente.GetAsync("/api/scripts")) as JsonArray;
        Assert.NotNull(scripts);
        Assert.All(scripts!, script => Assert.True(script?["estaBloqueado"]?.GetValue<bool>()));

        using var cuerpo = new StringContent("{\"scriptId\":\"ok.ps1\"}", Encoding.UTF8, "application/json");
        var respuesta = await cliente.PostAsync("/api/ejecuciones", cuerpo);
        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task ApiAceptaPermisosFirmados()
    {
        using var entorno = EntornoPruebas.Crear();
        entorno.GuardarPermisosProtegidos(CrearPermisosAdmin());
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(
            entorno.CrearConfiguracion(),
            entorno.Artefactos);
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);

        var salud = await LeerJsonAsync(await cliente.GetAsync("/api/salud"));
        Assert.Equal("ok", salud?["estado"]?.GetValue<string>());
        Assert.Equal("Disponible", salud?["permisos"]?["estado"]?.GetValue<string>());

        var usuario = await LeerJsonAsync(await cliente.GetAsync("/api/usuario"));
        Assert.Equal("admin", usuario?["rol"]?.GetValue<string>());

        var texto = File.ReadAllText(entorno.RutaPermisos, Encoding.UTF8);
        Assert.Contains("\"usuarios\"", texto, StringComparison.Ordinal);
        Assert.Contains("\"Firma\"", texto, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiRechazaPermisosJsonPlano()
    {
        using var entorno = EntornoPruebas.Crear();
        File.WriteAllText(entorno.RutaPermisos, CrearPermisosAdmin().ToJsonString());
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(
            entorno.CrearConfiguracion(),
            entorno.Artefactos);
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);

        var salud = await LeerJsonAsync(await cliente.GetAsync("/api/salud"));
        Assert.Equal("degradado", salud?["estado"]?.GetValue<string>());
        Assert.Equal("Corrupto", salud?["permisos"]?["estado"]?.GetValue<string>());
    }

    [Fact]
    public async Task ApiBloqueaPermisosJsonMalFormado()
    {
        using var entorno = EntornoPruebas.Crear();
        File.WriteAllText(entorno.RutaPermisos, "{");
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(
            entorno.CrearConfiguracion(),
            entorno.Artefactos);
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);

        var salud = await LeerJsonAsync(await cliente.GetAsync("/api/salud"));
        Assert.Equal("degradado", salud?["estado"]?.GetValue<string>());
        Assert.Equal("Corrupto", salud?["permisos"]?["estado"]?.GetValue<string>());

        var usuario = await LeerJsonAsync(await cliente.GetAsync("/api/usuario"));
        Assert.True(usuario?["bloqueado"]?.GetValue<bool>());
    }

    [Fact]
    public async Task ApiDesbloqueaConTokenYNoRenuevaUnaSesionActiva()
    {
        using var entorno = EntornoPruebas.Crear();
        using var rsaToken = RSA.Create(3072);
        var servicioToken = new ServicioTokenMaestro(rsaToken, rsaToken);
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(
            entorno.CrearConfiguracionPermisosAusentes(),
            servicioToken,
            entorno.Artefactos);
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);

        var token = servicioToken.Generar();
        var cuerpo = new StringContent($"{{\"token\":\"{token}\"}}", Encoding.UTF8, "application/json");
        var primeraRespuesta = await cliente.PostAsync("/api/token-maestro/desbloquear", cuerpo);
        Assert.Equal(HttpStatusCode.OK, primeraRespuesta.StatusCode);

        var usuario = await LeerJsonAsync(await cliente.GetAsync("/api/usuario"));
        Assert.Equal("admin", usuario?["rol"]?.GetValue<string>());
        var tokenAdmin = usuario?["tokenAdmin"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(tokenAdmin));

        using var ajustes = new HttpRequestMessage(HttpMethod.Get, "/api/ajustes");
        ajustes.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tokenAdmin);
        var respuestaAjustes = await cliente.SendAsync(ajustes);
        Assert.Equal(HttpStatusCode.OK, respuestaAjustes.StatusCode);
        var jsonAjustes = await LeerJsonAsync(respuestaAjustes);
        Assert.Equal(ServidorLocalWeb.MensajeCarpetaPermisosNoDisponible, jsonAjustes?["avisoConexion"]?.GetValue<string>());

        var cuerpoReutilizado = new StringContent($"{{\"token\":\"{token}\"}}", Encoding.UTF8, "application/json");
        var segundaRespuesta = await cliente.PostAsync("/api/token-maestro/desbloquear", cuerpoReutilizado);
        Assert.Equal(HttpStatusCode.Conflict, segundaRespuesta.StatusCode);
    }

    [Fact]
    public async Task ApiEmergenciaSinAccesoRemotoBloqueaLecturasYEscrituras()
    {
        using var entorno = EntornoPruebas.Crear();
        using var rsaToken = RSA.Create(3072);
        var servicioToken = new ServicioTokenMaestro(rsaToken, rsaToken);
        var configuracion = entorno.CrearConfiguracionPermisosInaccesibles();
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(
            configuracion,
            servicioToken,
            entorno.Artefactos);
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);

        var token = servicioToken.Generar();
        using var desbloqueo = new StringContent($"{{\"token\":\"{token}\"}}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await cliente.PostAsync("/api/token-maestro/desbloquear", desbloqueo)).StatusCode);

        var usuario = await LeerJsonAsync(await cliente.GetAsync("/api/usuario"));
        var tokenAdmin = usuario?["tokenAdmin"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(tokenAdmin));

        using var guardar = new HttpRequestMessage(HttpMethod.Post, "/api/ajustes")
        {
            Content = new StringContent(CrearPermisosAdmin().ToJsonString(), Encoding.UTF8, "application/json")
        };
        guardar.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tokenAdmin);
        Assert.Equal(HttpStatusCode.Conflict, (await cliente.SendAsync(guardar)).StatusCode);
        Assert.False(Directory.Exists(configuracion.RutaPermisos));

        using var catalogo = new HttpRequestMessage(HttpMethod.Post, "/api/catalogo-scripts")
        {
            Content = new StringContent("{\"scriptIds\":[]}", Encoding.UTF8, "application/json")
        };
        catalogo.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tokenAdmin);
        Assert.Equal(HttpStatusCode.Conflict, (await cliente.SendAsync(catalogo)).StatusCode);

        using var exportar = new HttpRequestMessage(HttpMethod.Get, "/api/configuracion-paquete/exportar");
        exportar.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tokenAdmin);
        Assert.Equal(HttpStatusCode.Conflict, (await cliente.SendAsync(exportar)).StatusCode);

        using var subcarpetas = new HttpRequestMessage(HttpMethod.Get, "/api/subcarpetas-scripts");
        subcarpetas.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tokenAdmin);
        Assert.Equal(HttpStatusCode.Conflict, (await cliente.SendAsync(subcarpetas)).StatusCode);

        using var leerCatalogo = new HttpRequestMessage(HttpMethod.Get, "/api/catalogo-scripts");
        leerCatalogo.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tokenAdmin);
        Assert.Equal(HttpStatusCode.Conflict, (await cliente.SendAsync(leerCatalogo)).StatusCode);
    }

    [Fact]
    public async Task ApiGeneraTokenMaestroDesdeServicio()
    {
        using var entorno = EntornoPruebas.Crear();
        using var rsaToken = RSA.Create(3072);
        var servicioToken = new ServicioTokenMaestro(rsaToken, rsaToken);
        entorno.GuardarPermisosProtegidos(CrearPermisosAdmin());
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(
            entorno.CrearConfiguracion(),
            servicioToken,
            entorno.Artefactos);
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);

        using var respuestaUsuario = await cliente.GetAsync("/api/usuario");
        var usuario = await LeerJsonAsync(respuestaUsuario);
        var tokenAdmin = usuario?["tokenAdmin"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(tokenAdmin));

        using var peticion = new HttpRequestMessage(HttpMethod.Post, "/api/token-maestro/generar")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        peticion.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tokenAdmin);
        using var respuesta = await cliente.SendAsync(peticion);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var json = await LeerJsonAsync(respuesta);
        var token = json?["token"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(servicioToken.Validar(token!, out _, out var motivo), motivo);
    }

    [Fact]
    public async Task ApiAdminExigeBearer()
    {
        using var entorno = EntornoPruebas.Crear();
        entorno.GuardarPermisosProtegidos(CrearPermisosAdmin());
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(
            entorno.CrearConfiguracion(),
            entorno.Artefactos);
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);

        var sinBearer = await cliente.GetAsync("/api/ajustes");
        Assert.Equal(HttpStatusCode.Unauthorized, sinBearer.StatusCode);

        using var importarSinBearer = new StringContent("{}", Encoding.UTF8, "application/json");
        var respuestaImportarSinBearer = await cliente.PostAsync("/api/configuracion-paquete/importar", importarSinBearer);
        Assert.Equal(HttpStatusCode.Unauthorized, respuestaImportarSinBearer.StatusCode);

        using var generarSinBearer = new StringContent("{}", Encoding.UTF8, "application/json");
        var respuestaGenerarSinBearer = await cliente.PostAsync("/api/token-maestro/generar", generarSinBearer);
        Assert.Equal(HttpStatusCode.Unauthorized, respuestaGenerarSinBearer.StatusCode);

        using var bearerInvalido = new HttpRequestMessage(HttpMethod.Get, "/api/ajustes");
        bearerInvalido.Headers.TryAddWithoutValidation("Authorization", "Bearer invalido");
        var respuestaBearerInvalido = await cliente.SendAsync(bearerInvalido);
        Assert.Equal(HttpStatusCode.Forbidden, respuestaBearerInvalido.StatusCode);
    }

    [Fact]
    public async Task ApiNominalNoPuedePublicarCatalogo()
    {
        using var entorno = EntornoPruebas.Crear();
        var permisos = CrearPermisosAdmin();
        permisos["usuarios"]![0]!["rol"] = "nominal";
        entorno.GuardarPermisosProtegidos(permisos);
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(
            entorno.CrearConfiguracion(),
            entorno.Artefactos);
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);

        var usuario = await LeerJsonAsync(await cliente.GetAsync("/api/usuario"));
        Assert.Equal("nominal", usuario?["rol"]?.GetValue<string>());
        Assert.Null(usuario?["tokenAdmin"]);

        using var publicar = new HttpRequestMessage(HttpMethod.Post, "/api/catalogo-scripts")
        {
            Content = new StringContent("{\"scriptIds\":[\"ok.ps1\"]}", Encoding.UTF8, "application/json")
        };
        publicar.Headers.TryAddWithoutValidation("Authorization", "Bearer no-autorizado");

        Assert.Equal(HttpStatusCode.Forbidden, (await cliente.SendAsync(publicar)).StatusCode);
        Assert.False(File.Exists(ServicioCatalogoScripts.ObtenerRuta(entorno.RutaPermisos)));
    }

    [Theory]
    [InlineData(false, "nominal", "ok.ps1", 403)]
    [InlineData(true, "nominal", "sub/ok.cmd", 403)]
    [InlineData(true, "nominal", "ok.ps1", 200)]
    [InlineData(true, "admin", "sub/ok.cmd", 200)]
    [InlineData(true, "admin", "ausente.ps1", 404)]
    public async Task ApiDiagnosticoNoRevelaDatosDeScriptsNoAutorizados(bool registrado, string rol, string scriptId, int codigo)
    {
        using var entorno = EntornoPruebas.Crear();
        var permisos = CrearPermisosAdmin();
        if (registrado) permisos["usuarios"]![0]!["rol"] = rol;
        else permisos["usuarios"] = new JsonArray();
        entorno.GuardarPermisosProtegidos(permisos);
        entorno.GuardarCatalogo(["ok.ps1", "sub/ok.cmd"]);
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(entorno.CrearConfiguracion(), entorno.Artefactos);
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);
        using var respuesta = await cliente.GetAsync("/api/diagnostico-ejecucion?scriptId=" + Uri.EscapeDataString(scriptId));
        Assert.Equal(codigo, (int)respuesta.StatusCode);
        var cuerpo = await LeerJsonAsync(respuesta);
        if (codigo == 200) Assert.Equal(scriptId, cuerpo?["scriptId"]?.GetValue<string>());
        else
        {
            Assert.NotNull(cuerpo?["error"]);
            Assert.Null(cuerpo?["sha256"]);
            Assert.Null(cuerpo?["firma"]);
            Assert.Null(cuerpo?["executionPolicy"]);
            Assert.Null(cuerpo?["modoDesarrolloFirmas"]);
        }
    }

    [Fact]
    public async Task ApiNominalSoloListaCarpetasPermitidas()
    {
        using var entorno = EntornoPruebas.Crear();
        Directory.CreateDirectory(Path.Combine(entorno.Raiz, "privado"));
        File.WriteAllText(Path.Combine(entorno.Raiz, "privado", "a.ps1"), "Write-Output 1");

        var permisos = CrearPermisosAdmin();
        permisos["usuarios"]![0]!["rol"] = "nominal";
        permisos["usuarios"]![0]!["carpetasPermitidas"] = new JsonArray("sub");
        entorno.GuardarPermisosProtegidos(permisos);
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(
            entorno.CrearConfiguracion(),
            entorno.Artefactos);
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);

        var raiz = await LeerJsonAsync(await cliente.GetAsync("/api/scripts")) as JsonArray;
        Assert.NotNull(raiz);
        Assert.Contains(raiz!, script => script?["nombre"]?.GetValue<string>() == "ok.ps1");
        Assert.Contains(raiz!, script => script?["esCarpeta"]?.GetValue<bool>() == true && script?["carpeta"]?.GetValue<string>() == "sub");
        Assert.DoesNotContain(raiz!, script => script?["id"]?.GetValue<string>() == "sub/ok.cmd");
        Assert.DoesNotContain(raiz!, script => script?["carpeta"]?.GetValue<string>() == "privado");

        var sub = await LeerJsonAsync(await cliente.GetAsync("/api/scripts?carpeta=sub")) as JsonArray;
        Assert.NotNull(sub);
        Assert.Contains(sub!, script => script?["id"]?.GetValue<string>() == "sub/ok.cmd");
        Assert.DoesNotContain(sub!, script => script?["nombre"]?.GetValue<string>() == "ok.ps1");

        var privado = await LeerJsonAsync(await cliente.GetAsync("/api/scripts?carpeta=privado")) as JsonArray;
        Assert.NotNull(privado);
        Assert.Empty(privado!);

        var busqueda = await LeerJsonAsync(await cliente.GetAsync("/api/scripts?carpeta=privado&buscar=ok")) as JsonArray;
        Assert.NotNull(busqueda);
        Assert.Contains(busqueda!, script => script?["id"]?.GetValue<string>() == "sub/ok.cmd"
            && script?["carpeta"]?.GetValue<string>() == "sub");
        Assert.DoesNotContain(busqueda!, script => script?["esCarpeta"]?.GetValue<bool>() == true);
        var sinAcceso = await LeerJsonAsync(await cliente.GetAsync("/api/scripts?buscar=a.ps1")) as JsonArray;
        Assert.Empty(sinAcceso!);
        Assert.Equal(HttpStatusCode.BadRequest, (await cliente.GetAsync("/api/scripts?buscar=" + new string('a', 201))).StatusCode);
    }

    [Fact]
    public void ConfiguracionPredeterminadaNoLeeNiEscribeElArchivoLocal()
    {
        var servicio = new ServicioConfiguracion();
        var configuracion = servicio.Cargar();
        Assert.Equal("MAD002MICROPRU.mad.ae.aena.es", configuracion.ServidorCentral);
        Assert.Throws<InvalidOperationException>(() => servicio.Guardar(configuracion));
        Assert.Throws<InvalidOperationException>(() => servicio.AplicarRutasImportadas("C:\\otro", "C:\\permisos"));
        Assert.False(File.Exists(RutasAplicacion.RutaConfiguracionUsuario));
    }

    [Fact]
    public async Task ApiGuardaPermisosFirmadosConElMismoConjunto()
    {
        using var entorno = EntornoPruebas.Crear();
        entorno.GuardarPermisosProtegidos(CrearPermisosAdmin());
        entorno.GuardarCatalogo(["ok.ps1", "sub/ok.cmd"]);
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(
            entorno.CrearConfiguracion(),
            entorno.Artefactos);
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);

        var usuario = await LeerJsonAsync(await cliente.GetAsync("/api/usuario"));
        var tokenAdmin = usuario?["tokenAdmin"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(tokenAdmin));
        var permisosEntrada = CrearPermisosAdmin();

        using var peticion = new HttpRequestMessage(HttpMethod.Post, "/api/ajustes")
        {
            Content = new StringContent(permisosEntrada.ToJsonString(), Encoding.UTF8, "application/json")
        };
        peticion.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tokenAdmin);

        var respuesta = await cliente.SendAsync(peticion);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var texto = File.ReadAllText(entorno.RutaPermisos, Encoding.UTF8);
        Assert.Contains("\"usuarios\"", texto, StringComparison.Ordinal);
        Assert.Contains("\"Firma\"", texto, StringComparison.Ordinal);
        Assert.Contains("\"Contenido\"", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Datos\"", texto, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(entorno.Raiz, "permisos.json")));
        Assert.True(entorno.Artefactos.IntentarValidarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            texto,
            out var claro,
            out var conjuntoId,
            out _));
        Assert.Equal(entorno.ConjuntoId, conjuntoId);
        Assert.NotNull(JsonNode.Parse(claro) as JsonObject);
    }

    [Fact]
    public async Task EjecucionRealUsaCatalogoFirmado()
    {
        using var entorno = EntornoPruebas.Crear();
        entorno.GuardarPermisosProtegidos(CrearPermisosAdmin());
        entorno.GuardarCatalogo(["ok.ps1"]);
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(
            entorno.CrearConfiguracion(),
            entorno.Artefactos);
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);

        var usuario = await LeerJsonAsync(await cliente.GetAsync("/api/usuario"));
        var tokenAdmin = usuario?["tokenAdmin"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(tokenAdmin));

        using var publicarCatalogo = new HttpRequestMessage(HttpMethod.Post, "/api/catalogo-scripts")
        {
            Content = new StringContent("{\"scriptIds\":[\"ok.ps1\"]}", Encoding.UTF8, "application/json")
        };
        publicarCatalogo.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tokenAdmin);
        Assert.Equal(HttpStatusCode.OK, (await cliente.SendAsync(publicarCatalogo)).StatusCode);
        var rutaCatalogo = ServicioCatalogoScripts.ObtenerRuta(entorno.RutaPermisos);
        var catalogoFirmado = File.ReadAllText(rutaCatalogo, Encoding.UTF8);
        Assert.Contains("\"ok.ps1\"", catalogoFirmado, StringComparison.Ordinal);
        Assert.Contains("\"Firma\"", catalogoFirmado, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(entorno.Raiz, "catalogo-scripts.json")));

        using var cuerpo = new StringContent("{\"scriptId\":\"ok.ps1\"}", Encoding.UTF8, "application/json");
        var respuesta = await cliente.PostAsync("/api/ejecuciones", cuerpo);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var inicio = await LeerJsonAsync(respuesta);
        var id = inicio?["id"]?.GetValue<Guid>();
        Assert.NotNull(id);

        var eventos = await LeerEventosAsync(cliente, id!.Value);
        Assert.Contains(eventos, evento => evento.Contains("ok", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(eventos, evento => evento.Contains("Finalizada correctamente", StringComparison.OrdinalIgnoreCase));
    }

    private static HttpClient CrearCliente(ServidorLocalWeb servidor)
    {
        var cookies = new CookieContainer();
        var manejador = new HttpClientHandler
        {
            CookieContainer = cookies
        };

        return new HttpClient(manejador)
        {
            BaseAddress = servidor.UrlBase
        };
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

    private static async Task PrepararSesionAsync(HttpClient cliente, ServidorLocalWeb servidor)
    {
        _ = await cliente.GetAsync("/");
        cliente.DefaultRequestHeaders.Add("X-LanzadorScripts-ApiToken", servidor.TokenApiInterno);
    }

    private static async Task<JsonNode?> LeerJsonAsync(HttpResponseMessage respuesta)
    {
        var contenido = await respuesta.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(contenido) ? null : JsonNode.Parse(contenido);
    }

    private static async Task<IReadOnlyList<string>> LeerEventosAsync(HttpClient cliente, Guid id)
    {
        using var cancelacion = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var flujo = await cliente.GetStreamAsync($"/api/ejecuciones/{id}/eventos", cancelacion.Token);
        using var lector = new StreamReader(flujo, Encoding.UTF8);
        var eventos = new List<string>();
        while (!cancelacion.IsCancellationRequested)
        {
            var linea = await lector.ReadLineAsync(cancelacion.Token);
            if (linea is null)
            {
                break;
            }

            if (linea.StartsWith("data: ", StringComparison.Ordinal))
            {
                eventos.Add(linea);
                if (linea.Contains("finalizada", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
        }

        return eventos;
    }

    private static JsonObject CrearPermisosBase()
    {
        return new JsonObject
        {
            ["scriptsAdmin"] = new JsonArray(),
            ["usuarios"] = new JsonArray(),
            ["seguridadScripts"] = new JsonObject
            {
                ["scriptsElevadosPermitidos"] = new JsonArray(),
                ["permitirExecutionPolicyBypass"] = false
            },
            ["rolUsuarioActual"] = "nominal",
            ["maxScriptsSimultaneos"] = 5
        };
    }

    private static JsonObject CrearPermisosAdmin()
    {
        var permisos = CrearPermisosBase();
        permisos["usuarios"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "admin-local",
                ["nombreUsuario"] = WindowsIdentity.GetCurrent().Name,
                ["rol"] = "admin",
                ["maxScriptsSimultaneos"] = 5,
                ["carpetasPermitidas"] = new JsonArray()
            }
        };
        return permisos;
    }
}

internal sealed class EntornoPruebas : IDisposable
{
    private readonly RSA _rsaArtefactos;

    private EntornoPruebas(string raiz)
    {
        Raiz = raiz;
        CarpetaPermisos = Path.Combine(Raiz, "PERMISOS");
        RutaPermisos = Path.Combine(CarpetaPermisos, RutasArtefactosProtegidos.NombrePermisos);
        _rsaArtefactos = RSA.Create(3072);
        Artefactos = new ServicioArtefactosFirmados(_rsaArtefactos, _rsaArtefactos);
        ConjuntoId = ServicioArtefactosFirmados.CrearConjuntoId();
    }

    public string Raiz { get; }

    public string RutaPermisos { get; }

    public string CarpetaPermisos { get; }

    public ServicioArtefactosFirmados Artefactos { get; }

    public string ConjuntoId { get; }

    public static EntornoPruebas Crear()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "LanzadorScripts_Pruebas_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(raiz);
        File.WriteAllText(Path.Combine(raiz, "ok.ps1"), "Write-Output 'ok'");
        Directory.CreateDirectory(Path.Combine(raiz, "sub"));
        File.WriteAllText(Path.Combine(raiz, "sub", "ok.cmd"), "echo ok");
        Directory.CreateDirectory(Path.Combine(raiz, "vacia"));
        Directory.CreateDirectory(Path.Combine(raiz, "PERMISOS"));
        Directory.CreateDirectory(Path.Combine(raiz, "PERMISOS", ServicioAuditoria.NombreCarpetaAuditoria));
        File.WriteAllText(Path.Combine(raiz, "PERMISOS", "bloqueado.ps1"), "Write-Output 'no'");
        Directory.CreateDirectory(Path.Combine(raiz, ".git"));
        File.WriteAllText(Path.Combine(raiz, ".git", "bloqueado.ps1"), "Write-Output 'no'");
        File.WriteAllText(Path.Combine(raiz, "texto.txt"), "no");
        File.WriteAllText(Path.Combine(raiz, "bad&name.ps1"), "Write-Output 'no'");
        return new EntornoPruebas(raiz);
    }

    public ConfiguracionLanzador CrearConfiguracion()
    {
        return new ConfiguracionLanzador
        {
            RutaScripts = Raiz,
            RutaPermisos = CarpetaPermisos,
            RutaLogs = Path.Combine(Raiz, "Logs")
        };
    }

    public ConfiguracionLanzador CrearConfiguracionPermisosAusentes()
    {
        var carpetaPermisosAusentes = Path.Combine(Raiz, "PERMISOS-AUSENTES");
        Directory.CreateDirectory(carpetaPermisosAusentes);
        return new ConfiguracionLanzador
        {
            RutaScripts = Raiz,
            RutaPermisos = carpetaPermisosAusentes,
            RutaLogs = Path.Combine(Raiz, "Logs")
        };
    }

    public ConfiguracionLanzador CrearConfiguracionPermisosInaccesibles()
    {
        return new ConfiguracionLanzador
        {
            RutaScripts = Raiz,
            RutaPermisos = Path.Combine(Raiz, "PERMISOS-INACCESIBLES"),
            RutaLogs = Path.Combine(Raiz, "Logs")
        };
    }

    public void GuardarPermisosProtegidos(JsonObject permisos)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RutaPermisos)!);
        Artefactos.GuardarTextoFirmado(
            RutaPermisos,
            ServicioArtefactosFirmados.TipoPermisos,
            permisos.ToJsonString(),
            ConjuntoId);
    }

    public void GuardarCatalogo(IEnumerable<string> scriptIds)
    {
        var validador = new ServicioValidacionScripts();
        var servicio = new ServicioCatalogoScripts(Artefactos);
        var catalogo = servicio.Crear(
            validador.DescubrirScripts(Raiz),
            scriptIds,
            ConjuntoId);
        servicio.Guardar(ServicioCatalogoScripts.ObtenerRuta(RutaPermisos), catalogo);
    }

    public void Dispose()
    {
        _rsaArtefactos.Dispose();
        try
        {
            Directory.Delete(Raiz, recursive: true);
        }
        catch
        {
        }
    }
}
