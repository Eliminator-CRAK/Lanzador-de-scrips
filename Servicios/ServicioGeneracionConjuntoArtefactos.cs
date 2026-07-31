// (Autor: Alex Roman)
// Descripcion: Genera permisos, catalogo y paquete DPAPI-NG con una unica clave AES en memoria.

using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LanzadorScripts.Servicios;

public static class ServicioGeneracionConjuntoArtefactos
{
    public const string ArgumentoGenerar = "--generar-conjunto-artefactos";

    public static bool EsSolicitud(string[] argumentos)
    {
        // Detecta el modo de generacion operativa sin iniciar la interfaz.
        return argumentos.Any(argumento =>
            string.Equals(argumento, ArgumentoGenerar, StringComparison.OrdinalIgnoreCase));
    }

    public static int Ejecutar(string[] argumentos)
    {
        try
        {
            // Decodifica solo metadatos; la clave AES nunca entra en la linea de comandos.
            var rutaScripts = DecodificarArgumento(argumentos, "--scripts-base64");
            var rutaSalida = DecodificarArgumento(argumentos, "--salida-base64");
            var administrador = DecodificarArgumento(argumentos, "--administrador-base64");
            var descriptor = DecodificarArgumento(argumentos, "--descriptor-base64");
            var totalEsperado = DecodificarEntero(argumentos, "--total-esperado");
            var resultado = Generar(
                rutaScripts,
                rutaSalida,
                administrador,
                descriptor,
                totalEsperado);
            Console.WriteLine(
                $"Conjunto creado. KeyId={resultado.KeyId}; Scripts={resultado.TotalScripts}; Salida={resultado.RutaSalida}");
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
        string administrador,
        string descriptorDpapiNg,
        int totalScriptsEsperado = 0)
    {
        // Usa el certificado operativo y DPAPI-NG real para la generacion final.
        return Generar(
            rutaScripts,
            rutaSalida,
            administrador,
            descriptorDpapiNg,
            totalScriptsEsperado,
            new ServicioFirmaArtefactos(),
            new ServicioDpapiNg());
    }

    internal static ResultadoGeneracionConjuntoArtefactos Generar(
        string rutaScripts,
        string rutaSalida,
        string administrador,
        string descriptorDpapiNg,
        int totalScriptsEsperado,
        ServicioFirmaArtefactos firma,
        IProtectorDpapiNg dpapiNg)
    {
        // Valida todas las entradas antes de generar material criptografico.
        var rutaScriptsValidada = ServicioRutasSeguras.ResolverCarpetaAbsoluta(
            rutaScripts,
            "carpeta de scripts");
        var rutaSalidaValidada = ServicioRutasSeguras.ResolverCarpetaAbsoluta(
            rutaSalida,
            "carpeta de salida del conjunto");
        var administradorValidado = ServicioGeneracionArtefactosIniciales.ValidarAdministrador(
            administrador);
        ServicioDpapiNg.ValidarDescriptor(descriptorDpapiNg);
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
        var clave = RandomNumberGenerator.GetBytes(32);
        try
        {
            // Cifra y firma los tres archivos con la misma clave e identidad RSA.
            using var artefactos = new ServicioArtefactosProtegidos(clave, firma);
            ServicioGeneracionArtefactosIniciales.Generar(
                rutaScriptsValidada,
                rutaSalidaValidada,
                artefactos,
                administradorValidado);
            var claveLocalNoUsada = new ServicioClaveArtefactos(
                Path.Combine(rutaSalidaValidada, ".clave-local-no-usada"));
            var aprovisionamiento = new ServicioAprovisionamientoClaveArtefactos(
                claveLocalNoUsada,
                firma,
                artefactos,
                dpapiNg,
                aplicarAcl: false);
            var keyId = aprovisionamiento.CrearPaquete(
                rutaSalidaValidada,
                descriptorDpapiNg,
                clave);
            ValidarArchivosFinales(rutaSalidaValidada);
            return new ResultadoGeneracionConjuntoArtefactos(
                keyId,
                scripts.Count,
                rutaSalidaValidada);
        }
        catch
        {
            // Impide distribuir un conjunto parcial cuando falla cualquier paso.
            EliminarArchivoSiExiste(
                Path.Combine(rutaSalidaValidada, RutasArtefactosProtegidos.NombrePermisos));
            EliminarArchivoSiExiste(
                Path.Combine(rutaSalidaValidada, RutasArtefactosProtegidos.NombreCatalogo));
            EliminarArchivoSiExiste(
                Path.Combine(rutaSalidaValidada, ServicioAprovisionamientoClaveArtefactos.NombrePaquete));
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clave);
        }
    }

    private static void ValidarSalida(string rutaScripts, string rutaSalida)
    {
        // Evita mezclar scripts operativos con artefactos o sobrescribir una salida previa.
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
        // Exige exactamente los tres archivos que se van a distribuir.
        var esperados = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RutasArtefactosProtegidos.NombrePermisos,
            RutasArtefactosProtegidos.NombreCatalogo,
            ServicioAprovisionamientoClaveArtefactos.NombrePaquete
        };
        var encontrados = Directory.EnumerateFiles(rutaSalida)
            .Select(Path.GetFileName)
            .Where(nombre => nombre is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!encontrados.SetEquals(esperados))
        {
            throw new InvalidOperationException(
                "La carpeta de salida no contiene exactamente los tres artefactos requeridos.");
        }
    }

    private static void EliminarArchivoSiExiste(string ruta)
    {
        // Elimina solo nombres fijos creados por esta operacion.
        if (File.Exists(ruta))
        {
            File.Delete(ruta);
        }
    }

    private static string DecodificarArgumento(string[] argumentos, string nombre)
    {
        // Recupera un argumento UTF-8 sin exponer rutas con espacios al analizador.
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
        // Valida el recuento esperado para detectar scripts omitidos.
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
    string KeyId,
    int TotalScripts,
    string RutaSalida);
