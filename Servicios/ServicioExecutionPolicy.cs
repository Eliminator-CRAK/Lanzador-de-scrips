// (Autor: Alex Roman)
// Descripcion: Aplica la politica de ejecucion de PowerShell solicitada por el administrador.

using System.Diagnostics;
using System.IO;
using System.Text;

namespace LanzadorScripts.Servicios;

public sealed class ServicioExecutionPolicy
{
    private static readonly TimeSpan TiempoMaximo = TimeSpan.FromSeconds(45);

    public async Task<ResultadoExecutionPolicy> AplicarUnrestrictedAsync()
    {
        var rutaPowerShell = ServicioFirmaAuthenticode.ObtenerRutaPowerShell();
        if (!File.Exists(rutaPowerShell) && !string.Equals(rutaPowerShell, "powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            return new ResultadoExecutionPolicy(false, "PowerShell 5.1 no esta disponible.");
        }

        var comando = "Set-ExecutionPolicy -ExecutionPolicy Unrestricted -Force; Get-ExecutionPolicy -List | Out-String";
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
        proceso.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        proceso.StartInfo.ArgumentList.Add("Bypass");
        proceso.StartInfo.ArgumentList.Add("-Command");
        proceso.StartInfo.ArgumentList.Add(comando);

        try
        {
            proceso.Start();
            var salida = proceso.StandardOutput.ReadToEndAsync();
            var error = proceso.StandardError.ReadToEndAsync();
            using var cancelacion = new CancellationTokenSource(TiempoMaximo);
            await proceso.WaitForExitAsync(cancelacion.Token);

            var textoSalida = (await salida).Trim();
            var textoError = (await error).Trim();
            return proceso.ExitCode == 0
                ? new ResultadoExecutionPolicy(true, string.IsNullOrWhiteSpace(textoSalida) ? "ExecutionPolicy aplicada." : textoSalida)
                : new ResultadoExecutionPolicy(false, ServicioRedaccionSecretos.Sanitizar(string.IsNullOrWhiteSpace(textoError) ? textoSalida : textoError));
        }
        catch (OperationCanceledException)
        {
            try
            {
                proceso.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return new ResultadoExecutionPolicy(false, "La aplicacion de ExecutionPolicy supero el tiempo maximo.");
        }
        catch (Exception ex)
        {
            return new ResultadoExecutionPolicy(false, ServicioRedaccionSecretos.Sanitizar(ex.Message));
        }
    }
}

public sealed record ResultadoExecutionPolicy(bool Exito, string Mensaje);
