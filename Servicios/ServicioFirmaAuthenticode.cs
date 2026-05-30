// (Autor: Alex Roman)
// Descripcion: Obtiene informacion de firma Authenticode usando PowerShell del sistema.

using System.Diagnostics;
using System.IO;
using System.Text;
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
            proceso.Start();
            if (!proceso.WaitForExit((int)TiempoMaximoConsulta.TotalMilliseconds))
            {
                proceso.Kill(entireProcessTree: true);
                return ResultadoFirmaAuthenticode.Fallo("La consulta de firma supero el tiempo maximo.");
            }

            var salida = proceso.StandardOutput.ReadToEnd();
            var error = proceso.StandardError.ReadToEnd();
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
        proceso.StartInfo.ArgumentList.Add("Get-ExecutionPolicy -Scope Process");

        try
        {
            proceso.Start();
            if (!proceso.WaitForExit((int)TiempoMaximoConsulta.TotalMilliseconds))
            {
                proceso.Kill(entireProcessTree: true);
                return "Consulta caducada";
            }

            var salida = proceso.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrWhiteSpace(salida) ? "No determinado" : salida;
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
