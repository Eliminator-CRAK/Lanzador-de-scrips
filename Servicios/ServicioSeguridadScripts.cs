// (Autor: Alex Roman)
// Descripcion: Valida integridad y firma de scripts antes de ejecutarlos.

using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace LanzadorScripts.Servicios;

public sealed class ServicioSeguridadScripts
{
    private static readonly char[] MetacaracteresPeligrosos = ['&', '|', '<', '>', '^', '%', '!'];

    private readonly object _bloqueoCache = new();
    private readonly Dictionary<string, EntradaFirmaCache> _cacheFirmas = new(StringComparer.OrdinalIgnoreCase);
    private readonly ServicioFirmaAuthenticode _servicioFirma = new();
    private bool? _powerShellDisponible;
    private string? _executionPolicy;

    public void PrecargarFirmas(IEnumerable<ScriptInterno> scripts)
    {
        var pendientes = scripts
            .Where(script => script.Tipo == "powershell")
            .Select(script => script.RutaCompleta)
            .Where(ruta => !TryGetFirmaCacheada(ruta, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pendientes.Count == 0)
        {
            return;
        }

        foreach (var firma in _servicioFirma.ObtenerFirmas(pendientes))
        {
            GuardarFirmaCacheada(firma.Key, firma.Value);
        }
    }

    public DiagnosticoEjecucionScript Diagnosticar(ScriptInterno script, JsonObject permisos, bool modoDesarrolloFirmas = false)
    {
        var politica = LeerPolitica(permisos);
        var baseDiagnostico = new DiagnosticoEjecucionScript(
            script.Id,
            script.Nombre,
            script.Tipo,
            false,
            string.Empty,
            PowerShellDisponibleCacheado(),
            ObtenerExecutionPolicyCacheada(),
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            politica.PermitirExecutionPolicyBypass,
            modoDesarrolloFirmas);

        if (ContieneMetacaracteresPeligrosos(script.Id)
            || ContieneMetacaracteresPeligrosos(script.Nombre)
            || ContieneMetacaracteresPeligrosos(Path.GetRelativePath(Path.GetPathRoot(script.RutaCompleta) ?? string.Empty, script.RutaCompleta)))
        {
            return baseDiagnostico with { MotivoBloqueo = "El nombre o la ruta del script contiene metacaracteres peligrosos." };
        }

        if (modoDesarrolloFirmas)
        {
            return DiagnosticarModoDesarrollo(script, baseDiagnostico);
        }

        if (script.Tipo == "powershell")
        {
            return DiagnosticarPowerShell(script, politica, baseDiagnostico);
        }

        return DiagnosticarBatch(script, politica, baseDiagnostico);
    }

    public static PoliticaSeguridadScripts LeerPolitica(JsonObject permisos)
    {
        var seguridad = permisos["seguridadScripts"] as JsonObject;
        var certificados = LeerArrayTexto(seguridad?["certificadosPowerShellPermitidos"] as JsonArray)
            .Select(ServicioFirmaAuthenticode.NormalizarThumbprint)
            .Where(valor => valor.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (seguridad?["hashesBatchPermitidos"] is JsonArray hashesJson)
        {
            foreach (var item in hashesJson.OfType<JsonObject>())
            {
                var scriptId = LeerTexto(item, "scriptId").Replace('\\', '/').Trim();
                var sha256 = NormalizarSha256(LeerTexto(item, "sha256"));
                if (!string.IsNullOrWhiteSpace(scriptId) && sha256.Length == 64)
                {
                    hashes[scriptId] = sha256;
                }
            }
        }

        var scriptsElevados = LeerArrayTexto(seguridad?["scriptsElevadosPermitidos"] as JsonArray)
            .Select(valor => valor.Replace('\\', '/').Trim())
            .Where(valor => !string.IsNullOrWhiteSpace(valor))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new PoliticaSeguridadScripts(
            certificados,
            hashes,
            scriptsElevados,
            LeerBooleano(seguridad, "permitirExecutionPolicyBypass", false));
    }

    public static JsonObject NormalizarPolitica(JsonObject? seguridad)
    {
        var certificados = new JsonArray();
        foreach (var certificado in LeerArrayTexto(seguridad?["certificadosPowerShellPermitidos"] as JsonArray)
            .Select(ServicioFirmaAuthenticode.NormalizarThumbprint)
            .Where(valor => valor.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(valor => valor, StringComparer.OrdinalIgnoreCase))
        {
            certificados.Add(certificado);
        }

        var hashes = new JsonArray();
        if (seguridad?["hashesBatchPermitidos"] is JsonArray hashesJson)
        {
            foreach (var item in hashesJson.OfType<JsonObject>())
            {
                var scriptId = LeerTexto(item, "scriptId").Replace('\\', '/').Trim();
                var sha256 = NormalizarSha256(LeerTexto(item, "sha256"));
                if (!string.IsNullOrWhiteSpace(scriptId) && sha256.Length == 64)
                {
                    hashes.Add(new JsonObject
                    {
                        ["scriptId"] = scriptId,
                        ["sha256"] = sha256
                    });
                }
            }
        }

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
            ["certificadosPowerShellPermitidos"] = certificados,
            ["hashesBatchPermitidos"] = hashes,
            ["scriptsElevadosPermitidos"] = scriptsElevados,
            ["permitirExecutionPolicyBypass"] = LeerBooleano(seguridad, "permitirExecutionPolicyBypass", false)
        };
    }

    public static bool RequiereBrokerElevado(ScriptInterno script, JsonObject permisos)
    {
        return LeerPolitica(permisos).ScriptsElevadosPermitidos.Contains(script.Id);
    }

    public static string CalcularSha256(string ruta)
    {
        using var flujo = File.OpenRead(ruta);
        return Convert.ToHexString(SHA256.HashData(flujo));
    }

    public static bool ContieneMetacaracteresPeligrosos(string texto)
    {
        return texto.IndexOfAny(MetacaracteresPeligrosos) >= 0;
    }

    private DiagnosticoEjecucionScript DiagnosticarPowerShell(
        ScriptInterno script,
        PoliticaSeguridadScripts politica,
        DiagnosticoEjecucionScript diagnostico)
    {
        if (!diagnostico.PowerShellDisponible)
        {
            return diagnostico with { MotivoBloqueo = "PowerShell 5.1 no esta disponible." };
        }

        var firma = ObtenerFirmaCacheada(script.RutaCompleta);
        diagnostico = diagnostico with
        {
            FirmaEstado = firma.Estado,
            FirmaThumbprint = firma.Thumbprint,
            FirmaSubject = firma.Subject
        };

        if (!firma.ConsultaCorrecta)
        {
            return diagnostico with { MotivoBloqueo = firma.Error };
        }

        if (!firma.FirmaValida)
        {
            return diagnostico with { MotivoBloqueo = $"Firma Authenticode no valida: {firma.Estado}." };
        }

        if (politica.CertificadosPowerShellPermitidos.Count == 0)
        {
            return diagnostico with { MotivoBloqueo = "No hay certificados PowerShell permitidos configurados." };
        }

        if (!politica.CertificadosPowerShellPermitidos.Contains(firma.Thumbprint))
        {
            return diagnostico with { MotivoBloqueo = "El certificado firmante del script no esta permitido." };
        }

        return diagnostico with { Permitido = true };
    }

    private static DiagnosticoEjecucionScript DiagnosticarModoDesarrollo(
        ScriptInterno script,
        DiagnosticoEjecucionScript diagnostico)
    {
        if (script.Tipo == "powershell" && !diagnostico.PowerShellDisponible)
        {
            return diagnostico with { MotivoBloqueo = "PowerShell 5.1 no esta disponible." };
        }

        if (script.Tipo != "powershell")
        {
            diagnostico = diagnostico with { Sha256 = CalcularSha256(script.RutaCompleta) };
        }

        return diagnostico with
        {
            Permitido = true,
            MotivoBloqueo = "Modo desarrollo activo: validacion de firma/hash omitida."
        };
    }

    private static DiagnosticoEjecucionScript DiagnosticarBatch(
        ScriptInterno script,
        PoliticaSeguridadScripts politica,
        DiagnosticoEjecucionScript diagnostico)
    {
        var hash = CalcularSha256(script.RutaCompleta);
        diagnostico = diagnostico with { Sha256 = hash };

        if (politica.HashesBatchPermitidos.Count == 0)
        {
            return diagnostico with { MotivoBloqueo = "No hay hashes SHA-256 permitidos configurados." };
        }

        if (!politica.HashesBatchPermitidos.TryGetValue(script.Id, out var esperado)
            || !string.Equals(esperado, hash, StringComparison.OrdinalIgnoreCase))
        {
            return diagnostico with { MotivoBloqueo = "El hash SHA-256 del script no esta permitido." };
        }

        return diagnostico with { Permitido = true };
    }

    private static IEnumerable<string> LeerArrayTexto(JsonArray? valores)
    {
        return valores is null
            ? []
            : valores.Select(valor => valor?.GetValue<string>() ?? string.Empty);
    }

    private static string LeerTexto(JsonObject? nodo, string propiedad)
    {
        return nodo?[propiedad]?.GetValue<string>() ?? string.Empty;
    }

    private static bool LeerBooleano(JsonObject? nodo, string propiedad, bool valorDefecto)
    {
        return nodo?[propiedad]?.GetValue<bool>() ?? valorDefecto;
    }

    private static string NormalizarSha256(string hash)
    {
        return string.Concat(hash.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    }

    private bool PowerShellDisponibleCacheado()
    {
        if (_powerShellDisponible.HasValue)
        {
            return _powerShellDisponible.Value;
        }

        _powerShellDisponible = _servicioFirma.PowerShellDisponible();
        return _powerShellDisponible.Value;
    }

    private string ObtenerExecutionPolicyCacheada()
    {
        if (!string.IsNullOrWhiteSpace(_executionPolicy))
        {
            return _executionPolicy;
        }

        _executionPolicy = _servicioFirma.ObtenerExecutionPolicy();
        return _executionPolicy;
    }

    private ResultadoFirmaAuthenticode ObtenerFirmaCacheada(string ruta)
    {
        if (TryGetFirmaCacheada(ruta, out var firma))
        {
            return firma;
        }

        firma = _servicioFirma.ObtenerFirma(ruta);
        GuardarFirmaCacheada(ruta, firma);
        return firma;
    }

    private bool TryGetFirmaCacheada(string ruta, out ResultadoFirmaAuthenticode firma)
    {
        firma = ResultadoFirmaAuthenticode.Fallo("Firma no cacheada.");
        var info = new FileInfo(ruta);
        if (!info.Exists)
        {
            return false;
        }

        lock (_bloqueoCache)
        {
            if (_cacheFirmas.TryGetValue(ruta, out var entrada)
                && entrada.Longitud == info.Length
                && entrada.UltimaEscrituraUtc == info.LastWriteTimeUtc)
            {
                firma = entrada.Firma;
                return true;
            }
        }

        return false;
    }

    private void GuardarFirmaCacheada(string ruta, ResultadoFirmaAuthenticode firma)
    {
        var info = new FileInfo(ruta);
        if (!info.Exists)
        {
            return;
        }

        lock (_bloqueoCache)
        {
            _cacheFirmas[ruta] = new EntradaFirmaCache(info.Length, info.LastWriteTimeUtc, firma);
        }
    }

    private sealed record EntradaFirmaCache(long Longitud, DateTime UltimaEscrituraUtc, ResultadoFirmaAuthenticode Firma);
}

public sealed record PoliticaSeguridadScripts(
    IReadOnlySet<string> CertificadosPowerShellPermitidos,
    IReadOnlyDictionary<string, string> HashesBatchPermitidos,
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
    string FirmaEstado,
    string FirmaThumbprint,
    string FirmaSubject,
    string Sha256,
    bool ExecutionPolicyBypassPermitido,
    bool ModoDesarrolloFirmas = false);
