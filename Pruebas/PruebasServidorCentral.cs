// (Autor: Alex Roman)
// Descripcion: Comprueba el cifrado, permisos, catalogo y auditoria del servidor central.

using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LanzadorScripts.Protocolo;
using LanzadorScripts.Servidor.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasServidorCentral
{
    [Fact]
    public void ClientePriorizaElSpnPropioYConservaHostComoCompatibilidad()
    {
        var candidatos = AutenticacionServidorCentral.CrearSpnCandidatos(
            "servidor.dominio.local");

        Assert.Equal(
            ["LanzadorScripts/servidor.dominio.local", "HOST/servidor.dominio.local"],
            candidatos);
        Assert.Equal(
            "servidor.dominio.local",
            AutenticacionServidorCentral.NormalizarServidor(" servidor.dominio.local. "));
        Assert.Throws<ArgumentException>(() =>
            AutenticacionServidorCentral.CrearSpnCandidatos("servidor/ruta"));
        Assert.Throws<ArgumentException>(() =>
            AutenticacionServidorCentral.CrearSpnCandidatos("127.0.0.1"));
    }

    [Fact]
    public void ClienteUsaElPipeSoloParaElEquipoWindowsActual()
    {
        string[] nombresLocales =
        [
            "MAD002MICROPRU",
            "MAD002MICROPRU.mad.ae.aena.es"
        ];

        Assert.True(DetectorServidorLocal.CoincideConNombreLocal(
            "mad002micropru",
            nombresLocales));
        Assert.True(DetectorServidorLocal.CoincideConNombreLocal(
            "MAD002MICROPRU.MAD.AE.AENA.ES.",
            nombresLocales));
        Assert.False(DetectorServidorLocal.CoincideConNombreLocal(
            "localhost",
            nombresLocales));
        Assert.False(DetectorServidorLocal.CoincideConNombreLocal(
            "MAD002MICROPRU2.mad.ae.aena.es",
            nombresLocales));

        var clienteLocal = new ClienteServidorCentral(Environment.MachineName, 47831);
        var clienteRemoto = new ClienteServidorCentral("servidor-remoto.invalid", 47831);

        Assert.True(clienteLocal.UsaCanalLocal);
        Assert.False(clienteRemoto.UsaCanalLocal);
    }

    [Fact]
    public void RegistroSpnUsaAgregarYEliminarDesdeLocalSystem()
    {
        var operaciones = new List<OperacionRegistroSpn>();
        var registro = new RegistroSpnServidor(
            () => true,
            operacion =>
            {
                operaciones.Add(operacion);
                return 0;
            });

        var alta = registro.Registrar();
        var baja = registro.Eliminar();

        Assert.True(alta.Exito, alta.Mensaje);
        Assert.True(baja.Exito, baja.Mensaje);
        Assert.Equal(0U, (uint)OperacionRegistroSpn.Agregar);
        Assert.Equal(2U, (uint)OperacionRegistroSpn.Eliminar);
        Assert.Equal([OperacionRegistroSpn.Agregar, OperacionRegistroSpn.Eliminar], operaciones);
        Assert.StartsWith("LanzadorScripts/", alta.SpnPrincipal, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistroSpnRechazaProcesosQueNoSonLocalSystem()
    {
        var invocado = false;
        var registro = new RegistroSpnServidor(
            () => false,
            _ =>
            {
                invocado = true;
                return 0;
            });

        var resultado = registro.Registrar();

        Assert.False(resultado.Exito);
        Assert.Equal(5U, resultado.CodigoWin32);
        Assert.False(invocado);
    }

    [Fact]
    public void SaludInformaElEstadoRealDeKerberos()
    {
        using var entorno = EntornoServidor.Crear();
        var autenticacion = new EstadoAutenticacionServidor(
            true,
            "LanzadorScripts/servidor.dominio.local",
            "SPN Kerberos registrado en la cuenta de equipo.");
        var procesador = new ProcesadorSolicitudesServidor(
            entorno.Repositorio,
            () => autenticacion);
        var solicitud = new SolicitudServidor(
            TransporteProtocolo.VersionActual,
            Guid.NewGuid(),
            OperacionesServidor.Salud,
            TransporteProtocolo.CrearDatos(new { }));

        var respuesta = procesador.Procesar(@"PCERA\alero", solicitud);
        var estado = respuesta.Datos.Deserialize<EstadoServidorCentral>(
            TransporteProtocolo.OpcionesJson);

        Assert.True(respuesta.Exito, respuesta.Mensaje);
        Assert.NotNull(estado);
        Assert.True(estado.AutenticacionRemotaPreparada);
        Assert.Equal(autenticacion.SpnPrincipal, estado.SpnServidor);
        Assert.Equal(autenticacion.Mensaje, estado.MensajeAutenticacion);
    }

    [Fact]
    public async Task CanalAdministrativoIdentificaClienteDespuesDeLeerLaSolicitud()
    {
        var nombreCanal = $"LanzadorScripts.Pruebas.{Guid.NewGuid():N}";
        using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var servidor = new NamedPipeServerStream(
            nombreCanal,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var cliente = new NamedPipeClientStream(
            ".",
            nombreCanal,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);

        var conexion = servidor.WaitForConnectionAsync(limite.Token);
        await cliente.ConnectAsync(5000, limite.Token);
        await conexion;

        var solicitud = new SolicitudServidor(
            TransporteProtocolo.VersionActual,
            Guid.NewGuid(),
            OperacionesServidor.Salud,
            TransporteProtocolo.CrearDatos(new { }));
        var escritura = TransporteProtocolo.EscribirAsync(cliente, solicitud, limite.Token);
        _ = await TransporteProtocolo.LeerAsync<SolicitudServidor>(servidor, limite.Token);
        await escritura;

        var actual = IdentidadClienteCanalLocal.ObtenerCuenta(servidor);
        var esperada = ConfiguracionServidor.NormalizarCuenta(
            WindowsIdentity.GetCurrent().Name);

        Assert.NotEmpty(esperada);
        Assert.Equal(esperada, actual, ignoreCase: true);
    }

    [Fact]
    public void ConfiguracionRetiraAdministradoresInicialesLegados()
    {
        var raiz = Path.Combine(
            Path.GetTempPath(),
            "LanzadorScriptsServidorPruebas",
            Guid.NewGuid().ToString("N"));
        var rutas = new RutasServidor(raiz);
        try
        {
            rutas.PrepararDirectorios();
            var rutaScripts = Path.Combine(raiz, "Scripts");
            var legado = JsonSerializer.Serialize(new
            {
                version = 1,
                puerto = 47831,
                maximoConexiones = 64,
                diasRetencionAuditoria = 3650,
                rutaScripts,
                administradoresIniciales = new[] { @"MAD00\aroperez_micro" }
            });
            File.WriteAllText(rutas.RutaConfiguracion, legado, new UTF8Encoding(false));

            var configuracion = new AlmacenConfiguracionServidor(rutas).CargarOCrear();
            var persistida = File.ReadAllText(rutas.RutaConfiguracion, Encoding.UTF8);

            Assert.Equal(rutaScripts, configuracion.RutaScripts, ignoreCase: true);
            Assert.DoesNotContain("administradoresIniciales", persistida, StringComparison.Ordinal);
            Assert.DoesNotContain(@"MAD00\aroperez_micro", persistida, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            EliminarDirectorioPruebas(raiz);
        }
    }

    [Fact]
    public void AdministradorInicialSeProtegeYSeConsumeUnaSolaVez()
    {
        var raiz = Path.Combine(
            Path.GetTempPath(),
            "LanzadorScriptsServidorPruebas",
            Guid.NewGuid().ToString("N"));
        var rutas = new RutasServidor(raiz);
        try
        {
            var almacen = new AlmacenAdministradorInicialServidor(
                rutas,
                new ProtectorTransformacionPruebas());
            almacen.Preparar(@"MAD00\aroperez_micro");
            var contenido = Encoding.UTF8.GetString(
                File.ReadAllBytes(rutas.RutaAdministradorInicialProtegido));

            Assert.DoesNotContain(@"MAD00\aroperez_micro", contenido, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(@"MAD00\aroperez_micro", almacen.Leer());
            almacen.Eliminar();
            Assert.False(File.Exists(rutas.RutaAdministradorInicialProtegido));
        }
        finally
        {
            EliminarDirectorioPruebas(raiz);
        }
    }

    [Fact]
    public void BaseCentralCifraUsuariosYPermisos()
    {
        using var entorno = EntornoServidor.Crear();
        var permisos = entorno.Repositorio.ObtenerPermisos(@"PCERA\alero", incluirTodos: true);

        Assert.Contains(permisos.Permisos["usuarios"]!.AsArray(), usuario =>
            usuario?["nombreUsuario"]?.GetValue<string>() == @"PCERA\alero");
        byte[] bytes;
        using (var flujo = new FileStream(
            entorno.Rutas.RutaBaseDatos,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[flujo.Length];
            flujo.ReadExactly(bytes);
        }

        var contenido = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(@"PCERA\alero", contenido, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"MAD00\aroperez_micro", contenido, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BaseCentralDetectaUnaFilaManipulada()
    {
        using var entorno = EntornoServidor.Crear();
        using (var conexion = new SqliteConnection($"Data Source={entorno.Rutas.RutaBaseDatos}"))
        {
            conexion.Open();
            using var comando = conexion.CreateCommand();
            comando.CommandText = "UPDATE Usuarios SET Datos = randomblob(length(Datos)) WHERE Id = (SELECT Id FROM Usuarios LIMIT 1);";
            comando.ExecuteNonQuery();
        }

        Assert.ThrowsAny<CryptographicException>(() => entorno.Repositorio.ListarUsuarios());
    }

    [Fact]
    public void BaseCentralCifraTambienLosValoresDeMetadatos()
    {
        using var entorno = EntornoServidor.Crear();
        var conjuntoId = entorno.Repositorio.ObtenerPermisos(
            @"PCERA\alero",
            incluirTodos: true).ConjuntoId;
        byte[] bytes;
        using (var flujo = new FileStream(
            entorno.Rutas.RutaBaseDatos,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[flujo.Length];
            flujo.ReadExactly(bytes);
        }

        var contenido = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(conjuntoId, contenido, StringComparison.Ordinal);
        Assert.Contains("Metadatos", contenido, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegridadDetectaMetadatosCifradosManipulados()
    {
        using var entorno = EntornoServidor.Crear();
        using (var conexion = new SqliteConnection($"Data Source={entorno.Rutas.RutaBaseDatos}"))
        {
            conexion.Open();
            using var comando = conexion.CreateCommand();
            comando.CommandText = "UPDATE Metadatos SET Datos = randomblob(length(Datos)) WHERE Clave = 'conjunto_id';";
            comando.ExecuteNonQuery();
        }

        var resultado = entorno.Repositorio.ComprobarIntegridad();
        Assert.False(resultado.Integra);
    }

    [Fact]
    public void IntegridadDetectaColumnasDeAuditoriaManipuladas()
    {
        using var entorno = EntornoServidor.Crear();
        entorno.Repositorio.RegistrarAuditoria(CrearEventoAuditoria());
        using (var conexion = new SqliteConnection($"Data Source={entorno.Rutas.RutaBaseDatos}"))
        {
            conexion.Open();
            using var comando = conexion.CreateCommand();
            comando.CommandText = "UPDATE Auditoria SET FechaUtcTicks = FechaUtcTicks + 1;";
            comando.ExecuteNonQuery();
        }

        var resultado = entorno.Repositorio.ComprobarIntegridad();
        Assert.False(resultado.Integra);
    }

    [Fact]
    public void EstadoUsaElUltimoDiagnosticoSinRepetirLaComprobacionCompleta()
    {
        using var entorno = EntornoServidor.Crear();
        Assert.True(entorno.Repositorio.ComprobarIntegridad().Integra);
        entorno.Repositorio.RegistrarAuditoria(CrearEventoAuditoria());
        using (var conexion = new SqliteConnection($"Data Source={entorno.Rutas.RutaBaseDatos}"))
        {
            conexion.Open();
            using var comando = conexion.CreateCommand();
            comando.CommandText = "UPDATE Auditoria SET FechaUtcTicks = FechaUtcTicks + 1;";
            comando.ExecuteNonQuery();
        }

        Assert.True(entorno.Repositorio.ObtenerEstado().BaseIntegra);
        Assert.False(entorno.Repositorio.ComprobarIntegridad().Integra);
        Assert.False(entorno.Repositorio.ObtenerEstado().BaseIntegra);
    }

    [Fact]
    public void SaludSoloSeEntregaACuentasAutorizadas()
    {
        using var entorno = EntornoServidor.Crear();
        Assert.True(entorno.Repositorio.ComprobarIntegridad().Integra);
        var procesador = new ProcesadorSolicitudesServidor(entorno.Repositorio);
        var solicitud = new SolicitudServidor(
            TransporteProtocolo.VersionActual,
            Guid.NewGuid(),
            OperacionesServidor.Salud,
            TransporteProtocolo.CrearDatos(new { }));

        var denegada = procesador.Procesar(@"MAD00\cuenta_no_autorizada", solicitud);
        var autorizada = procesador.Procesar(@"PCERA\alero", solicitud);

        Assert.False(denegada.Exito);
        Assert.Equal("acceso_denegado", denegada.Codigo);
        Assert.True(autorizada.Exito, autorizada.Mensaje);
    }

    [Fact]
    public void InicioMigraMetadatosAnterioresAlFormatoCifrado()
    {
        var raiz = Path.Combine(
            Path.GetTempPath(),
            "LanzadorScriptsServidorPruebas",
            Guid.NewGuid().ToString("N"));
        var rutas = new RutasServidor(raiz);
        var configuracion = new ConfiguracionServidor();
        rutas.PrepararDirectorios();
        new AlmacenConfiguracionServidor(rutas).Guardar(configuracion);
        using (var conexion = new SqliteConnection($"Data Source={rutas.RutaBaseDatos}"))
        {
            conexion.Open();
            using var comando = conexion.CreateCommand();
            comando.CommandText = """
                CREATE TABLE Metadatos (Clave TEXT PRIMARY KEY NOT NULL, Valor TEXT NOT NULL) STRICT;
                INSERT INTO Metadatos VALUES ('version_esquema', '1');
                INSERT INTO Metadatos VALUES ('conjunto_id', 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA');
                INSERT INTO Metadatos VALUES ('revision_permisos', '1');
                INSERT INTO Metadatos VALUES ('revision_catalogo', '1');
                """;
            comando.ExecuteNonQuery();
        }

        var clave = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var repositorio = new RepositorioServidor(rutas, configuracion, clave);
            repositorio.Inicializar(@"PCERA\alero");
            Assert.True(repositorio.ComprobarIntegridad().Integra);
            using var conexion = new SqliteConnection($"Data Source={rutas.RutaBaseDatos}");
            conexion.Open();
            using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT group_concat(name, ',') FROM pragma_table_info('Metadatos');";
            var columnas = Convert.ToString(comando.ExecuteScalar()) ?? string.Empty;
            Assert.Contains("Datos", columnas, StringComparison.Ordinal);
            Assert.DoesNotContain("Valor", columnas, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(raiz, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void PermisosMantienenAdministradorYConjuntoComun()
    {
        using var entorno = EntornoServidor.Crear();
        var conjuntoInicial = entorno.Repositorio.ObtenerPermisos(
            @"PCERA\alero",
            incluirTodos: true).ConjuntoId;
        var guardados = entorno.Repositorio.GuardarPermisos(CrearPermisos());

        Assert.Equal(conjuntoInicial, guardados.ConjuntoId);
        Assert.Equal(2, guardados.Permisos["usuarios"]!.AsArray().Count);
        Assert.Throws<InvalidDataException>(() => entorno.Repositorio.GuardarPermisos(
            CrearPermisos(sinAdministrador: true)));
    }

    [Fact]
    public void CatalogoExigeConjuntoVigenteYRechazaTraversal()
    {
        using var entorno = EntornoServidor.Crear();
        var carpeta = Path.Combine(entorno.Rutas.Raiz, "Scripts");
        Directory.CreateDirectory(carpeta);
        File.WriteAllText(Path.Combine(carpeta, "prueba.ps1"), "Write-Output 'ok'");
        var conjunto = entorno.Repositorio.ObtenerPermisos(
            @"PCERA\alero",
            incluirTodos: true).ConjuntoId;
        var generador = new GeneradorCatalogoServidor();
        var catalogo = generador.Generar(carpeta, conjunto);

        var guardado = entorno.Repositorio.GuardarCatalogo(catalogo);
        Assert.Single(guardado.Catalogo["scripts"]!.AsArray());
        catalogo["conjuntoId"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Assert.Throws<InvalidDataException>(() => entorno.Repositorio.GuardarCatalogo(catalogo));
        catalogo["conjuntoId"] = conjunto;
        catalogo["scripts"]![0]!["scriptId"] = @"\\servidor\recurso\prueba.ps1";
        Assert.Throws<InvalidDataException>(() => entorno.Repositorio.GuardarCatalogo(catalogo));
        Assert.Throws<InvalidDataException>(() => generador.Generar(
            Path.Combine(carpeta, "..", "Scripts"),
            conjunto));
    }

    [Fact]
    public void AuditoriaSeConsultaPorUsuarioYSuMarcaTemporalEsDelServidor()
    {
        using var entorno = EntornoServidor.Crear();
        var procesador = new ProcesadorSolicitudesServidor(entorno.Repositorio);
        var solicitud = new SolicitudServidor(
            TransporteProtocolo.VersionActual,
            Guid.NewGuid(),
            OperacionesServidor.RegistrarAuditoria,
            TransporteProtocolo.CrearDatos(new EventoAuditoriaServidorCentral(
                Guid.NewGuid().ToString("N"),
                "ejecucion.inicio",
                "permitido",
                @"OTRO\falso",
                string.Empty,
                "EQUIPO-PRUEBA",
                "prueba.ps1",
                "prueba.ps1",
                new string('A', 64),
                Guid.NewGuid().ToString("N"),
                null,
                string.Empty,
                string.Empty,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch)));
        var antes = DateTimeOffset.UtcNow;
        var respuesta = procesador.Procesar(@"PCERA\alero", solicitud);
        var despues = DateTimeOffset.UtcNow;
        var pagina = entorno.Repositorio.ConsultarAuditoria(
            new FiltroAuditoriaServidorCentral(@"PCERA\alero", null, null, null, null));

        Assert.True(respuesta.Exito, respuesta.Mensaje);
        var evento = Assert.Single(pagina.Eventos);
        Assert.Equal(@"PCERA\alero", evento.UsuarioWindows);
        Assert.InRange(evento.FechaUtc, antes, despues);
    }

    [Fact]
    public void AuditoriaAceptaReintentoIdenticoYRechazaColision()
    {
        using var entorno = EntornoServidor.Crear();
        var evento = CrearEventoAuditoria();

        entorno.Repositorio.RegistrarAuditoria(evento);
        entorno.Repositorio.RegistrarAuditoria(evento with
        {
            FechaUtc = evento.FechaUtc.AddSeconds(5),
            FechaLocal = evento.FechaLocal.AddSeconds(5)
        });

        var pagina = entorno.Repositorio.ConsultarAuditoria(
            new FiltroAuditoriaServidorCentral(evento.UsuarioWindows, null, null, null, null));
        Assert.Single(pagina.Eventos);
        Assert.Throws<InvalidDataException>(() => entorno.Repositorio.RegistrarAuditoria(
            evento with { Detalle = "contenido diferente" }));
    }

    [Fact]
    public void AuditoriaRechazaCamposManipuladosOSobredimensionados()
    {
        using var entorno = EntornoServidor.Crear();
        var evento = CrearEventoAuditoria();

        Assert.Throws<InvalidDataException>(() => entorno.Repositorio.RegistrarAuditoria(
            evento with { Accion = "ejecucion\ninvalida" }));
        Assert.Throws<InvalidDataException>(() => entorno.Repositorio.RegistrarAuditoria(
            evento with { ScriptSha256 = "NO-ES-UN-HASH" }));
        Assert.Throws<InvalidDataException>(() => entorno.Repositorio.RegistrarAuditoria(
            evento with { EjecucionId = "identificador-invalido" }));
        Assert.Throws<InvalidDataException>(() => entorno.Repositorio.RegistrarAuditoria(
            evento with { Detalle = new string('x', 8001) }));
    }

    [Fact]
    public void AuditoriaRechazaFiltrosInvalidosOSobredimensionados()
    {
        using var entorno = EntornoServidor.Crear();

        Assert.Throws<InvalidDataException>(() => entorno.Repositorio.ConsultarAuditoria(
            new FiltroAuditoriaServidorCentral(@"PCERA\alero\otro", null, null, null, null)));
        Assert.Throws<InvalidDataException>(() => entorno.Repositorio.ConsultarAuditoria(
            new FiltroAuditoriaServidorCentral(null, null, null, null, new string('x', 513))));
        Assert.Throws<InvalidDataException>(() => entorno.Repositorio.ConsultarAuditoria(
            new FiltroAuditoriaServidorCentral(
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(-1),
                null,
                null)));
        Assert.Throws<InvalidDataException>(() => entorno.Repositorio.ConsultarAuditoria(
            new FiltroAuditoriaServidorCentral(null, null, null, null, null, 0, 0)));
    }

    [Fact]
    public void AuditoriaPaginaYCuentaConFiltrosCifrados()
    {
        using var entorno = EntornoServidor.Crear();
        var inicio = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (var indice = 0; indice < 12; indice++)
        {
            var fechaUtc = inicio.AddSeconds(indice);
            entorno.Repositorio.RegistrarAuditoria(CrearEventoAuditoria() with
            {
                Resultado = indice % 2 == 0 ? "correcto" : "error",
                ScriptId = indice % 3 == 0 ? "objetivo.ps1" : "otro.ps1",
                ScriptNombre = indice % 3 == 0 ? "Objetivo" : "Otro",
                FechaUtc = fechaUtc,
                FechaLocal = fechaUtc.ToLocalTime()
            });
        }

        var pagina = entorno.Repositorio.ConsultarAuditoria(
            new FiltroAuditoriaServidorCentral(
                @"PCERA\alero",
                inicio.AddSeconds(-1),
                inicio.AddMinutes(1),
                "correcto",
                "objetivo",
                1,
                1));

        Assert.Equal(2, pagina.Total);
        var evento = Assert.Single(pagina.Eventos);
        Assert.Equal("objetivo.ps1", evento.ScriptId);
        Assert.Equal(inicio, evento.FechaUtc);
    }

    [Fact]
    public void RetencionEliminaSoloAuditoriaAnteriorAlLimite()
    {
        using var entorno = EntornoServidor.Crear();
        var reciente = CrearEventoAuditoria();
        var antiguo = CrearEventoAuditoria() with
        {
            FechaUtc = DateTimeOffset.UtcNow.AddDays(-40),
            FechaLocal = DateTimeOffset.Now.AddDays(-40)
        };
        entorno.Repositorio.RegistrarAuditoria(antiguo);
        entorno.Repositorio.RegistrarAuditoria(reciente);

        var eliminadas = entorno.Repositorio.PurgarAuditoriaAnteriorA(
            DateTimeOffset.UtcNow.AddDays(-30));
        var pagina = entorno.Repositorio.ConsultarAuditoria(
            new FiltroAuditoriaServidorCentral(null, null, null, null, null));

        Assert.Equal(1, eliminadas);
        Assert.Equal(reciente.EventoId, Assert.Single(pagina.Eventos).EventoId);
    }

    [Fact]
    public async Task ProtocoloRechazaPropiedadesDuplicadas()
    {
        var json = "{\"version\":1,\"version\":1,\"solicitudId\":\"00000000-0000-0000-0000-000000000001\",\"operacion\":\"salud\",\"datos\":{}}";
        var contenido = Encoding.UTF8.GetBytes(json);
        var cabecera = new byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(cabecera, contenido.Length);
        await using var flujo = new MemoryStream();
        await flujo.WriteAsync(cabecera);
        await flujo.WriteAsync(contenido);
        flujo.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            TransporteProtocolo.LeerAsync<SolicitudServidor>(flujo, CancellationToken.None));
    }

    [Fact]
    public void ClaveInicialSeCreaUnaSolaVezConArranquesConcurrentes()
    {
        var raiz = Path.Combine(
            Path.GetTempPath(),
            "LanzadorScriptsServidorPruebas",
            Guid.NewGuid().ToString("N"));
        var rutas = new RutasServidor(raiz);
        var claves = new byte[16][];
        try
        {
            Parallel.For(0, claves.Length, indice =>
            {
                claves[indice] = new AlmacenClaveServidor(rutas, new ProtectorPruebas())
                    .ObtenerOCrear();
            });

            Assert.All(claves, clave => Assert.Equal(claves[0], clave));
        }
        finally
        {
            foreach (var clave in claves.Where(clave => clave is not null))
            {
                CryptographicOperations.ZeroMemory(clave);
            }

            Directory.Delete(raiz, recursive: true);
        }
    }

    [Fact]
    public void NoSePuedeEliminarElUltimoAdministrador()
    {
        using var entorno = EntornoServidor.Crear();
        var administradores = entorno.Repositorio.ListarUsuarios()
            .Where(usuario => usuario.Rol == "admin")
            .ToArray();

        Assert.Single(administradores);
        Assert.Throws<InvalidOperationException>(() =>
            entorno.Repositorio.EliminarUsuario(administradores[0].Id));
    }

    [Fact]
    public void CopiaIncluyeBaseClaveYConfiguracion()
    {
        using var entorno = EntornoServidor.Crear(conClaveProtegida: true);
        var copia = entorno.Repositorio.CrearCopiaSeguridad();
        var ruta = Path.Combine(entorno.Rutas.RutaCopias, copia.NombreArchivo);

        Assert.True(File.Exists(ruta));
        using var zip = System.IO.Compression.ZipFile.OpenRead(ruta);
        Assert.Contains(zip.Entries, entrada => entrada.FullName == "LanzadorScripts.db");
        Assert.Contains(zip.Entries, entrada => entrada.FullName == "base-datos.key.dpapi");
        Assert.Contains(zip.Entries, entrada => entrada.FullName == "configuracion-servidor.json");
    }

    private static JsonObject CrearPermisos(bool sinAdministrador = false)
    {
        return new JsonObject
        {
            ["scriptsAdmin"] = new JsonArray("prueba.ps1"),
            ["usuarios"] = new JsonArray(
                CrearUsuario("admin-1", @"PCERA\alero", sinAdministrador ? "nominal" : "admin"),
                CrearUsuario("usuario-1", @"MAD00\aroperez_micro", "nominal")),
            ["seguridadScripts"] = new JsonObject
            {
                ["scriptsElevadosPermitidos"] = new JsonArray(),
                ["permitirExecutionPolicyBypass"] = false
            },
            ["rolUsuarioActual"] = "nominal",
            ["maxScriptsSimultaneos"] = 5
        };
    }

    private static JsonObject CrearUsuario(string id, string cuenta, string rol)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["nombreUsuario"] = cuenta,
            ["rol"] = rol,
            ["maxScriptsSimultaneos"] = 5,
            ["carpetasPermitidas"] = new JsonArray()
        };
    }

    private static EventoAuditoriaServidorCentral CrearEventoAuditoria()
    {
        var ahora = DateTimeOffset.Now;
        return new EventoAuditoriaServidorCentral(
            Guid.NewGuid().ToString("N"),
            "ejecucion.fin",
            "correcto",
            @"PCERA\alero",
            "S-1-5-21-1",
            "EQUIPO-PRUEBA",
            "prueba.ps1",
            "prueba.ps1",
            new string('A', 64),
            Guid.NewGuid().ToString("N"),
            0,
            string.Empty,
            string.Empty,
            ahora.ToUniversalTime(),
            ahora);
    }

    private sealed class ProtectorPruebas : IProtectorClaveServidor
    {
        public byte[] Proteger(ReadOnlySpan<byte> datos)
        {
            return datos.ToArray();
        }

        public byte[] Desproteger(ReadOnlySpan<byte> datos)
        {
            return datos.ToArray();
        }
    }

    private sealed class ProtectorTransformacionPruebas : IProtectorClaveServidor
    {
        public byte[] Proteger(ReadOnlySpan<byte> datos)
        {
            return Transformar(datos);
        }

        public byte[] Desproteger(ReadOnlySpan<byte> datos)
        {
            return Transformar(datos);
        }

        private static byte[] Transformar(ReadOnlySpan<byte> datos)
        {
            var resultado = datos.ToArray();
            for (var indice = 0; indice < resultado.Length; indice++)
            {
                resultado[indice] ^= 0xA5;
            }

            return resultado;
        }
    }

    private static void EliminarDirectorioPruebas(string ruta)
    {
        // Retira los datos temporales creados por cada prueba.
        try
        {
            Directory.Delete(ruta, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class EntornoServidor : IDisposable
    {
        private EntornoServidor(RutasServidor rutas, RepositorioServidor repositorio)
        {
            Rutas = rutas;
            Repositorio = repositorio;
        }

        public RutasServidor Rutas { get; }

        public RepositorioServidor Repositorio { get; }

        public static EntornoServidor Crear(bool conClaveProtegida = false)
        {
            var raiz = Path.Combine(Path.GetTempPath(), "LanzadorScriptsServidorPruebas", Guid.NewGuid().ToString("N"));
            var rutas = new RutasServidor(raiz);
            var configuracion = new ConfiguracionServidor();
            configuracion.Validar();
            rutas.PrepararDirectorios();
            new AlmacenConfiguracionServidor(rutas).Guardar(configuracion);
            if (conClaveProtegida)
            {
                File.WriteAllBytes(rutas.RutaClaveProtegida, RandomNumberGenerator.GetBytes(64));
            }

            var repositorio = new RepositorioServidor(
                rutas,
                configuracion,
                RandomNumberGenerator.GetBytes(32));
            repositorio.Inicializar(@"PCERA\alero");
            return new EntornoServidor(rutas, repositorio);
        }

        public void Dispose()
        {
            Repositorio.Dispose();
            try
            {
                Directory.Delete(Rutas.Raiz, recursive: true);
            }
            catch
            {
            }
        }
    }
}
