// (Autor: Alex Roman)
// Descripcion: Comprueba e instala Microsoft Edge WebView2 Runtime.

using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Web.WebView2.Core;

namespace LanzadorScripts.Servicios;

public sealed class ServicioInstalacionWebView2
{
    private const string NombreInstalador = "MicrosoftEdgeWebView2RuntimeInstallerX64.exe";
    private const string NombreRecursoInstalador = "WebView2.MicrosoftEdgeWebView2RuntimeInstallerX64.exe";
    private static readonly TimeSpan TiempoMaximoInstalacion = TimeSpan.FromMinutes(15);

    public async Task<ResultadoInstalacionWebView2> AsegurarInstaladoAsync(string? rutaRuntimeFijo)
    {
        if (RuntimeDisponible(rutaRuntimeFijo) || RuntimeDisponible(null))
        {
            return ResultadoInstalacionWebView2.Correcto();
        }

        await using var recurso = AbrirInstaladorEmbebido();
        if (recurso is null)
        {
            return ResultadoInstalacionWebView2.Error("No se encontro el instalador embebido de WebView2 Runtime.");
        }

        var rutaTemporal = await ExtraerInstaladorAsync(recurso);
        try
        {
            var resultado = await EjecutarInstaladorAsync(rutaTemporal);
            if (!resultado.Exito)
            {
                return resultado;
            }

            return RuntimeDisponible(null)
                ? ResultadoInstalacionWebView2.Correcto()
                : ResultadoInstalacionWebView2.Error("WebView2 Runtime no quedo disponible despues de instalarlo. Reinicia el equipo y vuelve a abrir la aplicacion.");
        }
        finally
        {
            BorrarTemporal(rutaTemporal);
        }
    }

    public string? ObtenerVersionDisponible(string? rutaRuntimeFijo)
    {
        return ObtenerVersion(rutaRuntimeFijo) ?? ObtenerVersion(null);
    }

    public string? ObtenerVersionRuta(string? rutaRuntime)
    {
        return ObtenerVersion(rutaRuntime);
    }

    private static bool RuntimeDisponible(string? rutaRuntimeFijo)
    {
        return !string.IsNullOrWhiteSpace(ObtenerVersion(rutaRuntimeFijo));
    }

    private static string? ObtenerVersion(string? rutaRuntimeFijo)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(rutaRuntimeFijo) && !Directory.Exists(rutaRuntimeFijo))
            {
                return null;
            }

            var version = CoreWebView2Environment.GetAvailableBrowserVersionString(rutaRuntimeFijo);
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch
        {
            return null;
        }
    }

    private static Stream? AbrirInstaladorEmbebido()
    {
        var ensamblado = Assembly.GetExecutingAssembly();
        return ensamblado.GetManifestResourceStream(NombreRecursoInstalador)
            ?? ensamblado.GetManifestResourceNames()
                .Where(nombre => nombre.EndsWith(NombreInstalador, StringComparison.OrdinalIgnoreCase))
                .Select(ensamblado.GetManifestResourceStream)
                .FirstOrDefault(flujo => flujo is not null);
    }

    private static async Task<string> ExtraerInstaladorAsync(Stream recurso)
    {
        var carpetaTemporal = Path.Combine(Path.GetTempPath(), "LanzadorScripts", "WebView2");
        Directory.CreateDirectory(carpetaTemporal);

        var rutaTemporal = Path.Combine(carpetaTemporal, NombreInstalador);
        await using var destino = new FileStream(rutaTemporal, FileMode.Create, FileAccess.Write, FileShare.None);
        await recurso.CopyToAsync(destino);
        return rutaTemporal;
    }

    private static async Task<ResultadoInstalacionWebView2> EjecutarInstaladorAsync(string rutaInstalador)
    {
        using var proceso = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = rutaInstalador,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(rutaInstalador) ?? Path.GetTempPath()
            }
        };

        proceso.StartInfo.ArgumentList.Add("/silent");
        proceso.StartInfo.ArgumentList.Add("/install");

        try
        {
            proceso.Start();
        }
        catch (Exception ex)
        {
            return ResultadoInstalacionWebView2.Error($"No se pudo iniciar el instalador de WebView2 Runtime: {ex.Message}");
        }

        using var cancelacion = new CancellationTokenSource(TiempoMaximoInstalacion);
        try
        {
            await proceso.WaitForExitAsync(cancelacion.Token);
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

            return ResultadoInstalacionWebView2.Error("La instalacion de WebView2 Runtime supero el tiempo maximo permitido.");
        }

        return proceso.ExitCode is 0 or 3010
            ? ResultadoInstalacionWebView2.Correcto()
            : ResultadoInstalacionWebView2.Error($"El instalador de WebView2 Runtime termino con codigo {proceso.ExitCode}.");
    }

    private static void BorrarTemporal(string rutaTemporal)
    {
        try
        {
            File.Delete(rutaTemporal);
        }
        catch
        {
        }
    }
}

public sealed record ResultadoInstalacionWebView2(bool Exito, string Mensaje)
{
    public static ResultadoInstalacionWebView2 Correcto()
    {
        return new ResultadoInstalacionWebView2(true, string.Empty);
    }

    public static ResultadoInstalacionWebView2 Error(string mensaje)
    {
        return new ResultadoInstalacionWebView2(false, mensaje);
    }
}
