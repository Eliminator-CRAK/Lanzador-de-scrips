// (Autor: Alex Roman)
// Descripcion: Gestiona las tablas cifradas de permisos, catalogo y auditoria.

using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LanzadorScripts.Protocolo;
using Microsoft.Data.Sqlite;

namespace LanzadorScripts.Servidor.Core;

public sealed class RepositorioServidor : IDisposable
{
    private const string TablaMetadatos = "Metadatos";
    private const string TablaUsuarios = "Usuarios";
    private const string TablaPermisos = "PermisosConfiguracion";
    private const string TablaCatalogo = "CatalogoScripts";
    private const string TablaCatalogoEstado = "CatalogoEstado";
    private const string TablaAuditoria = "Auditoria";
    private const string TablaIdentidades = "IdentidadesAuditoria";

    private enum OperacionSql
    {
        ContarUsuarios,
        ContarAuditorias,
        ObtenerUltimaAuditoria,
        ContarPermisos,
        EliminarUsuarios,
        EliminarPermisos,
        EliminarUsuario,
        EliminarCatalogo,
        InsertarCatalogo,
        EliminarEstadoCatalogo,
        InsertarIdentidadAuditoria,
        InsertarAuditoria,
        EliminarAuditoriaAnterior,
        EliminarIdentidadesHuerfanas,
        InsertarUsuario,
        InsertarPermisos,
        InsertarEstadoCatalogo,
        RenombrarMetadatosAnteriores,
        CrearMetadatosCifrados,
        EliminarMetadatosAnteriores,
        EliminarMetadato,
        InsertarMetadato
    }
    private readonly RutasServidor _rutas;
    private readonly ConfiguracionServidor _configuracion;
    private readonly CifradorDatosServidor _cifrador;
    private readonly SemaphoreSlim _escritura = new(1, 1);
    private readonly object _sincronizacionIntegridad = new();
    private readonly string _cadenaConexion;
    private ResultadoIntegridadServidorCentral _ultimaIntegridad = new(
        false,
        "La comprobacion completa de integridad esta pendiente.");
    private bool _desechado;

    public RepositorioServidor(
        RutasServidor rutas,
        ConfiguracionServidor configuracion,
        byte[] claveMaestra)
    {
        _rutas = rutas;
        _configuracion = configuracion;
        _cifrador = new CifradorDatosServidor(claveMaestra);
        CryptographicOperations.ZeroMemory(claveMaestra);
        _cadenaConexion = new SqliteConnectionStringBuilder
        {
            DataSource = rutas.RutaBaseDatos,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
    }

    public void Inicializar()
    {
        ComprobarNoDesechado();
        _rutas.PrepararDirectorios();
        RutasServidor.RechazarPuntoReanalisis(_rutas.RutaDatos);
        _escritura.Wait();
        try
        {
            using var conexion = AbrirConexion();
            using var comando = conexion.CreateCommand();
            comando.CommandText = """
                CREATE TABLE IF NOT EXISTS Metadatos (
                    Clave TEXT PRIMARY KEY NOT NULL,
                    Datos BLOB NOT NULL
                ) STRICT;
                CREATE TABLE IF NOT EXISTS Usuarios (
                    Id TEXT PRIMARY KEY NOT NULL,
                    IndiceNombre BLOB NOT NULL UNIQUE,
                    Datos BLOB NOT NULL
                ) STRICT;
                CREATE TABLE IF NOT EXISTS PermisosConfiguracion (
                    Id TEXT PRIMARY KEY NOT NULL,
                    Datos BLOB NOT NULL
                ) STRICT;
                CREATE TABLE IF NOT EXISTS CatalogoEstado (
                    Id TEXT PRIMARY KEY NOT NULL,
                    Datos BLOB NOT NULL
                ) STRICT;
                CREATE TABLE IF NOT EXISTS CatalogoScripts (
                    Id TEXT PRIMARY KEY NOT NULL,
                    IndiceScript BLOB NOT NULL UNIQUE,
                    Datos BLOB NOT NULL
                ) STRICT;
                CREATE TABLE IF NOT EXISTS IdentidadesAuditoria (
                    Id TEXT PRIMARY KEY NOT NULL,
                    IndiceUsuario BLOB NOT NULL UNIQUE,
                    Datos BLOB NOT NULL
                ) STRICT;
                CREATE TABLE IF NOT EXISTS Auditoria (
                    Id TEXT PRIMARY KEY NOT NULL,
                    IndiceUsuario BLOB NOT NULL,
                    FechaUtcTicks INTEGER NOT NULL,
                    Datos BLOB NOT NULL
                ) STRICT;
                CREATE INDEX IF NOT EXISTS IX_Auditoria_Usuario_Fecha
                    ON Auditoria (IndiceUsuario, FechaUtcTicks DESC);
                CREATE INDEX IF NOT EXISTS IX_Auditoria_Fecha
                    ON Auditoria (FechaUtcTicks DESC);
                """;
            comando.ExecuteNonQuery();
            using var transaccion = conexion.BeginTransaction();
            MigrarMetadatosSiEsNecesario(conexion, transaccion);
            AsegurarMetadato(conexion, transaccion, "version_esquema", "2");
            AsegurarMetadato(conexion, transaccion, "conjunto_id", CrearConjuntoId());
            AsegurarMetadato(conexion, transaccion, "revision_permisos", "1");
            AsegurarMetadato(conexion, transaccion, "revision_catalogo", "1");
            AsegurarPermisosIniciales(conexion, transaccion);
            if (!string.Equals(
                    LeerMetadato(conexion, "version_esquema", transaccion),
                    "2",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("La base usa un esquema de metadatos no compatible.");
            }

            transaccion.Commit();
            using var checkpoint = conexion.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
        finally
        {
            _escritura.Release();
        }
    }

    public EstadoServidorCentral ObtenerEstado()
    {
        ComprobarNoDesechado();
        try
        {
            using var conexion = AbrirConexion();
            var totalUsuarios = EjecutarEscalarLong(conexion, OperacionSql.ContarUsuarios);
            var totalAuditorias = EjecutarEscalarLong(conexion, OperacionSql.ContarAuditorias);
            var ultimoTicks = EjecutarEscalarLongNullable(
                conexion,
                OperacionSql.ObtenerUltimaAuditoria);
            ResultadoIntegridadServidorCentral integridad;
            lock (_sincronizacionIntegridad)
            {
                integridad = _ultimaIntegridad;
            }

            return new EstadoServidorCentral(
                "1.8.0",
                Environment.MachineName,
                true,
                integridad.Integra,
                _configuracion.Puerto,
                checked((int)totalUsuarios),
                totalAuditorias,
                ultimoTicks.HasValue
                    ? new DateTimeOffset(ultimoTicks.Value, TimeSpan.Zero)
                    : null,
                integridad.Mensaje);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or CryptographicException)
        {
            return new EstadoServidorCentral(
                "1.8.0",
                Environment.MachineName,
                false,
                false,
                _configuracion.Puerto,
                0,
                0,
                null,
                $"La base de datos no esta disponible: {ex.GetType().Name}.");
        }
    }

    public bool EsAdministrador(string cuenta)
    {
        var usuario = BuscarUsuarioPorNombre(cuenta);
        return usuario is { Activo: true, Rol: "admin" };
    }

    public bool EstaAutorizado(string cuenta)
    {
        return BuscarUsuarioPorNombre(cuenta) is { Activo: true };
    }

    public PermisosServidorCentral ObtenerPermisos(string cuenta, bool incluirTodos)
    {
        ComprobarNoDesechado();
        using var conexion = AbrirConexion();
        var configuracion = LeerFila<ConfiguracionPermisosDatos>(
            conexion,
            TablaPermisos,
            "actual");
        var usuarios = ListarUsuariosInterno(conexion);
        if (!incluirTodos)
        {
            usuarios = usuarios
                .Where(usuario => string.Equals(
                    usuario.NombreUsuario,
                    cuenta,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var jsonUsuarios = new JsonArray(usuarios.Select(usuario => new JsonObject
        {
            ["id"] = usuario.Id,
            ["nombreUsuario"] = usuario.NombreUsuario,
            ["rol"] = usuario.Rol,
            ["maxScriptsSimultaneos"] = usuario.MaxScriptsSimultaneos,
            ["carpetasPermitidas"] = new JsonArray(
                usuario.CarpetasPermitidas
                    .Select(valor => (JsonNode?)JsonValue.Create(valor))
                    .ToArray())
        }).ToArray());
        var permisos = new JsonObject
        {
            ["scriptsAdmin"] = new JsonArray(
                configuracion.ScriptsAdmin
                    .Select(valor => (JsonNode?)JsonValue.Create(valor))
                    .ToArray()),
            ["usuarios"] = jsonUsuarios,
            ["seguridadScripts"] = new JsonObject
            {
                ["scriptsElevadosPermitidos"] = new JsonArray(
                    configuracion.ScriptsElevados
                        .Select(valor => (JsonNode?)JsonValue.Create(valor))
                        .ToArray()),
                ["permitirExecutionPolicyBypass"] = configuracion.PermitirExecutionPolicyBypass
            },
            ["rolUsuarioActual"] = configuracion.RolUsuarioActual,
            ["maxScriptsSimultaneos"] = configuracion.MaxScriptsSimultaneos
        };
        return new PermisosServidorCentral(
            LeerMetadato(conexion, "conjunto_id"),
            long.Parse(LeerMetadato(conexion, "revision_permisos")),
            permisos);
    }

    public PermisosServidorCentral GuardarPermisos(JsonObject permisos)
    {
        var datos = ValidadorDatosServidor.ValidarPermisos(permisos);
        _escritura.Wait();
        try
        {
            using var conexion = AbrirConexion();
            using var transaccion = conexion.BeginTransaction();
            Ejecutar(conexion, transaccion, OperacionSql.EliminarUsuarios);
            foreach (var usuario in datos.Usuarios)
            {
                InsertarUsuario(conexion, transaccion, usuario);
            }

            Ejecutar(conexion, transaccion, OperacionSql.EliminarPermisos);
            EscribirFila(
                conexion,
                transaccion,
                TablaPermisos,
                "actual",
                new ConfiguracionPermisosDatos(
                    datos.ScriptsAdmin,
                    datos.ScriptsElevados,
                    datos.PermitirExecutionPolicyBypass,
                    datos.RolUsuarioActual,
                    datos.MaxScriptsSimultaneos));
            IncrementarRevision(conexion, transaccion, "revision_permisos");
            transaccion.Commit();
        }
        finally
        {
            _escritura.Release();
        }

        return ObtenerPermisos(string.Empty, incluirTodos: true);
    }

    public IReadOnlyList<UsuarioServidorCentral> ListarUsuarios()
    {
        using var conexion = AbrirConexion();
        return ListarUsuariosInterno(conexion);
    }

    public UsuarioServidorCentral GuardarUsuario(GuardarUsuarioServidorCentral solicitud)
    {
        var usuario = ValidadorDatosServidor.ValidarUsuario(solicitud);
        _escritura.Wait();
        try
        {
            using var conexion = AbrirConexion();
            using var transaccion = conexion.BeginTransaction();
            var existenteNombre = BuscarUsuarioPorIndice(
                conexion,
                _cifrador.CrearIndice("usuario", usuario.NombreUsuario));
            if (existenteNombre is not null
                && !string.Equals(existenteNombre.Id, usuario.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Ya existe un usuario con esa cuenta de Windows.");
            }

            Ejecutar(
                conexion,
                transaccion,
                OperacionSql.EliminarUsuario,
                ("$id", usuario.Id));
            InsertarUsuario(conexion, transaccion, usuario);
            ValidarExisteAdministrador(conexion, transaccion);
            IncrementarRevision(conexion, transaccion, "revision_permisos");
            transaccion.Commit();
            return usuario;
        }
        finally
        {
            _escritura.Release();
        }
    }

    public void EliminarUsuario(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100)
        {
            throw new InvalidDataException("El identificador del usuario no es valido.");
        }

        _escritura.Wait();
        try
        {
            using var conexion = AbrirConexion();
            using var transaccion = conexion.BeginTransaction();
            Ejecutar(
                conexion,
                transaccion,
                OperacionSql.EliminarUsuario,
                ("$id", id));
            ValidarExisteAdministrador(conexion, transaccion);
            IncrementarRevision(conexion, transaccion, "revision_permisos");
            transaccion.Commit();
        }
        finally
        {
            _escritura.Release();
        }
    }

    public CatalogoServidorCentral ObtenerCatalogo()
    {
        using var conexion = AbrirConexion();
        var estado = LeerFila<CatalogoEstadoDatos>(conexion, TablaCatalogoEstado, "actual");
        var entradas = new List<EntradaCatalogoServidor>();
        using (var comando = conexion.CreateCommand())
        {
            comando.CommandText = "SELECT Id, Datos FROM CatalogoScripts ORDER BY Id;";
            using var lector = comando.ExecuteReader();
            while (lector.Read())
            {
                entradas.Add(_cifrador.Descifrar<EntradaCatalogoServidor>(
                    TablaCatalogo,
                    lector.GetString(0),
                    (byte[])lector[1]));
            }
        }

        var conjuntoId = LeerMetadato(conexion, "conjunto_id");
        var catalogo = new JsonObject
        {
            ["version"] = 1,
            ["generadoUtc"] = estado.GeneradoUtc,
            ["conjuntoId"] = conjuntoId,
            ["scripts"] = new JsonArray(entradas
                .OrderBy(entrada => entrada.ScriptId, StringComparer.OrdinalIgnoreCase)
                .Select(entrada => new JsonObject
                {
                    ["scriptId"] = entrada.ScriptId,
                    ["extension"] = entrada.Extension,
                    ["longitud"] = entrada.Longitud,
                    ["sha256"] = entrada.Sha256
                }).ToArray())
        };
        return new CatalogoServidorCentral(
            conjuntoId,
            long.Parse(LeerMetadato(conexion, "revision_catalogo")),
            catalogo);
    }

    public CatalogoServidorCentral GuardarCatalogo(JsonObject catalogo)
    {
        using (var conexion = AbrirConexion())
        {
            var conjuntoEsperado = LeerMetadato(conexion, "conjunto_id");
            var conjuntoRecibido = catalogo["conjuntoId"]?.GetValue<string>() ?? string.Empty;
            if (!string.Equals(conjuntoRecibido, conjuntoEsperado, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "El catalogo no pertenece al conjunto vigente de permisos.");
            }
        }

        var datos = ValidadorDatosServidor.ValidarCatalogo(catalogo);
        _escritura.Wait();
        try
        {
            using var conexion = AbrirConexion();
            using var transaccion = conexion.BeginTransaction();
            Ejecutar(conexion, transaccion, OperacionSql.EliminarCatalogo);
            foreach (var entrada in datos.Scripts)
            {
                var id = Convert.ToHexString(_cifrador.CrearIndice("catalogo-id", entrada.ScriptId));
                var cifrado = _cifrador.Cifrar(TablaCatalogo, id, entrada);
                Ejecutar(
                    conexion,
                    transaccion,
                    OperacionSql.InsertarCatalogo,
                    ("$id", id),
                    ("$indice", _cifrador.CrearIndice("catalogo", entrada.ScriptId)),
                    ("$datos", cifrado));
            }

            Ejecutar(conexion, transaccion, OperacionSql.EliminarEstadoCatalogo);
            EscribirFila(
                conexion,
                transaccion,
                TablaCatalogoEstado,
                "actual",
                new CatalogoEstadoDatos(datos.GeneradoUtc));
            IncrementarRevision(conexion, transaccion, "revision_catalogo");
            transaccion.Commit();
        }
        finally
        {
            _escritura.Release();
        }

        return ObtenerCatalogo();
    }

    public void RegistrarAuditoria(EventoAuditoriaServidorCentral evento)
    {
        ValidarEventoAuditoria(evento);
        _escritura.Wait();
        try
        {
            using var conexion = AbrirConexion();
            using var transaccion = conexion.BeginTransaction();
            var existente = BuscarEventoAuditoria(conexion, transaccion, evento.EventoId);
            if (existente is not null)
            {
                if (!EsMismoEventoReintentado(existente, evento))
                {
                    throw new InvalidDataException(
                        "El identificador de auditoria ya pertenece a otro evento.");
                }

                transaccion.Commit();
                return;
            }

            var indiceUsuario = _cifrador.CrearIndice("auditoria-usuario", evento.UsuarioWindows);
            var idIdentidad = Convert.ToHexString(indiceUsuario);
            var identidad = _cifrador.Cifrar(
                TablaIdentidades,
                idIdentidad,
                new IdentidadAuditoriaDatos(evento.UsuarioWindows));
            Ejecutar(
                conexion,
                transaccion,
                OperacionSql.InsertarIdentidadAuditoria,
                ("$id", idIdentidad),
                ("$indice", indiceUsuario),
                ("$datos", identidad));
            var cifrado = _cifrador.Cifrar(TablaAuditoria, evento.EventoId, evento);
            Ejecutar(
                conexion,
                transaccion,
                OperacionSql.InsertarAuditoria,
                ("$id", evento.EventoId),
                ("$indice", indiceUsuario),
                ("$fecha", evento.FechaUtc.UtcTicks),
                ("$datos", cifrado));
            transaccion.Commit();
        }
        finally
        {
            _escritura.Release();
        }
    }

    public PaginaAuditoriaServidorCentral ConsultarAuditoria(FiltroAuditoriaServidorCentral filtro)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var (usuarioFiltro, resultadoFiltro, scriptFiltro) = ValidarFiltroAuditoria(filtro);
        var limite = filtro.Limite;
        var desplazamiento = filtro.Desplazamiento;
        using var conexion = AbrirConexion();

        if (resultadoFiltro is null && scriptFiltro is null)
        {
            using var comandoTotal = conexion.CreateCommand();
            comandoTotal.CommandText = """
                SELECT COUNT(*)
                FROM Auditoria
                WHERE ($filtrarUsuario = 0 OR IndiceUsuario = $usuario)
                  AND ($filtrarDesde = 0 OR FechaUtcTicks >= $desde)
                  AND ($filtrarHasta = 0 OR FechaUtcTicks <= $hasta);
                """;
            ConfigurarParametrosFiltroAuditoria(comandoTotal, usuarioFiltro, filtro);
            var total = Convert.ToInt64(comandoTotal.ExecuteScalar(), CultureInfo.InvariantCulture);

            using var comandoPagina = conexion.CreateCommand();
            comandoPagina.CommandText = """
                SELECT Id, Datos
                FROM Auditoria
                WHERE ($filtrarUsuario = 0 OR IndiceUsuario = $usuario)
                  AND ($filtrarDesde = 0 OR FechaUtcTicks >= $desde)
                  AND ($filtrarHasta = 0 OR FechaUtcTicks <= $hasta)
                ORDER BY FechaUtcTicks DESC, Id DESC
                LIMIT $limite OFFSET $desplazamiento;
                """;
            ConfigurarParametrosFiltroAuditoria(comandoPagina, usuarioFiltro, filtro);
            comandoPagina.Parameters.AddWithValue("$limite", limite);
            comandoPagina.Parameters.AddWithValue("$desplazamiento", desplazamiento);
            var pagina = LeerEventosAuditoria(comandoPagina, limite);
            return new PaginaAuditoriaServidorCentral(
                ListarIdentidadesAuditoria(conexion),
                pagina,
                total);
        }

        using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT Id, Datos
            FROM Auditoria
            WHERE ($filtrarUsuario = 0 OR IndiceUsuario = $usuario)
              AND ($filtrarDesde = 0 OR FechaUtcTicks >= $desde)
              AND ($filtrarHasta = 0 OR FechaUtcTicks <= $hasta)
            ORDER BY FechaUtcTicks DESC, Id DESC;
            """;
        ConfigurarParametrosFiltroAuditoria(comando, usuarioFiltro, filtro);
        var encontrados = new List<EventoAuditoriaServidorCentral>();
        long totalCoincidencias = 0;
        using (var lector = comando.ExecuteReader())
        {
            while (lector.Read())
            {
                var evento = _cifrador.Descifrar<EventoAuditoriaServidorCentral>(
                    TablaAuditoria,
                    lector.GetString(0),
                    (byte[])lector[1]);
                if (resultadoFiltro is not null
                    && !string.Equals(evento.Resultado, resultadoFiltro, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (scriptFiltro is not null
                    && !(evento.ScriptId?.Contains(scriptFiltro, StringComparison.OrdinalIgnoreCase) ?? false)
                    && !(evento.ScriptNombre?.Contains(scriptFiltro, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    continue;
                }

                if (totalCoincidencias >= desplazamiento && encontrados.Count < limite)
                {
                    encontrados.Add(evento);
                }

                totalCoincidencias++;
            }
        }

        var usuarios = ListarIdentidadesAuditoria(conexion);
        return new PaginaAuditoriaServidorCentral(
            usuarios,
            encontrados,
            totalCoincidencias);
    }

    private void ConfigurarParametrosFiltroAuditoria(
        SqliteCommand comando,
        string? usuarioFiltro,
        FiltroAuditoriaServidorCentral filtro)
    {
        var filtrarUsuario = usuarioFiltro is not null;
        comando.Parameters.AddWithValue("$filtrarUsuario", filtrarUsuario ? 1 : 0);
        comando.Parameters.AddWithValue(
            "$usuario",
            filtrarUsuario
                ? _cifrador.CrearIndice("auditoria-usuario", usuarioFiltro!)
                : Array.Empty<byte>());
        comando.Parameters.AddWithValue("$filtrarDesde", filtro.DesdeUtc.HasValue ? 1 : 0);
        comando.Parameters.AddWithValue("$desde", filtro.DesdeUtc?.UtcTicks ?? 0L);
        comando.Parameters.AddWithValue("$filtrarHasta", filtro.HastaUtc.HasValue ? 1 : 0);
        comando.Parameters.AddWithValue("$hasta", filtro.HastaUtc?.UtcTicks ?? 0L);
    }

    private List<EventoAuditoriaServidorCentral> LeerEventosAuditoria(
        SqliteCommand comando,
        int capacidad)
    {
        var eventos = new List<EventoAuditoriaServidorCentral>(capacidad);
        using var lector = comando.ExecuteReader();
        while (lector.Read())
        {
            eventos.Add(_cifrador.Descifrar<EventoAuditoriaServidorCentral>(
                TablaAuditoria,
                lector.GetString(0),
                (byte[])lector[1]));
        }

        return eventos;
    }

    public ResultadoIntegridadServidorCentral ComprobarIntegridad()
    {
        ResultadoIntegridadServidorCentral integridad;
        try
        {
            using var conexion = AbrirConexion();
            using var comando = conexion.CreateCommand();
            comando.CommandText = "PRAGMA integrity_check;";
            var resultado = Convert.ToString(comando.ExecuteScalar()) ?? string.Empty;
            if (!string.Equals(resultado, "ok", StringComparison.OrdinalIgnoreCase))
            {
                integridad = new ResultadoIntegridadServidorCentral(
                    false,
                    "SQLite detecto una incidencia de integridad.");
            }
            else
            {
                ComprobarIntegridadCriptografica(conexion);
                integridad = new ResultadoIntegridadServidorCentral(
                    true,
                    "La estructura y los datos cifrados son integros.");
            }
        }
        catch (Exception ex) when (ex is SqliteException
            or IOException
            or CryptographicException
            or InvalidDataException
            or JsonException)
        {
            integridad = new ResultadoIntegridadServidorCentral(
                false,
                $"No se pudo comprobar la integridad: {ex.GetType().Name}.");
        }

        lock (_sincronizacionIntegridad)
        {
            _ultimaIntegridad = integridad;
        }

        return integridad;
    }

    public long PurgarAuditoriaAnteriorA(DateTimeOffset limiteUtc)
    {
        if (limiteUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("La fecha limite debe estar expresada en UTC.", nameof(limiteUtc));
        }

        _escritura.Wait();
        try
        {
            using var conexion = AbrirConexion();
            using var transaccion = conexion.BeginTransaction();
            var eliminadas = Ejecutar(
                conexion,
                transaccion,
                OperacionSql.EliminarAuditoriaAnterior,
                ("$limite", limiteUtc.UtcTicks));
            Ejecutar(
                conexion,
                transaccion,
                OperacionSql.EliminarIdentidadesHuerfanas);
            transaccion.Commit();
            return eliminadas;
        }
        finally
        {
            _escritura.Release();
        }
    }

    public ResultadoCopiaServidorCentral CrearCopiaSeguridad()
    {
        _escritura.Wait();
        try
        {
            _rutas.PrepararDirectorios();
            var fecha = DateTimeOffset.UtcNow;
            var baseNombre = $"LanzadorScriptsServidor_{fecha:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
            var temporalDb = Path.Combine(_rutas.RutaCopias, baseNombre + ".db.tmp");
            var destinoZip = Path.Combine(_rutas.RutaCopias, baseNombre + ".zip");
            try
            {
                using (var origen = AbrirConexion())
                using (var destino = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = temporalDb,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                }.ToString()))
                {
                    destino.Open();
                    origen.BackupDatabase(destino);
                }

                using (var zip = ZipFile.Open(destinoZip, ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(temporalDb, "LanzadorScripts.db", CompressionLevel.Optimal);
                    zip.CreateEntryFromFile(
                        _rutas.RutaClaveProtegida,
                        "base-datos.key.dpapi",
                        CompressionLevel.Optimal);
                    zip.CreateEntryFromFile(
                        _rutas.RutaConfiguracion,
                        "configuracion-servidor.json",
                        CompressionLevel.Optimal);
                }

                var informacion = new FileInfo(destinoZip);
                return new ResultadoCopiaServidorCentral(informacion.Name, fecha, informacion.Length);
            }
            finally
            {
                if (File.Exists(temporalDb))
                {
                    File.Delete(temporalDb);
                }
            }
        }
        finally
        {
            _escritura.Release();
        }
    }

    public void Dispose()
    {
        if (_desechado)
        {
            return;
        }

        _desechado = true;
        SqliteConnection.ClearAllPools();
        _cifrador.Dispose();
        _escritura.Dispose();
    }

    private SqliteConnection AbrirConexion()
    {
        ComprobarNoDesechado();
        var conexion = new SqliteConnection(_cadenaConexion);
        conexion.Open();
        using var comando = conexion.CreateCommand();
        comando.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = 5000;
            PRAGMA secure_delete = ON;
            PRAGMA trusted_schema = OFF;
            """;
        comando.ExecuteNonQuery();
        return conexion;
    }

    private void AsegurarPermisosIniciales(SqliteConnection conexion, SqliteTransaction transaccion)
    {
        var total = EjecutarEscalarLong(
            conexion,
            OperacionSql.ContarPermisos,
            transaccion);
        if (total > 0)
        {
            return;
        }

        EscribirFila(
            conexion,
            transaccion,
            TablaPermisos,
            "actual",
            new ConfiguracionPermisosDatos([], [], false, "nominal", 5));
        foreach (var cuenta in _configuracion.AdministradoresIniciales)
        {
            InsertarUsuario(
                conexion,
                transaccion,
                new UsuarioServidorCentral(
                    Guid.NewGuid().ToString("N"),
                    cuenta,
                    "admin",
                    5,
                    [],
                    true));
        }

        EscribirFila(
            conexion,
            transaccion,
            TablaCatalogoEstado,
            "actual",
            new CatalogoEstadoDatos(DateTimeOffset.UtcNow));
    }

    private void InsertarUsuario(
        SqliteConnection conexion,
        SqliteTransaction transaccion,
        UsuarioServidorCentral usuario)
    {
        var cifrado = _cifrador.Cifrar(TablaUsuarios, usuario.Id, usuario);
        Ejecutar(
            conexion,
            transaccion,
            OperacionSql.InsertarUsuario,
            ("$id", usuario.Id),
            ("$indice", _cifrador.CrearIndice("usuario", usuario.NombreUsuario)),
            ("$datos", cifrado));
    }

    private UsuarioServidorCentral? BuscarUsuarioPorNombre(string cuenta)
    {
        var normalizada = ConfiguracionServidor.NormalizarCuenta(cuenta);
        if (normalizada.Length == 0)
        {
            return null;
        }

        using var conexion = AbrirConexion();
        return BuscarUsuarioPorIndice(
            conexion,
            _cifrador.CrearIndice("usuario", normalizada));
    }

    private UsuarioServidorCentral? BuscarUsuarioPorIndice(
        SqliteConnection conexion,
        byte[] indice)
    {
        using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT Id, Datos FROM Usuarios WHERE IndiceNombre = $indice LIMIT 1;";
        comando.Parameters.AddWithValue("$indice", indice);
        using var lector = comando.ExecuteReader();
        return lector.Read()
            ? _cifrador.Descifrar<UsuarioServidorCentral>(
                TablaUsuarios,
                lector.GetString(0),
                (byte[])lector[1])
            : null;
    }

    private List<UsuarioServidorCentral> ListarUsuariosInterno(SqliteConnection conexion)
    {
        var usuarios = new List<UsuarioServidorCentral>();
        using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT Id, Datos FROM Usuarios;";
        using var lector = comando.ExecuteReader();
        while (lector.Read())
        {
            usuarios.Add(_cifrador.Descifrar<UsuarioServidorCentral>(
                TablaUsuarios,
                lector.GetString(0),
                (byte[])lector[1]));
        }

        return usuarios
            .OrderBy(usuario => usuario.NombreUsuario, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<string> ListarIdentidadesAuditoria(SqliteConnection conexion)
    {
        var usuarios = new List<string>();
        using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT Id, Datos FROM IdentidadesAuditoria;";
        using var lector = comando.ExecuteReader();
        while (lector.Read())
        {
            var identidad = _cifrador.Descifrar<IdentidadAuditoriaDatos>(
                TablaIdentidades,
                lector.GetString(0),
                (byte[])lector[1]);
            usuarios.Add(identidad.NombreUsuario);
        }

        return usuarios
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(usuario => usuario, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private T LeerFila<T>(SqliteConnection conexion, string tabla, string id)
    {
        using var comando = conexion.CreateCommand();
        comando.CommandText = tabla switch
        {
            TablaPermisos => "SELECT Datos FROM PermisosConfiguracion WHERE Id = $id LIMIT 1;",
            TablaCatalogoEstado => "SELECT Datos FROM CatalogoEstado WHERE Id = $id LIMIT 1;",
            _ => throw new InvalidOperationException("La tabla cifrada solicitada no esta autorizada.")
        };
        comando.Parameters.AddWithValue("$id", id);
        var datos = comando.ExecuteScalar() as byte[]
            ?? throw new InvalidDataException($"No existe la fila requerida en {tabla}.");
        return _cifrador.Descifrar<T>(tabla, id, datos);
    }

    private void EscribirFila<T>(
        SqliteConnection conexion,
        SqliteTransaction transaccion,
        string tabla,
        string id,
        T datos)
    {
        var cifrado = _cifrador.Cifrar(tabla, id, datos);
        var operacion = tabla switch
        {
            TablaPermisos => OperacionSql.InsertarPermisos,
            TablaCatalogoEstado => OperacionSql.InsertarEstadoCatalogo,
            _ => throw new InvalidOperationException("La tabla cifrada solicitada no esta autorizada.")
        };
        Ejecutar(
            conexion,
            transaccion,
            operacion,
            ("$id", id),
            ("$datos", cifrado));
    }

    private static void ValidarEventoAuditoria(EventoAuditoriaServidorCentral evento)
    {
        ArgumentNullException.ThrowIfNull(evento);
        if (!Guid.TryParseExact(evento.EventoId, "N", out _)
            || ConfiguracionServidor.NormalizarCuenta(evento.UsuarioWindows).Length == 0
            || !EsTextoAuditoriaValido(evento.Accion, 200, obligatorio: true)
            || !EsTextoAuditoriaValido(evento.Resultado, 100, obligatorio: true)
            || !EsTextoAuditoriaValido(evento.UsuarioSid, 256)
            || !EsTextoAuditoriaValido(evento.Equipo, 256, obligatorio: true)
            || !EsTextoAuditoriaValido(evento.ScriptId, 1024)
            || !EsTextoAuditoriaValido(evento.ScriptNombre, 512)
            || evento.ScriptSha256 is not null
                && !EsHexadecimal(evento.ScriptSha256, 64)
            || evento.EjecucionId is not null
                && !Guid.TryParseExact(evento.EjecucionId, "N", out _)
            || !EsTextoAuditoriaValido(evento.Motivo, 2000, permitirSaltos: true)
            || !EsTextoAuditoriaValido(evento.Detalle, 8000, permitirSaltos: true)
            || evento.FechaUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("El evento de auditoria no tiene datos validos.");
        }
    }

    private static (string? Usuario, string? Resultado, string? Script) ValidarFiltroAuditoria(
        FiltroAuditoriaServidorCentral filtro)
    {
        // Limita los filtros antes de consultar y descifrar eventos.
        if (filtro.Limite is < 1 or > 2000
            || filtro.Desplazamiento is < 0 or > 1_000_000
            || filtro.DesdeUtc.HasValue
                && filtro.HastaUtc.HasValue
                && filtro.DesdeUtc.Value > filtro.HastaUtc.Value
            || !EsTextoAuditoriaValido(filtro.Resultado, 100)
            || !EsTextoAuditoriaValido(filtro.Script, 512))
        {
            throw new InvalidDataException("Los filtros de auditoria no son validos.");
        }

        string? usuario = null;
        if (!string.IsNullOrWhiteSpace(filtro.Usuario))
        {
            usuario = ConfiguracionServidor.NormalizarCuenta(filtro.Usuario);
            if (usuario.Length == 0)
            {
                throw new InvalidDataException("La cuenta del filtro de auditoria no es valida.");
            }
        }

        var resultado = string.IsNullOrWhiteSpace(filtro.Resultado)
            ? null
            : filtro.Resultado.Trim();
        var script = string.IsNullOrWhiteSpace(filtro.Script)
            ? null
            : filtro.Script.Trim();
        return (usuario, resultado, script);
    }

    private static bool EsTextoAuditoriaValido(
        string? valor,
        int longitudMaxima,
        bool obligatorio = false,
        bool permitirSaltos = false)
    {
        if (valor is null)
        {
            return !obligatorio;
        }

        if (valor.Length > longitudMaxima
            || obligatorio && string.IsNullOrWhiteSpace(valor))
        {
            return false;
        }

        return valor.All(caracter => !char.IsControl(caracter)
            || permitirSaltos && caracter is '\r' or '\n' or '\t');
    }

    private EventoAuditoriaServidorCentral? BuscarEventoAuditoria(
        SqliteConnection conexion,
        SqliteTransaction transaccion,
        string eventoId)
    {
        using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = "SELECT Datos FROM Auditoria WHERE Id = $id LIMIT 1;";
        comando.Parameters.AddWithValue("$id", eventoId);
        return comando.ExecuteScalar() is byte[] datos
            ? _cifrador.Descifrar<EventoAuditoriaServidorCentral>(TablaAuditoria, eventoId, datos)
            : null;
    }

    private static bool EsMismoEventoReintentado(
        EventoAuditoriaServidorCentral existente,
        EventoAuditoriaServidorCentral recibido)
    {
        return string.Equals(existente.EventoId, recibido.EventoId, StringComparison.Ordinal)
            && string.Equals(existente.Accion, recibido.Accion, StringComparison.Ordinal)
            && string.Equals(existente.Resultado, recibido.Resultado, StringComparison.Ordinal)
            && string.Equals(existente.UsuarioWindows, recibido.UsuarioWindows, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existente.UsuarioSid, recibido.UsuarioSid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existente.Equipo, recibido.Equipo, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existente.ScriptId, recibido.ScriptId, StringComparison.Ordinal)
            && string.Equals(existente.ScriptNombre, recibido.ScriptNombre, StringComparison.Ordinal)
            && string.Equals(existente.ScriptSha256, recibido.ScriptSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existente.EjecucionId, recibido.EjecucionId, StringComparison.OrdinalIgnoreCase)
            && existente.CodigoSalida == recibido.CodigoSalida
            && string.Equals(existente.Motivo, recibido.Motivo, StringComparison.Ordinal)
            && string.Equals(existente.Detalle, recibido.Detalle, StringComparison.Ordinal);
    }

    private void ValidarExisteAdministrador(
        SqliteConnection conexion,
        SqliteTransaction transaccion)
    {
        using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = "SELECT Id, Datos FROM Usuarios;";
        using var lector = comando.ExecuteReader();
        while (lector.Read())
        {
            var usuario = _cifrador.Descifrar<UsuarioServidorCentral>(
                TablaUsuarios,
                lector.GetString(0),
                (byte[])lector[1]);
            if (usuario.Activo && usuario.Rol == "admin")
            {
                return;
            }
        }

        throw new InvalidOperationException("No se puede eliminar el ultimo administrador activo.");
    }

    private void MigrarMetadatosSiEsNecesario(
        SqliteConnection conexion,
        SqliteTransaction transaccion)
    {
        var columnas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var comando = conexion.CreateCommand())
        {
            comando.Transaction = transaccion;
            comando.CommandText = "PRAGMA table_info(Metadatos);";
            using var lector = comando.ExecuteReader();
            while (lector.Read())
            {
                columnas.Add(lector.GetString(1));
            }
        }

        if (columnas.Contains("Datos") && !columnas.Contains("Valor"))
        {
            return;
        }

        if (!columnas.Contains("Valor") || columnas.Contains("Datos"))
        {
            throw new InvalidDataException("La tabla de metadatos no tiene un formato compatible.");
        }

        var anteriores = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var comando = conexion.CreateCommand())
        {
            comando.Transaction = transaccion;
            comando.CommandText = "SELECT Clave, Valor FROM Metadatos;";
            using var lector = comando.ExecuteReader();
            while (lector.Read())
            {
                var clave = lector.GetString(0);
                var valor = lector.GetString(1);
                if (!anteriores.TryAdd(clave, valor) || clave.Length > 100 || valor.Length > 1024)
                {
                    throw new InvalidDataException("Los metadatos anteriores no son validos.");
                }
            }
        }

        var permitidas = new HashSet<string>(StringComparer.Ordinal)
        {
            "version_esquema",
            "conjunto_id",
            "revision_permisos",
            "revision_catalogo"
        };
        if (anteriores.Keys.Any(clave => !permitidas.Contains(clave))
            || anteriores.TryGetValue("version_esquema", out var versionAnterior)
                && !string.Equals(versionAnterior, "1", StringComparison.Ordinal)
            || anteriores.TryGetValue("conjunto_id", out var conjuntoAnterior)
                && !EsHexadecimal(conjuntoAnterior, 32)
            || anteriores.TryGetValue("revision_permisos", out var revisionPermisos)
                && !EsRevisionValida(revisionPermisos)
            || anteriores.TryGetValue("revision_catalogo", out var revisionCatalogo)
                && !EsRevisionValida(revisionCatalogo))
        {
            throw new InvalidDataException("Los metadatos anteriores no superan la validacion.");
        }

        Ejecutar(conexion, transaccion, OperacionSql.RenombrarMetadatosAnteriores);
        Ejecutar(
            conexion,
            transaccion,
            OperacionSql.CrearMetadatosCifrados);
        foreach (var entrada in anteriores)
        {
            var valor = string.Equals(entrada.Key, "version_esquema", StringComparison.Ordinal)
                ? "2"
                : entrada.Value;
            EscribirMetadato(conexion, transaccion, entrada.Key, valor);
        }

        Ejecutar(conexion, transaccion, OperacionSql.EliminarMetadatosAnteriores);
    }

    private void ComprobarIntegridadCriptografica(SqliteConnection conexion)
    {
        var metadatos = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var comando = conexion.CreateCommand())
        {
            comando.CommandText = "SELECT Clave, Datos FROM Metadatos;";
            using var lector = comando.ExecuteReader();
            while (lector.Read())
            {
                var clave = lector.GetString(0);
                var valor = _cifrador.Descifrar<string>(
                    TablaMetadatos,
                    clave,
                    (byte[])lector[1]);
                if (!metadatos.TryAdd(clave, valor))
                {
                    throw new InvalidDataException("La base contiene metadatos duplicados.");
                }
            }
        }

        var clavesEsperadas = new[]
        {
            "version_esquema",
            "conjunto_id",
            "revision_permisos",
            "revision_catalogo"
        };
        if (metadatos.Count != clavesEsperadas.Length
            || clavesEsperadas.Any(clave => !metadatos.ContainsKey(clave))
            || !string.Equals(metadatos["version_esquema"], "2", StringComparison.Ordinal)
            || !EsHexadecimal(metadatos["conjunto_id"], 32)
            || !EsRevisionValida(metadatos["revision_permisos"])
            || !EsRevisionValida(metadatos["revision_catalogo"]))
        {
            throw new InvalidDataException("Los metadatos cifrados no son validos.");
        }

        _ = LeerFila<ConfiguracionPermisosDatos>(conexion, TablaPermisos, "actual");
        _ = LeerFila<CatalogoEstadoDatos>(conexion, TablaCatalogoEstado, "actual");

        using (var comando = conexion.CreateCommand())
        {
            comando.CommandText = "SELECT Id, IndiceNombre, Datos FROM Usuarios;";
            using var lector = comando.ExecuteReader();
            while (lector.Read())
            {
                var id = lector.GetString(0);
                var indice = (byte[])lector[1];
                var usuario = _cifrador.Descifrar<UsuarioServidorCentral>(
                    TablaUsuarios,
                    id,
                    (byte[])lector[2]);
                var esperado = _cifrador.CrearIndice("usuario", usuario.NombreUsuario);
                if (!string.Equals(id, usuario.Id, StringComparison.Ordinal)
                    || !CryptographicOperations.FixedTimeEquals(indice, esperado)
                    || ConfiguracionServidor.NormalizarCuenta(usuario.NombreUsuario).Length == 0)
                {
                    throw new InvalidDataException("Una fila de usuarios no supera la validacion criptografica.");
                }
            }
        }

        using (var comando = conexion.CreateCommand())
        {
            comando.CommandText = "SELECT Id, IndiceScript, Datos FROM CatalogoScripts;";
            using var lector = comando.ExecuteReader();
            while (lector.Read())
            {
                var id = lector.GetString(0);
                var indice = (byte[])lector[1];
                var entrada = _cifrador.Descifrar<EntradaCatalogoServidor>(
                    TablaCatalogo,
                    id,
                    (byte[])lector[2]);
                var indiceEsperado = _cifrador.CrearIndice("catalogo", entrada.ScriptId);
                var idEsperado = Convert.ToHexString(
                    _cifrador.CrearIndice("catalogo-id", entrada.ScriptId));
                if (!string.Equals(id, idEsperado, StringComparison.Ordinal)
                    || !CryptographicOperations.FixedTimeEquals(indice, indiceEsperado)
                    || !EsHexadecimal(entrada.Sha256, 64))
                {
                    throw new InvalidDataException("Una fila del catalogo no supera la validacion criptografica.");
                }
            }
        }

        using (var comando = conexion.CreateCommand())
        {
            comando.CommandText = "SELECT Id, IndiceUsuario, Datos FROM IdentidadesAuditoria;";
            using var lector = comando.ExecuteReader();
            while (lector.Read())
            {
                var id = lector.GetString(0);
                var indice = (byte[])lector[1];
                var identidad = _cifrador.Descifrar<IdentidadAuditoriaDatos>(
                    TablaIdentidades,
                    id,
                    (byte[])lector[2]);
                var esperado = _cifrador.CrearIndice(
                    "auditoria-usuario",
                    identidad.NombreUsuario);
                if (!string.Equals(id, Convert.ToHexString(esperado), StringComparison.Ordinal)
                    || !CryptographicOperations.FixedTimeEquals(indice, esperado)
                    || ConfiguracionServidor.NormalizarCuenta(identidad.NombreUsuario).Length == 0)
                {
                    throw new InvalidDataException("Una identidad de auditoria no supera la validacion criptografica.");
                }
            }
        }

        using (var comando = conexion.CreateCommand())
        {
            comando.CommandText = "SELECT Id, IndiceUsuario, FechaUtcTicks, Datos FROM Auditoria;";
            using var lector = comando.ExecuteReader();
            while (lector.Read())
            {
                var id = lector.GetString(0);
                var indice = (byte[])lector[1];
                var ticks = lector.GetInt64(2);
                var evento = _cifrador.Descifrar<EventoAuditoriaServidorCentral>(
                    TablaAuditoria,
                    id,
                    (byte[])lector[3]);
                var esperado = _cifrador.CrearIndice(
                    "auditoria-usuario",
                    evento.UsuarioWindows);
                ValidarEventoAuditoria(evento);
                if (!string.Equals(id, evento.EventoId, StringComparison.Ordinal)
                    || ticks != evento.FechaUtc.UtcTicks
                    || !CryptographicOperations.FixedTimeEquals(indice, esperado))
                {
                    throw new InvalidDataException("Un evento de auditoria no supera la validacion criptografica.");
                }
            }
        }
    }

    private static bool EsRevisionValida(string valor)
    {
        return long.TryParse(
            valor,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var revision)
            && revision >= 1;
    }

    private static bool EsHexadecimal(string? valor, int longitud)
    {
        return valor is not null
            && valor.Length == longitud
            && valor.All(caracter => caracter is >= '0' and <= '9'
                or >= 'A' and <= 'F');
    }

    private void AsegurarMetadato(
        SqliteConnection conexion,
        SqliteTransaction transaccion,
        string clave,
        string valor)
    {
        using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = "SELECT COUNT(*) FROM Metadatos WHERE Clave = $clave;";
        comando.Parameters.AddWithValue("$clave", clave);
        if (Convert.ToInt64(comando.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
        {
            EscribirMetadato(conexion, transaccion, clave, valor);
        }
    }

    private string LeerMetadato(
        SqliteConnection conexion,
        string clave,
        SqliteTransaction? transaccion = null)
    {
        using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = "SELECT Datos FROM Metadatos WHERE Clave = $clave LIMIT 1;";
        comando.Parameters.AddWithValue("$clave", clave);
        var datos = comando.ExecuteScalar() as byte[]
            ?? throw new InvalidDataException($"No existe el metadato {clave}.");
        return _cifrador.Descifrar<string>(TablaMetadatos, clave, datos);
    }

    private void IncrementarRevision(
        SqliteConnection conexion,
        SqliteTransaction transaccion,
        string clave)
    {
        var actual = LeerMetadato(conexion, clave, transaccion);
        if (!long.TryParse(actual, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            || revision is < 1 or long.MaxValue)
        {
            throw new InvalidDataException("La revision cifrada de la base no es valida.");
        }

        Ejecutar(
            conexion,
            transaccion,
            OperacionSql.EliminarMetadato,
            ("$clave", clave));
        EscribirMetadato(
            conexion,
            transaccion,
            clave,
            (revision + 1).ToString(CultureInfo.InvariantCulture));
    }

    private void EscribirMetadato(
        SqliteConnection conexion,
        SqliteTransaction transaccion,
        string clave,
        string valor)
    {
        var cifrado = _cifrador.Cifrar(TablaMetadatos, clave, valor);
        Ejecutar(
            conexion,
            transaccion,
            OperacionSql.InsertarMetadato,
            ("$clave", clave),
            ("$datos", cifrado));
    }

    private static string CrearConjuntoId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    }

    private static long EjecutarEscalarLong(
        SqliteConnection conexion,
        OperacionSql operacion,
        SqliteTransaction? transaccion = null)
    {
        using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = operacion switch
        {
            OperacionSql.ContarUsuarios => "SELECT COUNT(*) FROM Usuarios;",
            OperacionSql.ContarAuditorias => "SELECT COUNT(*) FROM Auditoria;",
            OperacionSql.ContarPermisos => "SELECT COUNT(*) FROM PermisosConfiguracion;",
            _ => throw new InvalidOperationException("La consulta escalar no esta autorizada.")
        };
        return Convert.ToInt64(comando.ExecuteScalar());
    }

    private static long? EjecutarEscalarLongNullable(
        SqliteConnection conexion,
        OperacionSql operacion)
    {
        using var comando = conexion.CreateCommand();
        comando.CommandText = operacion switch
        {
            OperacionSql.ObtenerUltimaAuditoria => "SELECT MAX(FechaUtcTicks) FROM Auditoria;",
            _ => throw new InvalidOperationException("La consulta escalar no esta autorizada.")
        };
        var valor = comando.ExecuteScalar();
        return valor is null or DBNull ? null : Convert.ToInt64(valor);
    }

    private static int Ejecutar(
        SqliteConnection conexion,
        SqliteTransaction transaccion,
        OperacionSql operacion,
        params (string Nombre, object Valor)[] parametros)
    {
        using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = operacion switch
        {
            OperacionSql.EliminarUsuarios => "DELETE FROM Usuarios;",
            OperacionSql.EliminarPermisos => "DELETE FROM PermisosConfiguracion;",
            OperacionSql.EliminarUsuario => "DELETE FROM Usuarios WHERE Id = $id;",
            OperacionSql.EliminarCatalogo => "DELETE FROM CatalogoScripts;",
            OperacionSql.InsertarCatalogo =>
                "INSERT INTO CatalogoScripts (Id, IndiceScript, Datos) VALUES ($id, $indice, $datos);",
            OperacionSql.EliminarEstadoCatalogo => "DELETE FROM CatalogoEstado;",
            OperacionSql.InsertarIdentidadAuditoria =>
                "INSERT INTO IdentidadesAuditoria (Id, IndiceUsuario, Datos) " +
                "VALUES ($id, $indice, $datos) " +
                "ON CONFLICT(IndiceUsuario) DO UPDATE SET Datos = excluded.Datos;",
            OperacionSql.InsertarAuditoria =>
                "INSERT INTO Auditoria (Id, IndiceUsuario, FechaUtcTicks, Datos) " +
                "VALUES ($id, $indice, $fecha, $datos);",
            OperacionSql.EliminarAuditoriaAnterior =>
                "DELETE FROM Auditoria WHERE FechaUtcTicks < $limite;",
            OperacionSql.EliminarIdentidadesHuerfanas =>
                "DELETE FROM IdentidadesAuditoria WHERE IndiceUsuario NOT IN " +
                "(SELECT DISTINCT IndiceUsuario FROM Auditoria);",
            OperacionSql.InsertarUsuario =>
                "INSERT INTO Usuarios (Id, IndiceNombre, Datos) VALUES ($id, $indice, $datos);",
            OperacionSql.InsertarPermisos =>
                "INSERT INTO PermisosConfiguracion (Id, Datos) VALUES ($id, $datos);",
            OperacionSql.InsertarEstadoCatalogo =>
                "INSERT INTO CatalogoEstado (Id, Datos) VALUES ($id, $datos);",
            OperacionSql.RenombrarMetadatosAnteriores =>
                "ALTER TABLE Metadatos RENAME TO MetadatosV1;",
            OperacionSql.CrearMetadatosCifrados =>
                "CREATE TABLE Metadatos (Clave TEXT PRIMARY KEY NOT NULL, Datos BLOB NOT NULL) STRICT;",
            OperacionSql.EliminarMetadatosAnteriores => "DROP TABLE MetadatosV1;",
            OperacionSql.EliminarMetadato => "DELETE FROM Metadatos WHERE Clave = $clave;",
            OperacionSql.InsertarMetadato =>
                "INSERT INTO Metadatos (Clave, Datos) VALUES ($clave, $datos);",
            _ => throw new InvalidOperationException("La operacion SQL no esta autorizada.")
        };
        foreach (var parametro in parametros)
        {
            comando.Parameters.AddWithValue(parametro.Nombre, parametro.Valor);
        }

        return comando.ExecuteNonQuery();
    }

    private void ComprobarNoDesechado()
    {
        ObjectDisposedException.ThrowIf(_desechado, this);
    }

    private sealed record ConfiguracionPermisosDatos(
        IReadOnlyList<string> ScriptsAdmin,
        IReadOnlyList<string> ScriptsElevados,
        bool PermitirExecutionPolicyBypass,
        string RolUsuarioActual,
        int MaxScriptsSimultaneos);

    private sealed record CatalogoEstadoDatos(DateTimeOffset GeneradoUtc);

    private sealed record IdentidadAuditoriaDatos(string NombreUsuario);
}
