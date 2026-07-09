// (Autor: Alex Roman)
// Descripcion: Genera, protege y valida el catalogo externo de scripts autorizados.

using System.IO;
using System.Text;
using System.Text.Json;

namespace LanzadorScripts.Servicios;

public sealed class ServicioCatalogoScripts
{
    public const string NombreArchivo = RutasArtefactosProtegidos.NombreCatalogo;

    private const int VersionCatalogo = 1;

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ServicioArtefactosProtegidos _artefactos;

    public ServicioCatalogoScripts()
        : this(new ServicioArtefactosProtegidos())
    {
    }

    internal ServicioCatalogoScripts(ServicioArtefactosProtegidos artefactos)
    {
        _artefactos = artefactos;
    }

    public CatalogoScripts Crear(
        IReadOnlyList<ScriptInterno> scriptsDetectados,
        IEnumerable<string> scriptsSeleccionados)
    {
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

            var info = new FileInfo(script.RutaCompleta);
            entradas.Add(new EntradaCatalogoScript(
                scriptId,
                Path.GetExtension(script.RutaCompleta).ToLowerInvariant(),
                info.Length,
                ServicioSeguridadScripts.CalcularSha256(script.RutaCompleta)));
        }

        return new CatalogoScripts(
            VersionCatalogo,
            DateTimeOffset.UtcNow,
            _artefactos.KeyId,
            entradas);
    }

    public void Guardar(string rutaCatalogo, CatalogoScripts catalogo)
    {
        Validar(catalogo);
        var json = JsonSerializer.Serialize(catalogo, OpcionesJson);
        _artefactos.GuardarTextoProtegido(
            rutaCatalogo,
            ServicioArtefactosProtegidos.TipoCatalogoScripts,
            json);
    }

    public bool IntentarCargar(
        string rutaCatalogo,
        out CatalogoScripts? catalogo,
        out string error)
    {
        catalogo = null;
        error = string.Empty;
        if (!File.Exists(rutaCatalogo))
        {
            error = "No se encontro catalogo-scripts.json.";
            return false;
        }

        try
        {
            if (!_artefactos.IntentarCargarTextoProtegido(
                rutaCatalogo,
                ServicioArtefactosProtegidos.TipoCatalogoScripts,
                out var json,
                out error,
                out _))
            {
                return false;
            }

            catalogo = JsonSerializer.Deserialize<CatalogoScripts>(json, OpcionesJson);
            if (catalogo is null)
            {
                error = "El catalogo de scripts no contiene datos validos.";
                return false;
            }

            Validar(catalogo);
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
                var info = new FileInfo(script.RutaCompleta);
                var sha256 = ServicioSeguridadScripts.CalcularSha256(script.RutaCompleta);
                if (!indice.TryGetValue(script.Id, out var entrada))
                {
                    return new EstadoCatalogoScriptCliente(
                        script.Id,
                        script.Tipo,
                        info.Length,
                        sha256,
                        "no-incluido",
                        false);
                }

                var coincide = entrada.Longitud == info.Length
                    && string.Equals(entrada.Extension, Path.GetExtension(script.RutaCompleta), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entrada.Sha256, sha256, StringComparison.OrdinalIgnoreCase);
                return new EstadoCatalogoScriptCliente(
                    script.Id,
                    script.Tipo,
                    info.Length,
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
        if (catalogo.Version != VersionCatalogo
            || string.IsNullOrWhiteSpace(catalogo.KeyId)
            || catalogo.Scripts is null)
        {
            throw new InvalidOperationException("El catalogo de scripts no tiene una version valida.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entrada in catalogo.Scripts)
        {
            var scriptId = NormalizarScriptId(entrada.ScriptId);
            var extension = Path.GetExtension(scriptId).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(scriptId)
                || Path.IsPathRooted(scriptId)
                || scriptId.Split('/').Any(segmento => segmento is "." or "..")
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
        return scriptId.Replace('\\', '/').Trim().TrimStart('/');
    }
}

public sealed record CatalogoScripts(
    int Version,
    DateTimeOffset GeneradoUtc,
    string KeyId,
    IReadOnlyList<EntradaCatalogoScript> Scripts);

public sealed record EntradaCatalogoScript(
    string ScriptId,
    string Extension,
    long Longitud,
    string Sha256);
