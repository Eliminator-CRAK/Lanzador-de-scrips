// (Autor: Alex Roman)
// Descripcion: Valida la publicacion y actualizacion opcional del cliente MSI.

using System.Security.Cryptography;
using System.Text.Json;
using LanzadorScripts.Protocolo;
using LanzadorScripts.Servicios;
using LanzadorScripts.Servidor.Core;
using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasActualizaciones
{
    [Fact]
    public void CatalogoSeleccionaVersionMayorYRetiraLaEliminada()
    {
        var raiz = CrearDirectorioTemporal();
        try
        {
            var anterior = CrearMsiFicticio(raiz, "1.9.1", 16);
            var reciente = CrearMsiFicticio(raiz, "1.10.0", 32);
            var catalogo = new CatalogoActualizacionesServidor(
                raiz,
                CrearResultadoValido);

            var estado = catalogo.ObtenerEstado();
            var actualizacion = catalogo.ObtenerActualizacion(
                new ConsultaActualizacionCliente("1.9.0", "x64", "Instalada"));

            Assert.Equal("1.10.0", estado.VersionActiva);
            Assert.True(actualizacion.Disponible);
            Assert.Equal(Path.GetFileName(reciente), actualizacion.NombreArchivo);
            Assert.Equal(CatalogoActualizacionesServidor.NombreRecursoCompartido,
                actualizacion.RecursoCompartido);

            File.Delete(reciente);
            estado = catalogo.ObtenerEstado();

            Assert.Equal("1.9.1", estado.VersionActiva);
            Assert.Single(estado.Paquetes);
            Assert.Equal(Path.GetFileName(anterior), estado.Paquetes[0].NombreArchivo);
        }
        finally
        {
            EliminarDirectorioTemporal(raiz);
        }
    }

    [Fact]
    public void CatalogoCacheaPorRutaTamanoYFecha()
    {
        var raiz = CrearDirectorioTemporal();
        try
        {
            var ruta = CrearMsiFicticio(raiz, "1.9.1", 16);
            var validaciones = 0;
            var catalogo = new CatalogoActualizacionesServidor(
                raiz,
                archivo =>
                {
                    validaciones++;
                    return CrearResultadoValido(archivo);
                });

            _ = catalogo.ObtenerEstado();
            _ = catalogo.ObtenerEstado();
            Assert.Equal(1, validaciones);

            File.AppendAllText(ruta, "cambio");
            File.SetLastWriteTimeUtc(ruta, DateTime.UtcNow.AddSeconds(2));
            _ = catalogo.ObtenerEstado();
            Assert.Equal(2, validaciones);

            _ = catalogo.ObtenerEstado(forzarValidacion: true);
            Assert.Equal(3, validaciones);
        }
        finally
        {
            EliminarDirectorioTemporal(raiz);
        }
    }

    [Fact]
    public void ValidadorRechazaMsiIncompletoYBloqueado()
    {
        var raiz = CrearDirectorioTemporal();
        try
        {
            var ruta = CrearMsiFicticio(raiz, "1.9.1", 32);
            var incompleto = ValidadorPaqueteActualizacion.Validar(ruta);
            Assert.False(incompleto.Valido);

            using var bloqueo = new FileStream(
                ruta,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            var bloqueado = ValidadorPaqueteActualizacion.Validar(ruta);
            Assert.False(bloqueado.Valido);
        }
        finally
        {
            EliminarDirectorioTemporal(raiz);
        }
    }

    [Fact]
    public void ValidadorDevuelveRechazoParaNombreDeVersionIncompatible()
    {
        var raiz = CrearDirectorioTemporal();
        try
        {
            var ruta = Path.Combine(raiz, "LanzadorScripts-1.9.1.4-x64.msi");
            File.WriteAllBytes(ruta, [1, 2, 3, 4]);

            var resultado = ValidadorPaqueteActualizacion.Validar(ruta);

            Assert.False(resultado.Valido);
            Assert.Contains("formato", resultado.Mensaje, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            EliminarDirectorioTemporal(raiz);
        }
    }

    [Fact]
    public void ConsultaActualizacionExigeUsuarioActivo()
    {
        var raiz = CrearDirectorioTemporal();
        try
        {
            var rutas = new RutasServidor(Path.Combine(raiz, "Servidor"));
            var configuracion = new ConfiguracionServidor
            {
                RutaScripts = Path.Combine(raiz, "Scripts")
            };
            Directory.CreateDirectory(configuracion.RutaScripts);
            configuracion.Validar();
            rutas.PrepararDirectorios();
            using var repositorio = new RepositorioServidor(
                rutas,
                configuracion,
                RandomNumberGenerator.GetBytes(32));
            repositorio.Inicializar(@"PCERA\alero");
            repositorio.GuardarUsuario(new GuardarUsuarioServidorCentral(
                null,
                @"MAD00\usuario_activo",
                "nominal",
                2,
                [],
                true));
            repositorio.GuardarUsuario(new GuardarUsuarioServidorCentral(
                null,
                @"MAD00\usuario_inactivo",
                "nominal",
                2,
                [],
                false));

            var carpeta = Path.Combine(raiz, "Actualizaciones");
            Directory.CreateDirectory(carpeta);
            _ = CrearMsiFicticio(carpeta, "1.9.1", 24);
            var procesador = new ProcesadorSolicitudesServidor(
                repositorio,
                catalogoActualizaciones: new CatalogoActualizacionesServidor(
                    carpeta,
                    CrearResultadoValido));

            var activa = procesador.Procesar(
                @"MAD00\usuario_activo",
                CrearSolicitudActualizacion());
            var inactiva = procesador.Procesar(
                @"MAD00\usuario_inactivo",
                CrearSolicitudActualizacion());
            var desconocida = procesador.Procesar(
                @"MAD00\usuario_desconocido",
                CrearSolicitudActualizacion());

            Assert.True(activa.Exito, activa.Mensaje);
            Assert.True(activa.Datos.Deserialize<ActualizacionClienteServidor>(
                TransporteProtocolo.OpcionesJson)!.Disponible);
            Assert.Equal("acceso_denegado", inactiva.Codigo);
            Assert.Equal("acceso_denegado", desconocida.Codigo);
        }
        finally
        {
            EliminarDirectorioTemporal(raiz);
        }
    }

    [Fact]
    public async Task PortableNuncaConsultaElServidorDeActualizaciones()
    {
        var consultoConfiguracion = false;
        var distribucion = new ContextoDistribucion(
            TipoDistribucion.Portable,
            @"C:\Temp\LanzadorScripts\Portable\Sesion-00000000000000000000000000000001",
            @"C:\Temp\LanzadorScripts\Ejecucion\Sesion-00000000000000000000000000000002");
        var servicio = new ServicioActualizacionesCliente(
            () =>
            {
                consultoConfiguracion = true;
                throw new InvalidOperationException();
            },
            distribucion);

        var resultado = await servicio.ConsultarAsync(CancellationToken.None);

        Assert.False(resultado.Disponible);
        Assert.False(consultoConfiguracion);
        Assert.Contains("portable", resultado.Mensaje, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RutaRemotaEsCerradaYRechazaTraversal()
    {
        var valida = new ActualizacionClienteServidor(
            true,
            "1.9.1",
            "LanzadorScripts-1.9.1-x64.msi",
            CatalogoActualizacionesServidor.NombreRecursoCompartido,
            100,
            new string('A', 64),
            DateTimeOffset.UtcNow);

        var ruta = ServicioActualizacionesCliente.ConstruirRutaRemota(
            "servidor.dominio.local",
            valida);
        Assert.Equal(
            @"\\servidor.dominio.local\LanzadorScriptsActualizaciones$\LanzadorScripts-1.9.1-x64.msi",
            ruta);

        Assert.Throws<InvalidDataException>(() =>
            ServicioActualizacionesCliente.ConstruirRutaRemota(
                "servidor.dominio.local",
                valida with { NombreArchivo = "..\\otro.msi" }));
        Assert.Throws<InvalidDataException>(() =>
            ServicioActualizacionesCliente.ConstruirRutaRemota(
                "servidor\\otro",
                valida));
        Assert.Throws<InvalidDataException>(() =>
            ServicioActualizacionesCliente.ConstruirRutaRemota(
                "servidor.dominio.local",
                valida with { RecursoCompartido = "Otro$" }));
        Assert.Throws<InvalidDataException>(() =>
            ServicioActualizacionesCliente.ConstruirRutaRemota(
                "servidor.dominio.local",
                valida with { NombreArchivo = null! }));
    }

    [Fact]
    public void ClienteYActualizadorAplicanElContratoOpcionalSeguro()
    {
        var ventana = Leer("VentanaPrincipal.xaml");
        var codigoVentana = Leer("VentanaPrincipal.xaml.cs");
        var aplicacion = Leer("Aplicacion.xaml.cs");
        var cliente = Leer("Servicios", "ServicioActualizacionesCliente.cs");
        var actualizador = Leer("Actualizador", "LanzadorScripts.Actualizador.cpp");

        Assert.Contains("x:Name=\"BotonActualizarAplicacion\"", ventana, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Collapsed\"", ventana, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PanelActualizacion\"", ventana, StringComparison.Ordinal);
        Assert.Contains("Descargando...", codigoVentana, StringComparison.Ordinal);
        Assert.Contains("Verificando...", codigoVentana, StringComparison.Ordinal);
        Assert.Contains("Actualizando...", codigoVentana, StringComparison.Ordinal);
        Assert.Contains("_comprobacionActualizacionIniciada", codigoVentana, StringComparison.Ordinal);
        Assert.Contains("ReintentarLimpiezaStagingAbandonadoAsync", aplicacion, StringComparison.Ordinal);
        Assert.Contains("_distribucion.EsPortable", cliente, StringComparison.Ordinal);
        Assert.Contains("ValidadorPaqueteActualizacion.Validar", cliente, StringComparison.Ordinal);
        Assert.Contains("WinVerifyTrust", actualizador, StringComparison.Ordinal);
        Assert.Contains(ValidadorPaqueteActualizacion.UpgradeCodeEsperado, actualizador, StringComparison.Ordinal);
        Assert.Contains(ValidadorPaqueteActualizacion.HuellaFirmaEsperada, actualizador, StringComparison.Ordinal);
        Assert.Contains("/passive /norestart REBOOT=ReallySuppress", actualizador, StringComparison.Ordinal);
        Assert.Contains("CodigoReinicioNecesario = 3010", actualizador, StringComparison.Ordinal);
        Assert.Contains("if (codigo == ERROR_TIMEOUT)", actualizador, StringComparison.Ordinal);
        Assert.Contains("HANDLE bloqueoMsi = INVALID_HANDLE_VALUE", actualizador, StringComparison.Ordinal);
        Assert.Contains("const DWORD codigo = EjecutarMsi(rutaMsi);\n    CloseHandle(bloqueoMsi);", actualizador, StringComparison.Ordinal);
        Assert.DoesNotContain("powershell.exe", actualizador, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwsh.exe", actualizador, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd.exe", actualizador, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServidorPreparaRepositorioPersistenteYVistaDeActualizaciones()
    {
        var rutas = Leer("Servidor", "LanzadorScripts.Servidor.Core", "RutasServidor.cs");
        var control = Leer(
            "Servidor",
            "LanzadorScripts.Servidor.Administracion",
            "ServicioControlWindows.cs");
        var catalogo = Leer(
            "Servidor",
            "LanzadorScripts.Servidor.Core",
            "CatalogoActualizacionesServidor.cs");
        var instalador = Leer("Servidor", "Distribucion", "Instalar-Servidor.ps1");
        var desinstalador = Leer("Servidor", "Distribucion", "Desinstalar-Servidor.ps1");
        var ventana = Leer(
            "Servidor",
            "LanzadorScripts.Servidor.Administracion",
            "MainWindow.xaml");

        Assert.Contains("RutaActualizaciones", rutas, StringComparison.Ordinal);
        Assert.Contains("AuthenticatedUserSid", rutas, StringComparison.Ordinal);
        Assert.Contains("LocalSystemSid", rutas, StringComparison.Ordinal);
        Assert.Contains("CatalogoActualizacionesServidor.NombreRecursoCompartido", control, StringComparison.Ordinal);
        Assert.Contains("LanzadorScriptsActualizaciones$", catalogo, StringComparison.Ordinal);
        Assert.Contains("WellKnownSidType.LocalSystemSid", control, StringComparison.Ordinal);
        Assert.Contains("/GRANT:{sistema},FULL", control, StringComparison.Ordinal);
        Assert.Contains("/GRANT:$sistemaLocal,FULL", instalador, StringComparison.Ordinal);
        Assert.Contains("if ($EliminarDatos)", desinstalador, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BotonActualizaciones\"", ventana, StringComparison.Ordinal);
        Assert.Contains("Text=\"Actualizaciones\"", ventana, StringComparison.Ordinal);
        Assert.Contains("Abrir carpeta", ventana, StringComparison.Ordinal);
        Assert.Contains("Volver a validar", ventana, StringComparison.Ordinal);
    }

    private static SolicitudServidor CrearSolicitudActualizacion()
    {
        return new SolicitudServidor(
            TransporteProtocolo.VersionActual,
            Guid.NewGuid(),
            OperacionesServidor.ObtenerActualizacion,
            TransporteProtocolo.CrearDatos(
                new ConsultaActualizacionCliente("1.9.0", "x64", "Instalada")));
    }

    private static string CrearMsiFicticio(string carpeta, string version, int longitud)
    {
        Directory.CreateDirectory(carpeta);
        var ruta = Path.Combine(carpeta, $"LanzadorScripts-{version}-x64.msi");
        File.WriteAllBytes(ruta, Enumerable.Repeat((byte)0x5A, longitud).ToArray());
        return ruta;
    }

    private static ResultadoValidacionPaqueteActualizacion CrearResultadoValido(string ruta)
    {
        var archivo = new FileInfo(ruta);
        var version = Version.Parse(
            archivo.Name["LanzadorScripts-".Length..^"-x64.msi".Length]);
        return new ResultadoValidacionPaqueteActualizacion(
            true,
            archivo.Name,
            version,
            archivo.Length,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ruta))),
            new DateTimeOffset(archivo.LastWriteTimeUtc, TimeSpan.Zero),
            "Valida",
            string.Empty);
    }

    private static string CrearDirectorioTemporal()
    {
        var ruta = Path.Combine(
            Path.GetTempPath(),
            "LanzadorScriptsActualizacionesPruebas",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ruta);
        return ruta;
    }

    private static void EliminarDirectorioTemporal(string ruta)
    {
        try
        {
            Directory.Delete(ruta, recursive: true);
        }
        catch
        {
        }
    }

    private static string Leer(params string[] partes)
    {
        return File.ReadAllText(Path.Combine([ObtenerRaizProyecto(), .. partes]));
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
