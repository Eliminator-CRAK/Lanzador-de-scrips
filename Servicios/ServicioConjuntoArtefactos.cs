// (Autor: Alex Roman)
// Descripcion: Valida y actualiza permisos y catalogo como un unico conjunto firmado.

using System.IO;
using System.Text.Json.Nodes;

namespace LanzadorScripts.Servicios;

public sealed class ServicioConjuntoArtefactos
{
    private const string NombreBloqueo = ".lanzadorscripts-conjunto.lock";
    private static readonly TimeSpan EsperaMaximaBloqueo = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> PropiedadesPermisos = new(StringComparer.Ordinal)
    {
        "scriptsAdmin",
        "usuarios",
        "seguridadScripts",
        "rolUsuarioActual",
        "maxScriptsSimultaneos"
    };

    private static readonly HashSet<string> PropiedadesUsuario = new(StringComparer.Ordinal)
    {
        "id",
        "nombreUsuario",
        "rol",
        "maxScriptsSimultaneos",
        "carpetasPermitidas"
    };

    private static readonly HashSet<string> PropiedadesSeguridad = new(StringComparer.Ordinal)
    {
        "scriptsElevadosPermitidos",
        "permitirExecutionPolicyBypass"
    };

    private readonly ServicioArtefactosFirmados _artefactos;
    private readonly ServicioCatalogoScripts _catalogos;

    public ServicioConjuntoArtefactos()
        : this(new ServicioArtefactosFirmados())
    {
    }

    internal ServicioConjuntoArtefactos(ServicioArtefactosFirmados artefactos)
    {
        _artefactos = artefactos;
        _catalogos = new ServicioCatalogoScripts(artefactos);
    }

    public bool IntentarCargarPermisos(
        string rutaPermisos,
        out JsonObject permisos,
        out string conjuntoId,
        out string error,
        out bool recuperado)
    {
        permisos = new JsonObject();
        conjuntoId = string.Empty;
        if (!_artefactos.IntentarCargarTextoFirmado(
                rutaPermisos,
                ServicioArtefactosFirmados.TipoPermisos,
                out var json,
                out conjuntoId,
                out error,
                out recuperado))
        {
            return false;
        }

        try
        {
            permisos = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException("El contenido de permisos no es un objeto JSON.");
            if (!ValidarEstructuraPermisos(permisos, out error))
            {
                permisos = new JsonObject();
                conjuntoId = string.Empty;
                return false;
            }

            return true;
        }
        catch
        {
            permisos = new JsonObject();
            conjuntoId = string.Empty;
            error = "El contenido firmado de permisos no es valido.";
            return false;
        }
    }

    public bool IntentarCargarPareja(
        string rutaPermisos,
        out JsonObject permisos,
        out CatalogoScripts? catalogo,
        out string conjuntoId,
        out string error)
    {
        permisos = new JsonObject();
        catalogo = null;
        conjuntoId = string.Empty;
        if (!IntentarCargarPermisos(
                rutaPermisos,
                out permisos,
                out var conjuntoPermisos,
                out error,
                out _))
        {
            return false;
        }

        var rutaCatalogo = ServicioCatalogoScripts.ObtenerRuta(rutaPermisos);
        if (!_catalogos.IntentarCargar(rutaCatalogo, out catalogo, out error)
            || catalogo is null)
        {
            permisos = new JsonObject();
            return false;
        }

        if (!string.Equals(conjuntoPermisos, catalogo.ConjuntoId, StringComparison.Ordinal))
        {
            permisos = new JsonObject();
            catalogo = null;
            error = "Permisos y catalogo no pertenecen al mismo ConjuntoId firmado.";
            return false;
        }

        conjuntoId = conjuntoPermisos;
        return true;
    }

    public void GuardarPermisosPreservandoConjunto(string rutaPermisos, JsonObject permisos)
    {
        ArgumentNullException.ThrowIfNull(permisos);
        EjecutarConBloqueo(rutaPermisos, () =>
        {
            if (!IntentarCargarPareja(
                    rutaPermisos,
                    out _,
                    out _,
                    out var conjuntoId,
                    out var error))
            {
                throw new InvalidOperationException(
                    $"No se pueden guardar permisos sin una pareja firmada valida. {error}");
            }

            _artefactos.GuardarTextoFirmado(
                rutaPermisos,
                ServicioArtefactosFirmados.TipoPermisos,
                permisos.ToJsonString(),
                conjuntoId);
            ValidarDespuesDeEscribir(rutaPermisos, rutaPermisos);
        });
    }

    public void GuardarCatalogoPreservandoConjunto(
        string rutaPermisos,
        IReadOnlyList<ScriptInterno> scripts,
        IEnumerable<string> seleccionados,
        out CatalogoScripts catalogo)
    {
        CatalogoScripts? resultado = null;
        EjecutarConBloqueo(rutaPermisos, () =>
        {
            if (!IntentarCargarPareja(
                    rutaPermisos,
                    out _,
                    out _,
                    out var conjuntoId,
                    out var error))
            {
                throw new InvalidOperationException(
                    $"No se puede publicar el catalogo sin una pareja firmada valida. {error}");
            }

            resultado = _catalogos.Crear(scripts, seleccionados, conjuntoId);
            var rutaCatalogo = ServicioCatalogoScripts.ObtenerRuta(rutaPermisos);
            _catalogos.Guardar(rutaCatalogo, resultado);
            ValidarDespuesDeEscribir(rutaPermisos, rutaCatalogo);
        });
        catalogo = resultado
            ?? throw new InvalidOperationException("No se pudo crear el catalogo firmado.");
    }

    private void ValidarDespuesDeEscribir(string rutaPermisos, string rutaModificada)
    {
        if (IntentarCargarPareja(
                rutaPermisos,
                out _,
                out _,
                out _,
                out var error))
        {
            return;
        }

        RestaurarRespaldo(rutaModificada);
        throw new InvalidOperationException(
            $"La actualizacion no dejo una pareja firmada valida y se restauro la copia anterior. {error}");
    }

    private static void EjecutarConBloqueo(string rutaPermisos, Action accion)
    {
        var rutas = RutasArtefactosProtegidos.DesdeRutaPermisos(rutaPermisos);
        Directory.CreateDirectory(rutas.Carpeta);
        var rutaBloqueo = ServicioRutasSeguras.ResolverArchivoEnCarpeta(
            rutas.Carpeta,
            NombreBloqueo,
            "bloqueo del conjunto firmado");
        var limite = DateTime.UtcNow + EsperaMaximaBloqueo;
        FileStream? bloqueo = null;
        while (bloqueo is null)
        {
            try
            {
                bloqueo = new FileStream(
                    rutaBloqueo,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.WriteThrough);
            }
            catch (IOException) when (DateTime.UtcNow < limite)
            {
                Thread.Sleep(100);
            }
        }

        try
        {
            accion();
        }
        finally
        {
            bloqueo.Dispose();
            try
            {
                File.Delete(rutaBloqueo);
            }
            catch (IOException)
            {
                // Otro proceso puede haber abierto ya el archivo de bloqueo.
            }
        }
    }

    private static void RestaurarRespaldo(string ruta)
    {
        var respaldo = ruta + ".bak";
        if (!File.Exists(respaldo))
        {
            throw new IOException("No existe una copia de respaldo para restaurar el artefacto anterior.");
        }

        var temporal = ruta + $".{Guid.NewGuid():N}.restore";
        try
        {
            File.Copy(respaldo, temporal, overwrite: false);
            File.Replace(temporal, ruta, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        finally
        {
            if (File.Exists(temporal))
            {
                File.Delete(temporal);
            }
        }
    }

    private static bool ValidarEstructuraPermisos(JsonObject permisos, out string error)
    {
        // Exige el esquema conocido para no aceptar campos firmados que la aplicacion ignore.
        if (!TienePropiedadesExactas(permisos, PropiedadesPermisos)
            || permisos["scriptsAdmin"] is not JsonArray scriptsAdmin
            || !EsArrayDeTextos(scriptsAdmin)
            || permisos["usuarios"] is not JsonArray usuarios
            || permisos["seguridadScripts"] is not JsonObject seguridad
            || !EsTexto(permisos["rolUsuarioActual"])
            || !EsEntero(permisos["maxScriptsSimultaneos"])
            || !TienePropiedadesExactas(seguridad, PropiedadesSeguridad)
            || seguridad["scriptsElevadosPermitidos"] is not JsonArray scriptsElevados
            || !EsArrayDeTextos(scriptsElevados)
            || !EsBooleano(seguridad["permitirExecutionPolicyBypass"]))
        {
            error = "El contenido firmado de permisos contiene propiedades o tipos no permitidos.";
            return false;
        }

        foreach (var usuario in usuarios)
        {
            if (usuario is not JsonObject objetoUsuario
                || !TienePropiedadesExactas(objetoUsuario, PropiedadesUsuario)
                || !EsTexto(objetoUsuario["id"])
                || !EsTexto(objetoUsuario["nombreUsuario"])
                || !EsTexto(objetoUsuario["rol"])
                || !EsEntero(objetoUsuario["maxScriptsSimultaneos"])
                || objetoUsuario["carpetasPermitidas"] is not JsonArray carpetas
                || !EsArrayDeTextos(carpetas))
            {
                error = "El contenido firmado de permisos contiene un usuario no valido.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TienePropiedadesExactas(
        JsonObject objeto,
        IReadOnlySet<string> propiedades)
    {
        return objeto.Count == propiedades.Count
            && objeto.All(propiedad => propiedades.Contains(propiedad.Key));
    }

    private static bool EsArrayDeTextos(JsonArray valores)
    {
        return valores.All(EsTexto);
    }

    private static bool EsTexto(JsonNode? valor)
    {
        return valor is JsonValue json
            && json.TryGetValue<string>(out _);
    }

    private static bool EsEntero(JsonNode? valor)
    {
        return valor is JsonValue json
            && json.TryGetValue<int>(out _);
    }

    private static bool EsBooleano(JsonNode? valor)
    {
        return valor is JsonValue json
            && json.TryGetValue<bool>(out _);
    }
}
