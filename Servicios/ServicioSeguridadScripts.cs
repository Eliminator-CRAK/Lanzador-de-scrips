// (Autor: Alex Roman)
// Descripcion: Valida integridad y firma de scripts antes de ejecutarlos.

using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace LanzadorScripts.Servicios;

public sealed class ServicioSeguridadScripts
{
    private static readonly char[] MetacaracteresPeligrosos = ['&', '|', '<', '>', '^', '%', '!'];

    private readonly ServicioFirmaAuthenticode _servicioFirma = new();

    public DiagnosticoEjecucionScript Diagnosticar(ScriptInterno script, JsonObject permisos)
    {
        var politica = LeerPolitica(permisos);
        var baseDiagnostico = new DiagnosticoEjecucionScript(
            script.Id,
            script.Nombre,
            script.Tipo,
            false,
            string.Empty,
            _servicioFirma.PowerShellDisponible(),
            _servicioFirma.ObtenerExecutionPolicy(),
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            politica.PermitirExecutionPolicyBypass);

        if (ContieneMetacaracteresPeligrosos(script.Id)
            || ContieneMetacaracteresPeligrosos(script.Nombre)
            || ContieneMetacaracteresPeligrosos(Path.GetRelativePath(Path.GetPathRoot(script.RutaCompleta) ?? string.Empty, script.RutaCompleta)))
        {
            return baseDiagnostico with { MotivoBloqueo = "El nombre o la ruta del script contiene metacaracteres peligrosos." };
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

        return new PoliticaSeguridadScripts(
            certificados,
            hashes,
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

        return new JsonObject
        {
            ["certificadosPowerShellPermitidos"] = certificados,
            ["hashesBatchPermitidos"] = hashes,
            ["permitirExecutionPolicyBypass"] = LeerBooleano(seguridad, "permitirExecutionPolicyBypass", false)
        };
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

        var firma = _servicioFirma.ObtenerFirma(script.RutaCompleta);
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
}

public sealed record PoliticaSeguridadScripts(
    IReadOnlySet<string> CertificadosPowerShellPermitidos,
    IReadOnlyDictionary<string, string> HashesBatchPermitidos,
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
    bool ExecutionPolicyBypassPermitido);
