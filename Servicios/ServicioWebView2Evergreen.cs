// (Autor: Alex Roman)
// Descripcion: Reutiliza WebView2 compartido o instala el paquete oficial sin conexion verificado.

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace LanzadorScripts.Servicios;

internal static class ServicioWebView2Evergreen
{
    internal const string HashInstalador = "771042DB15CB5C463BAC51A8408E70183D7130E8AC946709384C2223DA582C1B";
    internal const string NombreInstalador = "MicrosoftEdgeWebView2RuntimeInstallerX64.exe";
    internal const string VersionMinima = "150.0.4078.48";

    public static async Task AsegurarAsync()
    {
        var disponibilidad = new ServicioDisponibilidadWebView2();
        var existente = disponibilidad.Comprobar();
        if (existente.Exito && ServicioArranqueWebView2.EsVersionSistemaCompatible(existente.Version)) return;

        var portable = RutasAplicacion.Distribucion.EsPortable;
        var carpeta = portable
            ? Path.Combine(RutasAplicacion.Distribucion.RaizEjecucionPortable!, "Prerrequisitos")
            : Path.Combine(AppContext.BaseDirectory, "Prerrequisitos");
        ServicioDirectoriosAplicacion.RechazarPuntosReanalisis(carpeta);
        var instalador = Path.Combine(carpeta, NombreInstalador);
        try
        {
            ServicioDirectoriosAplicacion.RechazarPuntosReanalisis(instalador);
            if (portable)
            {
                Directory.CreateDirectory(carpeta);
                await using var recurso = ServicioRecursoWebView2Portable.Abrir()
                    ?? throw new InvalidDataException("La portable no contiene el instalador oficial de WebView2.");
                await using var salida = new FileStream(instalador, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await recurso.CopyToAsync(salida);
                await salida.FlushAsync();
            }
            // El hash fijado corresponde a un instalador cuya firma Microsoft se verifica al publicar.
            using var bloqueo = new FileStream(instalador, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(bloqueo)), HashInstalador, StringComparison.Ordinal))
                throw new InvalidDataException("El instalador de WebView2 no coincide con el paquete oficial autorizado.");
            var inicio = new ProcessStartInfo(instalador)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = carpeta
            };
            inicio.ArgumentList.Add("/silent");
            inicio.ArgumentList.Add("/install");
            using var proceso = Process.Start(inicio)
                ?? throw new IOException("Windows no pudo iniciar el instalador de WebView2.");
            using var limite = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try { await proceso.WaitForExitAsync(limite.Token); }
            catch (OperationCanceledException)
            {
                // No interrumpe una instalacion del sistema que puede seguir completandose.
                throw new IOException("WebView2 continua instalando despues de diez minutos. Espere y vuelva a abrir LanzadorScripts.");
            }
            var resultado = disponibilidad.Comprobar();
            if (!resultado.Exito || !ServicioArranqueWebView2.EsVersionSistemaCompatible(resultado.Version))
                throw new IOException($"WebView2 no quedo disponible. Codigo del instalador: {proceso.ExitCode}. Revise las politicas del equipo.");
        }
        finally
        {
            if (portable)
            {
                try { if (File.Exists(instalador)) File.Delete(instalador); }
                catch (IOException) { /* El lanzador nativo recupera el resto al finalizar. */ }
            }
        }
    }
}
