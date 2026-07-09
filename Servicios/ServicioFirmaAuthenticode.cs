// (Autor: Alex Roman)
// Descripcion: Obtiene informacion de firma Authenticode usando PowerShell del sistema.

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LanzadorScripts.Servicios;

public sealed class ServicioFirmaAuthenticode
{
    private static readonly TimeSpan TiempoMaximoConsulta = TimeSpan.FromSeconds(20);

    public ResultadoFirmaAuthenticode ObtenerFirma(string rutaArchivo)
    {
        if (!File.Exists(rutaArchivo))
        {
            return ResultadoFirmaAuthenticode.Fallo("Archivo no encontrado.");
        }

        var rutaPowerShell = ObtenerRutaPowerShell();
        if (!File.Exists(rutaPowerShell))
        {
            return ResultadoFirmaAuthenticode.Fallo("PowerShell 5.1 no esta disponible.");
        }

        var comando = CrearComandoFirma(rutaArchivo);
        using var proceso = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = rutaPowerShell,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        proceso.StartInfo.ArgumentList.Add("-NoLogo");
        proceso.StartInfo.ArgumentList.Add("-NoProfile");
        proceso.StartInfo.ArgumentList.Add("-NonInteractive");
        proceso.StartInfo.ArgumentList.Add("-EncodedCommand");
        proceso.StartInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(comando)));

        try
        {
            if (!EjecutarProceso(proceso, out var salida, out var error))
            {
                return ResultadoFirmaAuthenticode.Fallo("La consulta de firma supero el tiempo maximo.");
            }

            if (proceso.ExitCode != 0 || string.IsNullOrWhiteSpace(salida))
            {
                return ResultadoFirmaAuthenticode.Fallo(ServicioRedaccionSecretos.Sanitizar(error));
            }

            var nodo = JsonNode.Parse(salida) as JsonObject;
            if (nodo is null)
            {
                return ResultadoFirmaAuthenticode.Fallo("La salida de firma no es valida.");
            }

            return new ResultadoFirmaAuthenticode(
                LeerTexto(nodo, "status"),
                LeerTexto(nodo, "statusMessage"),
                NormalizarThumbprint(LeerTexto(nodo, "thumbprint")),
                LeerTexto(nodo, "subject"),
                LeerTexto(nodo, "issuer"),
                LeerTexto(nodo, "notAfter"),
                string.Empty);
        }
        catch (Exception ex)
        {
            return ResultadoFirmaAuthenticode.Fallo(ServicioRedaccionSecretos.Sanitizar(ex.Message));
        }
    }

    public IReadOnlyDictionary<string, ResultadoFirmaAuthenticode> ObtenerFirmas(IReadOnlyList<string> rutasArchivo)
    {
        var resultado = new Dictionary<string, ResultadoFirmaAuthenticode>(StringComparer.OrdinalIgnoreCase);
        var rutas = rutasArchivo
            .Where(ruta => !string.IsNullOrWhiteSpace(ruta))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var ruta in rutas.Where(ruta => !File.Exists(ruta)))
        {
            resultado[ruta] = ResultadoFirmaAuthenticode.Fallo("Archivo no encontrado.");
        }

        var rutasExistentes = rutas.Where(File.Exists).ToList();
        if (rutasExistentes.Count == 0)
        {
            return resultado;
        }

        var rutaPowerShell = ObtenerRutaPowerShell();
        if (!File.Exists(rutaPowerShell))
        {
            foreach (var ruta in rutasExistentes)
            {
                resultado[ruta] = ResultadoFirmaAuthenticode.Fallo("PowerShell 5.1 no esta disponible.");
            }

            return resultado;
        }

        var comando = CrearComandoFirmas(rutasExistentes);
        using var proceso = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = rutaPowerShell,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        proceso.StartInfo.ArgumentList.Add("-NoLogo");
        proceso.StartInfo.ArgumentList.Add("-NoProfile");
        proceso.StartInfo.ArgumentList.Add("-NonInteractive");
        proceso.StartInfo.ArgumentList.Add("-EncodedCommand");
        proceso.StartInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(comando)));

        try
        {
            if (!EjecutarProceso(proceso, out var salida, out var error))
            {
                return CrearResultadoFallo(rutasExistentes, resultado, "La consulta de firma supero el tiempo maximo.");
            }

            if (proceso.ExitCode != 0 || string.IsNullOrWhiteSpace(salida))
            {
                return CrearResultadoFallo(rutasExistentes, resultado, ServicioRedaccionSecretos.Sanitizar(error));
            }

            var nodo = JsonNode.Parse(salida);
            foreach (var item in EnumerarObjetos(nodo))
            {
                var ruta = LeerTexto(item, "path");
                if (string.IsNullOrWhiteSpace(ruta))
                {
                    continue;
                }

                resultado[ruta] = new ResultadoFirmaAuthenticode(
                    LeerTexto(item, "status"),
                    LeerTexto(item, "statusMessage"),
                    NormalizarThumbprint(LeerTexto(item, "thumbprint")),
                    LeerTexto(item, "subject"),
                    LeerTexto(item, "issuer"),
                    LeerTexto(item, "notAfter"),
                    string.Empty);
            }
        }
        catch (Exception ex)
        {
            return CrearResultadoFallo(rutasExistentes, resultado, ServicioRedaccionSecretos.Sanitizar(ex.Message));
        }

        foreach (var ruta in rutasExistentes.Where(ruta => !resultado.ContainsKey(ruta)))
        {
            resultado[ruta] = ResultadoFirmaAuthenticode.Fallo("La salida de firma no contiene el script.");
        }

        return resultado;
    }

    public bool PowerShellDisponible()
    {
        return File.Exists(ObtenerRutaPowerShell());
    }

    public string ObtenerExecutionPolicy()
    {
        var rutaPowerShell = ObtenerRutaPowerShell();
        if (!File.Exists(rutaPowerShell))
        {
            return "PowerShell no disponible";
        }

        using var proceso = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = rutaPowerShell,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        proceso.StartInfo.ArgumentList.Add("-NoLogo");
        proceso.StartInfo.ArgumentList.Add("-NoProfile");
        proceso.StartInfo.ArgumentList.Add("-NonInteractive");
        proceso.StartInfo.ArgumentList.Add("-Command");
        proceso.StartInfo.ArgumentList.Add("Get-ExecutionPolicy");

        try
        {
            if (!EjecutarProceso(proceso, out var salida, out _))
            {
                return "Consulta caducada";
            }

            var texto = salida.Trim();
            return string.IsNullOrWhiteSpace(texto) ? "No determinado" : texto;
        }
        catch
        {
            return "No determinado";
        }
    }

    public static string ObtenerRutaPowerShell()
    {
        var ruta = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        return File.Exists(ruta) ? ruta : "powershell.exe";
    }

    public static string NormalizarThumbprint(string thumbprint)
    {
        return string.Concat(thumbprint.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    }

    private static string CrearComandoFirma(string rutaArchivo)
    {
        var rutaEscapada = rutaArchivo.Replace("'", "''");
        return $$"""
$firma = Get-AuthenticodeSignature -LiteralPath '{{rutaEscapada}}'
$cert = $firma.SignerCertificate
[pscustomobject]@{
    status = [string]$firma.Status
    statusMessage = [string]$firma.StatusMessage
    thumbprint = if ($cert) { [string]$cert.Thumbprint } else { '' }
    subject = if ($cert) { [string]$cert.Subject } else { '' }
    issuer = if ($cert) { [string]$cert.Issuer } else { '' }
    notAfter = if ($cert) { $cert.NotAfter.ToString('o') } else { '' }
} | ConvertTo-Json -Compress
""";
    }

    private static bool EjecutarProceso(Process proceso, out string salida, out string error)
    {
        salida = string.Empty;
        error = string.Empty;

        proceso.Start();
        var salidaTask = proceso.StandardOutput.ReadToEndAsync();
        var errorTask = proceso.StandardError.ReadToEndAsync();

        if (!proceso.WaitForExit((int)TiempoMaximoConsulta.TotalMilliseconds))
        {
            try
            {
                proceso.Kill(entireProcessTree: true);
                proceso.WaitForExit();
            }
            catch
            {
            }

            salida = ObtenerResultadoSeguro(salidaTask);
            error = ObtenerResultadoSeguro(errorTask);
            return false;
        }

        salida = ObtenerResultadoSeguro(salidaTask);
        error = ObtenerResultadoSeguro(errorTask);
        return true;
    }

    private static string ObtenerResultadoSeguro(Task<string> tarea)
    {
        try
        {
            return tarea.GetAwaiter().GetResult();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string CrearComandoFirmas(IReadOnlyList<string> rutasArchivo)
    {
        var rutasBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rutasArchivo)));
        return $$"""
$json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{rutasBase64}}'))
$rutas = $json | ConvertFrom-Json
$resultado = foreach ($ruta in $rutas) {
    $firma = Get-AuthenticodeSignature -LiteralPath $ruta
    $cert = $firma.SignerCertificate
    [pscustomobject]@{
        path = [string]$ruta
        status = [string]$firma.Status
        statusMessage = [string]$firma.StatusMessage
        thumbprint = if ($cert) { [string]$cert.Thumbprint } else { '' }
        subject = if ($cert) { [string]$cert.Subject } else { '' }
        issuer = if ($cert) { [string]$cert.Issuer } else { '' }
        notAfter = if ($cert) { $cert.NotAfter.ToString('o') } else { '' }
    }
}
@($resultado) | ConvertTo-Json -Compress
""";
    }

    private static IReadOnlyDictionary<string, ResultadoFirmaAuthenticode> CrearResultadoFallo(
        IEnumerable<string> rutas,
        Dictionary<string, ResultadoFirmaAuthenticode> resultado,
        string mensaje)
    {
        foreach (var ruta in rutas.Where(ruta => !resultado.ContainsKey(ruta)))
        {
            resultado[ruta] = ResultadoFirmaAuthenticode.Fallo(mensaje);
        }

        return resultado;
    }

    private static IEnumerable<JsonObject> EnumerarObjetos(JsonNode? nodo)
    {
        if (nodo is JsonArray arreglo)
        {
            return arreglo.OfType<JsonObject>();
        }

        return nodo is JsonObject objeto ? [objeto] : [];
    }

    private static string LeerTexto(JsonObject nodo, string propiedad)
    {
        return nodo[propiedad]?.GetValue<string>() ?? string.Empty;
    }
}

public sealed record ResultadoFirmaAuthenticode(
    string Estado,
    string MensajeEstado,
    string Thumbprint,
    string Subject,
    string Issuer,
    string NotAfter,
    string Error)
{
    public bool ConsultaCorrecta => string.IsNullOrWhiteSpace(Error);

    public bool FirmaValida => ConsultaCorrecta && string.Equals(Estado, "Valid", StringComparison.OrdinalIgnoreCase);

    public static ResultadoFirmaAuthenticode Fallo(string mensaje)
    {
        return new ResultadoFirmaAuthenticode(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, mensaje);
    }
}
