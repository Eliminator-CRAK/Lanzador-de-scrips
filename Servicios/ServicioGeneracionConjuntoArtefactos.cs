// (Autor: Alex Roman)
// Descripcion: Genera un conjunto distribuible de permisos y catalogo firmados.

using System.IO;
using System.Text;
using System.Text.Json;

namespace LanzadorScripts.Servicios;

public static class ServicioGeneracionConjuntoArtefactos
{
    public const string ArgumentoGenerar = "--generar-conjunto-artefactos";

    public static bool EsSolicitud(string[] argumentos)
    {
        return argumentos.Any(argumento =>
            string.Equals(argumento, ArgumentoGenerar, StringComparison.OrdinalIgnoreCase));
    }

    public static int Ejecutar(string[] argumentos)
    {
        try
        {
            var rutaScripts = DecodificarArgumento(argumentos, "--scripts-base64");
            var rutaSalida = DecodificarArgumento(argumentos, "--salida-base64");
            var administradores = DecodificarAdministradores(argumentos);
            var totalEsperado = DecodificarEntero(argumentos, "--total-esperado");
            var resultado = Generar(
                rutaScripts,
                rutaSalida,
                administradores,
                totalEsperado);
            Console.WriteLine(
                $"Conjunto firmado creado. ConjuntoId={resultado.ConjuntoId}; Scripts={resultado.TotalScripts}; Salida={resultado.RutaSalida}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ServicioRedaccionSecretos.Sanitizar(ex.Message));
            return 1;
        }
    }

    public static ResultadoGeneracionConjuntoArtefactos Generar(
        string rutaScripts,
        string rutaSalida,
        IEnumerable<string> administradores,
        int totalScriptsEsperado = 0)
    {
        var rutaScriptsValidada = ServicioRutasSeguras.ResolverCarpetaAbsoluta(
            rutaScripts,
            "scripts operativos");
        var rutaSalidaValidada = ServicioRutasSeguras.ResolverCarpetaAbsoluta(
            rutaSalida,
            "salida del conjunto firmado");
        ServicioGeneracionArtefactosIniciales.ValidarAdministradores(administradores);
        ValidarSalida(rutaScriptsValidada, rutaSalidaValidada);

        var scripts = new ServicioValidacionScripts().DescubrirScripts(rutaScriptsValidada);
        if (scripts.Count == 0)
        {
            throw new InvalidOperationException("No se encontraron scripts validos para el conjunto.");
        }

        if (totalScriptsEsperado > 0 && scripts.Count != totalScriptsEsperado)
        {
            throw new InvalidOperationException(
                $"Se esperaban {totalScriptsEsperado} scripts y se validaron {scripts.Count}.");
        }

        Directory.CreateDirectory(rutaSalidaValidada);
        try
        {
            var resultado = ServicioGeneracionArtefactosIniciales.Generar(
                rutaScriptsValidada,
                rutaSalidaValidada,
                administradores);
            ValidarArchivosFinales(rutaSalidaValidada);
            return resultado;
        }
        catch
        {
            EliminarArchivoSiExiste(
                Path.Combine(rutaSalidaValidada, RutasArtefactosProtegidos.NombrePermisos));
            EliminarArchivoSiExiste(
                Path.Combine(rutaSalidaValidada, RutasArtefactosProtegidos.NombreCatalogo));
            throw;
        }
    }

    private static void ValidarSalida(string rutaScripts, string rutaSalida)
    {
        if (ServicioRutasSeguras.EstaDentroDeCarpeta(rutaScripts, rutaSalida)
            || ServicioRutasSeguras.EstaDentroDeCarpeta(rutaSalida, rutaScripts))
        {
            throw new InvalidOperationException(
                "La carpeta de salida debe estar separada de la carpeta de scripts.");
        }

        if (Directory.Exists(rutaSalida)
            && Directory.EnumerateFileSystemEntries(rutaSalida).Any())
        {
            throw new InvalidOperationException("La carpeta de salida debe estar vacia.");
        }
    }

    private static void ValidarArchivosFinales(string rutaSalida)
    {
        var esperados = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RutasArtefactosProtegidos.NombrePermisos,
            RutasArtefactosProtegidos.NombreCatalogo
        };
        var encontrados = Directory.EnumerateFileSystemEntries(rutaSalida)
            .Select(Path.GetFileName)
            .Where(nombre => nombre is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!encontrados.SetEquals(esperados))
        {
            throw new InvalidOperationException(
                "La carpeta de salida no contiene exactamente los dos artefactos firmados requeridos.");
        }
    }

    private static void EliminarArchivoSiExiste(string ruta)
    {
        if (File.Exists(ruta))
        {
            File.Delete(ruta);
        }
    }

    private static IReadOnlyList<string> DecodificarAdministradores(string[] argumentos)
    {
        var indice = Array.FindIndex(
            argumentos,
            argumento => string.Equals(argumento, "--administradores-base64", StringComparison.OrdinalIgnoreCase));
        if (indice < 0)
        {
            return ServicioGeneracionArtefactosIniciales.AdministradoresPredeterminados;
        }

        if (indice + 1 >= argumentos.Length)
        {
            throw new InvalidOperationException("Falta el valor requerido para --administradores-base64.");
        }

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(argumentos[indice + 1]));
        return JsonSerializer.Deserialize<string[]>(json)
            ?? throw new InvalidOperationException("La lista de administradores no es valida.");
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

    private static int DecodificarEntero(string[] argumentos, string nombre)
    {
        var indice = Array.FindIndex(
            argumentos,
            argumento => string.Equals(argumento, nombre, StringComparison.OrdinalIgnoreCase));
        if (indice < 0)
        {
            return 0;
        }

        if (indice + 1 >= argumentos.Length
            || !int.TryParse(argumentos[indice + 1], out var valor)
            || valor < 0
            || valor > 10000)
        {
            throw new InvalidOperationException($"El argumento {nombre} no es valido.");
        }

        return valor;
    }
}

public sealed record ResultadoGeneracionConjuntoArtefactos(
    string ConjuntoId,
    int TotalScripts,
    string RutaSalida);
