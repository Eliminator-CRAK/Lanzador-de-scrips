// (Autor: Alex Roman)
// Descripcion: Valida scripts contra el catalogo cifrado antes de ejecutarlos.

using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace LanzadorScripts.Servicios;

public sealed class ServicioSeguridadScripts
{
    private static readonly char[] MetacaracteresPeligrosos = ['&', '|', '<', '>', '^', '%', '!'];

    private readonly ServicioFirmaAuthenticode _servicioPowerShell = new();
    private bool? _powerShellDisponible;
    private string? _executionPolicy;

    public DiagnosticoEjecucionScript Diagnosticar(
        ScriptInterno script,
        JsonObject permisos,
        CatalogoScripts? catalogo,
        string errorCatalogo,
        bool modoDesarrolloFirmas = false)
    {
        var politica = LeerPolitica(permisos);
        var baseDiagnostico = new DiagnosticoEjecucionScript(
            script.Id,
            script.Nombre,
            script.Tipo,
            false,
            string.Empty,
            script.Tipo != "powershell" || PowerShellDisponibleCacheado(),
            script.Tipo == "powershell" ? ObtenerExecutionPolicyCacheada() : "No aplica",
            catalogo is null ? "invalido" : "cargado",
            catalogo?.KeyId ?? string.Empty,
            string.Empty,
            politica.PermitirExecutionPolicyBypass,
            modoDesarrolloFirmas);

        if (ContieneMetacaracteresPeligrosos(script.Id)
            || ContieneMetacaracteresPeligrosos(script.Nombre)
            || ContieneMetacaracteresPeligrosos(
                Path.GetRelativePath(
                    script.RutaValidada.RaizAutorizada,
                    script.RutaCompleta)))
        {
            return baseDiagnostico with
            {
                MotivoBloqueo = "El nombre o la ruta del script contiene metacaracteres peligrosos."
            };
        }

        if (script.Tipo == "powershell" && !baseDiagnostico.PowerShellDisponible)
        {
            return baseDiagnostico with
            {
                MotivoBloqueo = "PowerShell 5.1 no esta disponible."
            };
        }

        if (modoDesarrolloFirmas)
        {
            return baseDiagnostico with
            {
                Permitido = true,
                MotivoBloqueo = "Modo desarrollo activo: validacion del catalogo omitida.",
                CatalogoEstado = "omitido",
                Sha256 = CalcularSha256(script.RutaValidada)
            };
        }

        if (catalogo is null)
        {
            return baseDiagnostico with
            {
                MotivoBloqueo = string.IsNullOrWhiteSpace(errorCatalogo)
                    ? "El catalogo de scripts no es valido."
                    : errorCatalogo
            };
        }

        var entrada = ServicioCatalogoScripts.Buscar(catalogo, script.Id);
        if (entrada is null)
        {
            return baseDiagnostico with
            {
                CatalogoEstado = "no-incluido",
                MotivoBloqueo = "El script no esta incluido en el catalogo firmado."
            };
        }

        var longitud = script.RutaValidada.ObtenerLongitud();
        var hash = CalcularSha256(script.RutaValidada);
        var extension = script.RutaValidada.Extension;
        var coincide = entrada.Longitud == longitud
            && string.Equals(entrada.Extension, extension, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entrada.Sha256, hash, StringComparison.OrdinalIgnoreCase);

        if (!coincide)
        {
            return baseDiagnostico with
            {
                CatalogoEstado = "modificado",
                Sha256 = hash,
                MotivoBloqueo = "El script fue modificado, movido o sustituido despues de publicar el catalogo."
            };
        }

        return baseDiagnostico with
        {
            Permitido = true,
            CatalogoEstado = "autorizado",
            Sha256 = hash
        };
    }

    public static PoliticaSeguridadScripts LeerPolitica(JsonObject permisos)
    {
        var seguridad = permisos["seguridadScripts"] as JsonObject;
        var scriptsElevados = LeerArrayTexto(seguridad?["scriptsElevadosPermitidos"] as JsonArray)
            .Select(valor => valor.Replace('\\', '/').Trim())
            .Where(valor => !string.IsNullOrWhiteSpace(valor))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new PoliticaSeguridadScripts(
            scriptsElevados,
            LeerBooleano(seguridad, "permitirExecutionPolicyBypass", false));
    }

    public static JsonObject NormalizarPolitica(JsonObject? seguridad)
    {
        var scriptsElevados = new JsonArray();
        foreach (var scriptId in LeerArrayTexto(seguridad?["scriptsElevadosPermitidos"] as JsonArray)
            .Select(valor => valor.Replace('\\', '/').Trim())
            .Where(valor => !string.IsNullOrWhiteSpace(valor))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(valor => valor, StringComparer.OrdinalIgnoreCase))
        {
            scriptsElevados.Add(scriptId);
        }

        return new JsonObject
        {
            ["scriptsElevadosPermitidos"] = scriptsElevados,
            ["permitirExecutionPolicyBypass"] = LeerBooleano(
                seguridad,
                "permitirExecutionPolicyBypass",
                false)
        };
    }

    public static bool RequiereBrokerElevado(ScriptInterno script, JsonObject permisos)
    {
        return LeerPolitica(permisos).ScriptsElevadosPermitidos.Contains(script.Id);
    }

    public static string CalcularSha256(RutaScriptValidada ruta)
    {
        using var flujo = ruta.AbrirLectura();
        return Convert.ToHexString(SHA256.HashData(flujo));
    }

    public static bool ContieneMetacaracteresPeligrosos(string texto)
    {
        return texto.IndexOfAny(MetacaracteresPeligrosos) >= 0;
    }

    private static IEnumerable<string> LeerArrayTexto(JsonArray? valores)
    {
        return valores is null
            ? []
            : valores.Select(valor => valor?.GetValue<string>() ?? string.Empty);
    }

    private static bool LeerBooleano(JsonObject? nodo, string propiedad, bool valorDefecto)
    {
        return nodo?[propiedad]?.GetValue<bool>() ?? valorDefecto;
    }

    private bool PowerShellDisponibleCacheado()
    {
        if (_powerShellDisponible.HasValue)
        {
            return _powerShellDisponible.Value;
        }

        _powerShellDisponible = _servicioPowerShell.PowerShellDisponible();
        return _powerShellDisponible.Value;
    }

    private string ObtenerExecutionPolicyCacheada()
    {
        if (!string.IsNullOrWhiteSpace(_executionPolicy))
        {
            return _executionPolicy;
        }

        _executionPolicy = _servicioPowerShell.ObtenerExecutionPolicy();
        return _executionPolicy;
    }
}

public sealed record PoliticaSeguridadScripts(
    IReadOnlySet<string> ScriptsElevadosPermitidos,
    bool PermitirExecutionPolicyBypass);

public sealed record DiagnosticoEjecucionScript(
    string ScriptId,
    string Nombre,
    string Tipo,
    bool Permitido,
    string MotivoBloqueo,
    bool PowerShellDisponible,
    string ExecutionPolicy,
    string CatalogoEstado,
    string CatalogoKeyId,
    string Sha256,
    bool ExecutionPolicyBypassPermitido,
    bool ModoDesarrolloFirmas = false);
