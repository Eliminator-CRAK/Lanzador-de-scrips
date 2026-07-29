// (Autor: Alex Roman)
// Descripcion: Genera el paquete central de aprovisionamiento desde la clave local autorizada.

using System.IO;
using System.Text;

namespace LanzadorScripts.Servicios;

public static class ServicioGeneracionPaqueteClaveArtefactos
{
    public const string ArgumentoGenerar = "--generar-paquete-clave-artefactos";

    public static bool EsSolicitud(string[] argumentos)
    {
        return argumentos.Any(argumento =>
            string.Equals(argumento, ArgumentoGenerar, StringComparison.OrdinalIgnoreCase));
    }

    public static int Ejecutar(string[] argumentos)
    {
        try
        {
            var descriptor = DecodificarArgumento(argumentos, "--descriptor-base64");
            var rutaPermisos = DecodificarArgumento(argumentos, "--permisos-base64");
            new ServicioAprovisionamientoClaveArtefactos().CrearPaquete(
                rutaPermisos,
                descriptor);
            Console.WriteLine(
                $"Paquete creado: {Path.Combine(rutaPermisos, ServicioAprovisionamientoClaveArtefactos.NombrePaquete)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ServicioRedaccionSecretos.Sanitizar(ex.Message));
            return 1;
        }
    }

    private static string DecodificarArgumento(string[] argumentos, string nombre)
    {
        var indice = Array.FindIndex(
            argumentos,
            argumento => string.Equals(argumento, nombre, StringComparison.OrdinalIgnoreCase));
        if (indice < 0 || indice + 1 >= argumentos.Length)
        {
            throw new InvalidOperationException($"Falta el argumento requerido {nombre}.");
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(argumentos[indice + 1]));
    }
}
