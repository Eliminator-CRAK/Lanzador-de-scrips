// (Autor: Alex Roman)
// Descripcion: Genera, firma y valida el catalogo externo de scripts autorizados.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanzadorScripts.Servicios;

public sealed class ServicioCatalogoScripts
{
    public const string NombreArchivo = RutasArtefactosProtegidos.NombreCatalogo;

    private const int VersionCatalogo = 1;

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly ServicioArtefactosFirmados _artefactos;

    public ServicioCatalogoScripts()
        : this(new ServicioArtefactosFirmados())
    {
    }

    internal ServicioCatalogoScripts(ServicioArtefactosFirmados artefactos)
    {
        _artefactos = artefactos;
    }

    public CatalogoScripts Crear(
        IReadOnlyList<ScriptInterno> scriptsDetectados,
        IEnumerable<string> scriptsSeleccionados,
        string conjuntoId)
    {
        ServicioArtefactosFirmados.ValidarConjuntoId(conjuntoId);
        var indice = scriptsDetectados.ToDictionary(
            script => NormalizarScriptId(script.Id),
            StringComparer.OrdinalIgnoreCase);
        var seleccionados = scriptsSeleccionados
            .Select(NormalizarScriptId)
            .Where(scriptId => !string.IsNullOrWhiteSpace(scriptId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(scriptId => scriptId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var entradas = new List<EntradaCatalogoScript>(seleccionados.Count);

        foreach (var scriptId in seleccionados)
        {
            if (!indice.TryGetValue(scriptId, out var script))
            {
                throw new InvalidOperationException($"El script seleccionado no existe o no es seguro: {scriptId}");
            }

            var ruta = script.RutaValidada;
            entradas.Add(new EntradaCatalogoScript(
                scriptId,
                ruta.Extension,
                ruta.ObtenerLongitud(),
                ServicioSeguridadScripts.CalcularSha256(ruta)));
        }

        return new CatalogoScripts(
            VersionCatalogo,
            DateTimeOffset.UtcNow,
            conjuntoId,
            entradas);
    }

    public void Guardar(string rutaCatalogo, CatalogoScripts catalogo)
    {
        Validar(catalogo);
        var json = JsonSerializer.Serialize(catalogo, OpcionesJson);
        _artefactos.GuardarTextoFirmado(
            rutaCatalogo,
            ServicioArtefactosFirmados.TipoCatalogoScripts,
            json,
            catalogo.ConjuntoId);
    }

    public bool IntentarCargar(
        string rutaCatalogo,
        out CatalogoScripts? catalogo,
        out string error)
    {
        catalogo = null;
        error = string.Empty;
        try
        {
            if (!_artefactos.IntentarCargarTextoFirmado(
                rutaCatalogo,
                ServicioArtefactosFirmados.TipoCatalogoScripts,
                out var json,
                out var conjuntoIdFirmado,
                out error,
                out _))
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "No se encontro catalogo-scripts.json.";
                }

                return false;
            }

            catalogo = JsonSerializer.Deserialize<CatalogoScripts>(json, OpcionesJson);
            if (catalogo is null)
            {
                error = "El catalogo de scripts no contiene datos validos.";
                return false;
            }

            Validar(catalogo);
            if (!string.Equals(catalogo.ConjuntoId, conjuntoIdFirmado, StringComparison.Ordinal))
            {
                catalogo = null;
                error = "El ConjuntoId interno del catalogo no coincide con el contenedor firmado.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            catalogo = null;
            error = ServicioRedaccionSecretos.Sanitizar(ex.Message);
            return false;
        }
    }

    public IReadOnlyList<EstadoCatalogoScriptCliente> ObtenerEstados(
        IReadOnlyList<ScriptInterno> scriptsDetectados,
        CatalogoScripts? catalogo)
    {
        var indice = catalogo?.Scripts.ToDictionary(
            entrada => entrada.ScriptId,
            StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, EntradaCatalogoScript>(StringComparer.OrdinalIgnoreCase);

        return scriptsDetectados
            .OrderBy(script => script.Id, StringComparer.OrdinalIgnoreCase)
            .Select(script =>
            {
                var ruta = script.RutaValidada;
                var longitud = ruta.ObtenerLongitud();
                var sha256 = ServicioSeguridadScripts.CalcularSha256(ruta);
                if (!indice.TryGetValue(script.Id, out var entrada))
                {
                    return new EstadoCatalogoScriptCliente(
                        script.Id,
                        script.Tipo,
                        longitud,
                        sha256,
                        "no-incluido",
                        false);
                }

                var coincide = entrada.Longitud == longitud
                    && string.Equals(entrada.Extension, ruta.Extension, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entrada.Sha256, sha256, StringComparison.OrdinalIgnoreCase);
                return new EstadoCatalogoScriptCliente(
                    script.Id,
                    script.Tipo,
                    longitud,
                    sha256,
                    coincide ? "autorizado" : "modificado",
                    true);
            })
            .ToList();
    }

    public static string ObtenerRuta(string rutaPermisos)
    {
        return RutasArtefactosProtegidos.DesdeRutaPermisos(rutaPermisos).RutaCatalogo;
    }

    public static EntradaCatalogoScript? Buscar(CatalogoScripts? catalogo, string scriptId)
    {
        return catalogo?.Scripts.FirstOrDefault(
            entrada => string.Equals(entrada.ScriptId, scriptId, StringComparison.OrdinalIgnoreCase));
    }

    private static void Validar(CatalogoScripts catalogo)
    {
        if (catalogo.Version != VersionCatalogo || catalogo.Scripts is null)
        {
            throw new InvalidOperationException("El catalogo de scripts no tiene una version valida.");
        }

        ServicioArtefactosFirmados.ValidarConjuntoId(catalogo.ConjuntoId);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entrada in catalogo.Scripts)
        {
            var scriptId = NormalizarScriptId(entrada.ScriptId);
            var extension = Path.GetExtension(scriptId).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(scriptId)
                || Path.IsPathRooted(scriptId)
                || scriptId.Split('/').Any(segmento => segmento.Length == 0 || segmento is "." or "..")
                || extension is not ".ps1" and not ".bat" and not ".cmd"
                || !string.Equals(extension, entrada.Extension, StringComparison.OrdinalIgnoreCase)
                || entrada.Longitud < 0
                || entrada.Sha256.Length != 64
                || entrada.Sha256.Any(caracter => !Uri.IsHexDigit(caracter))
                || !ids.Add(scriptId))
            {
                throw new InvalidOperationException($"El catalogo contiene una entrada no valida: {scriptId}");
            }
        }
    }

    private static string NormalizarScriptId(string scriptId)
    {
        return scriptId.Replace('\\', '/').Trim();
    }
}

public sealed record CatalogoScripts(
    int Version,
    DateTimeOffset GeneradoUtc,
    string ConjuntoId,
    IReadOnlyList<EntradaCatalogoScript> Scripts);

public sealed record EntradaCatalogoScript(
    string ScriptId,
    string Extension,
    long Longitud,
    string Sha256);
