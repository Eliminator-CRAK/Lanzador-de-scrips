// (Autor: Alex Roman)
// Descripcion: Genera un catalogo seguro desde la carpeta local de scripts del servidor.

using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace LanzadorScripts.Servidor.Core;

public sealed class GeneradorCatalogoServidor
{
    private const int MaximoScripts = 10_000;
    private const long LongitudMaximaScript = 512L * 1024 * 1024;
    private static readonly HashSet<string> ExtensionesPermitidas =
        new(StringComparer.OrdinalIgnoreCase) { ".ps1", ".bat", ".cmd" };

    public JsonObject Generar(string rutaRaiz, string conjuntoId)
    {
        var raiz = ResolverRaiz(rutaRaiz);
        if (string.IsNullOrWhiteSpace(conjuntoId) || conjuntoId.Length > 128)
        {
            throw new InvalidDataException("El identificador del conjunto no es valido.");
        }

        var entradas = new List<JsonObject>();
        var pendientes = new Queue<string>();
        pendientes.Enqueue(raiz);
        while (pendientes.Count > 0)
        {
            var carpeta = pendientes.Dequeue();
            RechazarPuntoReanalisis(carpeta);
            foreach (var subcarpeta in Directory.EnumerateDirectories(carpeta))
            {
                RechazarPuntoReanalisis(subcarpeta);
                pendientes.Enqueue(subcarpeta);
            }

            foreach (var archivo in Directory.EnumerateFiles(carpeta))
            {
                var extension = Path.GetExtension(archivo);
                if (!ExtensionesPermitidas.Contains(extension))
                {
                    continue;
                }

                RechazarPuntoReanalisis(archivo);
                entradas.Add(CrearEntrada(raiz, archivo, extension));
                if (entradas.Count > MaximoScripts)
                {
                    throw new InvalidDataException(
                        $"La carpeta supera el limite de {MaximoScripts} scripts.");
                }
            }
        }

        return new JsonObject
        {
            ["version"] = 1,
            ["generadoUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["conjuntoId"] = conjuntoId,
            ["scripts"] = new JsonArray(entradas
                .OrderBy(entrada => entrada["scriptId"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
                .Select(entrada => (JsonNode?)entrada)
                .ToArray())
        };
    }

    private static JsonObject CrearEntrada(string raiz, string ruta, string extension)
    {
        var completa = Path.GetFullPath(ruta);
        var relativa = Path.GetRelativePath(raiz, completa).Replace('\\', '/');
        if (Path.IsPathRooted(relativa)
            || relativa.Split('/').Any(segmento => segmento.Length == 0 || segmento is "." or ".."))
        {
            throw new InvalidDataException("Se detecto una ruta de script fuera de la carpeta seleccionada.");
        }

        var antes = new FileInfo(completa);
        if (antes.Length is < 0 or > LongitudMaximaScript)
        {
            throw new InvalidDataException($"El script supera el tamano permitido: {relativa}");
        }

        string hash;
        using (var flujo = new FileStream(
            completa,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan))
        {
            hash = Convert.ToHexString(SHA256.HashData(flujo));
        }

        var despues = new FileInfo(completa);
        if (antes.Length != despues.Length || antes.LastWriteTimeUtc != despues.LastWriteTimeUtc)
        {
            throw new IOException($"El script cambio mientras se calculaba su hash: {relativa}");
        }

        return new JsonObject
        {
            ["scriptId"] = relativa,
            ["extension"] = extension.ToLowerInvariant(),
            ["longitud"] = despues.Length,
            ["sha256"] = hash
        };
    }

    private static string ResolverRaiz(string rutaRaiz)
    {
        if (string.IsNullOrWhiteSpace(rutaRaiz)
            || rutaRaiz.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException("La carpeta de scripts no es valida.");
        }

        var completa = Path.GetFullPath(rutaRaiz.Trim());
        if (!Directory.Exists(completa))
        {
            throw new DirectoryNotFoundException("No existe la carpeta de scripts seleccionada.");
        }

        RechazarPuntoReanalisis(completa);
        return Path.TrimEndingDirectorySeparator(completa) + Path.DirectorySeparatorChar;
    }

    private static void RechazarPuntoReanalisis(string ruta)
    {
        if ((File.GetAttributes(ruta) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("La carpeta de scripts no puede contener enlaces ni puntos de reanalisis.");
        }
    }
}
