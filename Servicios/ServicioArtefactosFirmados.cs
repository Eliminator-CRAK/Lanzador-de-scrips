// (Autor: Alex Roman)
// Descripcion: Firma, valida y guarda los artefactos compartidos de la aplicacion.

using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanzadorScripts.Servicios;

public sealed class ServicioArtefactosFirmados
{
    public const string TipoPermisos = "permissions";
    public const string TipoCatalogoScripts = "script-catalog";
    public const int VersionActual = 3;
    public const string AlgoritmoActual = "RSA-PSS-SHA256";

    private const string AutorContenedor = "Alex Roman";
    private const string DescripcionContenedor = "Artefacto firmado de LanzadorScripts.";
    private const int LongitudConjuntoId = 32;
    private const int LongitudMaximaContenido = 16 * 1024 * 1024;
    private const int LongitudMaximaArchivo = 24 * 1024 * 1024;
    private const int LongitudMaximaFirma = 16 * 1024;
    private const int ProfundidadMaximaJson = 64;

    private static readonly UTF8Encoding Utf8Estricto = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonDocumentOptions OpcionesDocumento = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = ProfundidadMaximaJson
    };

    private static readonly JsonWriterOptions OpcionesEscritura = new()
    {
        Indented = true,
        SkipValidation = false
    };

    private static readonly HashSet<string> PropiedadesContenedor = new(StringComparer.Ordinal)
    {
        "Autor",
        "Descripcion",
        "Version",
        "Tipo",
        "Algoritmo",
        "ConjuntoId",
        "Contenido",
        "Firma"
    };

    private readonly ServicioFirmaArtefactos _servicioFirma;

    public ServicioArtefactosFirmados()
        : this(new ServicioFirmaArtefactos())
    {
    }

    internal ServicioArtefactosFirmados(RSA claveFirma, RSA claveVerificacion)
        : this(new ServicioFirmaArtefactos(claveFirma, claveVerificacion))
    {
    }

    internal ServicioArtefactosFirmados(ServicioFirmaArtefactos servicioFirma)
    {
        _servicioFirma = servicioFirma;
    }

    public static string CrearConjuntoId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(LongitudConjuntoId / 2));
    }

    public string FirmarTexto(string tipo, string texto, string conjuntoId)
    {
        ValidarTipo(tipo);
        ValidarConjuntoId(conjuntoId);
        var contenido = PrepararContenido(texto);
        try
        {
            var bytesFirmados = ObtenerBytesFirma(tipo, conjuntoId, contenido);
            var firma = _servicioFirma.Firmar(bytesFirmados);
            try
            {
                return CrearContenedor(tipo, conjuntoId, contenido, firma);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(firma);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contenido);
        }
    }

    public bool IntentarValidarTexto(
        string tipo,
        string texto,
        out string contenido,
        out string conjuntoId,
        out string error)
    {
        contenido = string.Empty;
        conjuntoId = string.Empty;
        error = string.Empty;

        try
        {
            ValidarTipo(tipo);
            if (string.IsNullOrWhiteSpace(texto)
                || Utf8Estricto.GetByteCount(texto) > LongitudMaximaArchivo)
            {
                error = "El artefacto firmado tiene un tamano no valido.";
                return false;
            }

            using var documento = JsonDocument.Parse(texto, OpcionesDocumento);
            if (documento.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "El artefacto firmado no contiene un objeto JSON.";
                return false;
            }

            if (EsContenedorAesAnterior(documento.RootElement))
            {
                error = "El artefacto usa el formato AES v1/v2 obsoleto. Sustituya permisos y catalogo por el conjunto firmado v3.";
                return false;
            }

            if (!ValidarPropiedadesUnicas(documento.RootElement, PropiedadesContenedor, out error))
            {
                return false;
            }

            var autor = LeerTexto(documento.RootElement, "Autor");
            var descripcion = LeerTexto(documento.RootElement, "Descripcion");
            var tipoContenedor = LeerTexto(documento.RootElement, "Tipo");
            var algoritmo = LeerTexto(documento.RootElement, "Algoritmo");
            var conjunto = LeerTexto(documento.RootElement, "ConjuntoId");
            var firmaBase64 = LeerTexto(documento.RootElement, "Firma");
            if (!documento.RootElement.TryGetProperty("Version", out var version)
                || !version.TryGetInt32(out var numeroVersion)
                || !documento.RootElement.TryGetProperty("Contenido", out var contenidoElemento)
                || contenidoElemento.ValueKind != JsonValueKind.Object
                || !string.Equals(autor, AutorContenedor, StringComparison.Ordinal)
                || !string.Equals(descripcion, DescripcionContenedor, StringComparison.Ordinal)
                || numeroVersion != VersionActual
                || !string.Equals(tipoContenedor, tipo, StringComparison.Ordinal)
                || !string.Equals(algoritmo, AlgoritmoActual, StringComparison.Ordinal)
                || !EsConjuntoIdValido(conjunto))
            {
                error = "El artefacto firmado no tiene el tipo, version o metadatos esperados.";
                return false;
            }

            if (!ValidarObjetosSinDuplicados(contenidoElemento, out error))
            {
                return false;
            }

            var contenidoBytes = Utf8Estricto.GetBytes(contenidoElemento.GetRawText());
            if (contenidoBytes.Length == 0 || contenidoBytes.Length > LongitudMaximaContenido)
            {
                CryptographicOperations.ZeroMemory(contenidoBytes);
                error = "El contenido del artefacto firmado tiene un tamano no valido.";
                return false;
            }

            byte[] firma;
            try
            {
                firma = Convert.FromBase64String(firmaBase64);
            }
            catch (FormatException)
            {
                CryptographicOperations.ZeroMemory(contenidoBytes);
                error = "La firma del artefacto no tiene un formato Base64 valido.";
                return false;
            }

            try
            {
                if (firma.Length == 0 || firma.Length > LongitudMaximaFirma)
                {
                    error = "La firma del artefacto tiene una longitud no valida.";
                    return false;
                }

                var bytesFirmados = ObtenerBytesFirma(tipo, conjunto, contenidoBytes);
                if (!_servicioFirma.Verificar(bytesFirmados, firma))
                {
                    error = "La firma del artefacto no es valida.";
                    return false;
                }

                contenido = Utf8Estricto.GetString(contenidoBytes);
                conjuntoId = conjunto;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(contenidoBytes);
                CryptographicOperations.ZeroMemory(firma);
            }
        }
        catch (Exception ex) when (ex is JsonException
            or DecoderFallbackException
            or EncoderFallbackException
            or CryptographicException
            or ArgumentException
            or InvalidOperationException)
        {
            contenido = string.Empty;
            conjuntoId = string.Empty;
            error = "El artefacto firmado esta corrupto o fue modificado.";
            return false;
        }
    }

    public bool IntentarObtenerConjuntoIdFirmado(
        string tipo,
        string texto,
        out string conjuntoId,
        out string error)
    {
        return IntentarValidarTexto(tipo, texto, out _, out conjuntoId, out error);
    }

    public void GuardarTextoFirmado(
        string ruta,
        string tipo,
        string texto,
        string conjuntoId)
    {
        GuardarTextoAtomico(ruta, FirmarTexto(tipo, texto, conjuntoId));
    }

    public bool IntentarCargarTextoFirmado(
        string ruta,
        string tipo,
        out string contenido,
        out string conjuntoId,
        out string error,
        out bool recuperado)
    {
        recuperado = false;
        if (IntentarCargarDesdeRuta(ruta, tipo, out contenido, out conjuntoId, out error))
        {
            return true;
        }

        var errorPrincipal = error;
        if (IntentarCargarDesdeRuta(ruta + ".bak", tipo, out contenido, out conjuntoId, out _))
        {
            recuperado = true;
            error = string.Empty;
            return true;
        }

        contenido = string.Empty;
        conjuntoId = string.Empty;
        error = errorPrincipal;
        return false;
    }

    public static void GuardarTextoAtomico(string ruta, string contenido)
    {
        var rutaValidada = ServicioRutasSeguras.ResolverArchivoAbsoluto(
            ruta,
            "artefacto firmado",
            ".json");
        RechazarEnlacesSistema(rutaValidada);
        var carpeta = Path.GetDirectoryName(rutaValidada)
            ?? throw new InvalidOperationException("No se pudo resolver la carpeta del archivo firmado.");
        Directory.CreateDirectory(carpeta);
        RechazarEnlacesSistema(rutaValidada);

        var temporal = Path.Combine(carpeta, $".{Path.GetFileName(rutaValidada)}.{Guid.NewGuid():N}.tmp");
        var respaldo = rutaValidada + ".bak";
        try
        {
            using (var flujo = new FileStream(
                temporal,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            using (var escritor = new StreamWriter(flujo, Utf8Estricto))
            {
                escritor.Write(contenido);
                escritor.Flush();
                flujo.Flush(flushToDisk: true);
            }

            if (File.Exists(rutaValidada))
            {
                File.Replace(temporal, rutaValidada, respaldo, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporal, rutaValidada);
            }
        }
        finally
        {
            if (File.Exists(temporal))
            {
                File.Delete(temporal);
            }
        }
    }

    public static void ValidarConjuntoId(string conjuntoId)
    {
        if (!EsConjuntoIdValido(conjuntoId))
        {
            throw new ArgumentException(
                "El identificador del conjunto debe contener 32 caracteres hexadecimales en mayusculas.",
                nameof(conjuntoId));
        }
    }

    private static string CrearContenedor(
        string tipo,
        string conjuntoId,
        ReadOnlySpan<byte> contenido,
        ReadOnlySpan<byte> firma)
    {
        using var flujo = new MemoryStream();
        using (var escritor = new Utf8JsonWriter(flujo, OpcionesEscritura))
        {
            escritor.WriteStartObject();
            escritor.WriteString("Autor", AutorContenedor);
            escritor.WriteString("Descripcion", DescripcionContenedor);
            escritor.WriteNumber("Version", VersionActual);
            escritor.WriteString("Tipo", tipo);
            escritor.WriteString("Algoritmo", AlgoritmoActual);
            escritor.WriteString("ConjuntoId", conjuntoId);
            escritor.WritePropertyName("Contenido");
            escritor.WriteRawValue(contenido, skipInputValidation: false);
            escritor.WriteBase64String("Firma", firma);
            escritor.WriteEndObject();
        }

        return Utf8Estricto.GetString(flujo.ToArray());
    }

    private static byte[] PrepararContenido(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            throw new ArgumentException("El contenido que se va a firmar no puede estar vacio.", nameof(texto));
        }

        using var documento = JsonDocument.Parse(texto, OpcionesDocumento);
        if (documento.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("El contenido que se va a firmar debe ser un objeto JSON.", nameof(texto));
        }

        if (!ValidarObjetosSinDuplicados(documento.RootElement, out var error))
        {
            throw new ArgumentException(error, nameof(texto));
        }

        var contenido = Utf8Estricto.GetBytes(documento.RootElement.GetRawText());
        if (contenido.Length == 0 || contenido.Length > LongitudMaximaContenido)
        {
            CryptographicOperations.ZeroMemory(contenido);
            throw new ArgumentException("El contenido que se va a firmar tiene un tamano no valido.", nameof(texto));
        }

        return contenido;
    }

    private static byte[] ObtenerBytesFirma(
        string tipo,
        string conjuntoId,
        ReadOnlySpan<byte> contenido)
    {
        using var flujo = new MemoryStream();
        EscribirCampo(flujo, Utf8Estricto.GetBytes("LanzadorScripts.ArtefactoFirmado"));
        Span<byte> version = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(version, VersionActual);
        EscribirCampo(flujo, version);
        EscribirCampo(flujo, Utf8Estricto.GetBytes(AutorContenedor));
        EscribirCampo(flujo, Utf8Estricto.GetBytes(DescripcionContenedor));
        EscribirCampo(flujo, Utf8Estricto.GetBytes(tipo));
        EscribirCampo(flujo, Utf8Estricto.GetBytes(AlgoritmoActual));
        EscribirCampo(flujo, Utf8Estricto.GetBytes(conjuntoId));
        EscribirCampo(flujo, contenido);
        return flujo.ToArray();
    }

    private static void EscribirCampo(Stream destino, ReadOnlySpan<byte> campo)
    {
        Span<byte> longitud = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(longitud, campo.Length);
        destino.Write(longitud);
        destino.Write(campo);
    }

    private bool IntentarCargarDesdeRuta(
        string ruta,
        string tipo,
        out string contenido,
        out string conjuntoId,
        out string error)
    {
        contenido = string.Empty;
        conjuntoId = string.Empty;
        RutaArchivoProtegidoValidada rutaSegura;
        try
        {
            rutaSegura = ServicioRutasSeguras.ResolverArchivoProtegido(ruta);
        }
        catch
        {
            error = "La ruta del archivo firmado no es segura.";
            return false;
        }

        if (!File.Exists(rutaSegura.RutaCompleta))
        {
            error = "No se encontro el archivo firmado.";
            return false;
        }

        try
        {
            RechazarEnlacesSistema(rutaSegura.RutaCompleta);
            using var flujo = rutaSegura.AbrirLectura();
            if (flujo.Length <= 0 || flujo.Length > LongitudMaximaArchivo)
            {
                error = "El archivo firmado tiene un tamano no valido.";
                return false;
            }

            using var lector = new StreamReader(
                flujo,
                Utf8Estricto,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: false);
            return IntentarValidarTexto(tipo, lector.ReadToEnd(), out contenido, out conjuntoId, out error);
        }
        catch (IOException)
        {
            error = "No se pudo leer el archivo firmado.";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = "No se pudo acceder al archivo firmado.";
            return false;
        }
        catch (DecoderFallbackException)
        {
            error = "El archivo firmado no contiene UTF-8 valido.";
            return false;
        }
    }

    private static bool ValidarPropiedadesUnicas(
        JsonElement objeto,
        IReadOnlySet<string> esperadas,
        out string error)
    {
        var encontradas = new HashSet<string>(StringComparer.Ordinal);
        foreach (var propiedad in objeto.EnumerateObject())
        {
            if (!esperadas.Contains(propiedad.Name) || !encontradas.Add(propiedad.Name))
            {
                error = "El artefacto firmado contiene propiedades desconocidas o duplicadas.";
                return false;
            }
        }

        if (!encontradas.SetEquals(esperadas))
        {
            error = "El artefacto firmado no contiene todas las propiedades requeridas.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidarObjetosSinDuplicados(JsonElement elemento, out string error)
    {
        if (elemento.ValueKind == JsonValueKind.Object)
        {
            var propiedades = new HashSet<string>(StringComparer.Ordinal);
            foreach (var propiedad in elemento.EnumerateObject())
            {
                if (!propiedades.Add(propiedad.Name))
                {
                    error = "El contenido firmado contiene propiedades JSON duplicadas.";
                    return false;
                }

                if (!ValidarObjetosSinDuplicados(propiedad.Value, out error))
                {
                    return false;
                }
            }
        }
        else if (elemento.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in elemento.EnumerateArray())
            {
                if (!ValidarObjetosSinDuplicados(item, out error))
                {
                    return false;
                }
            }
        }

        error = string.Empty;
        return true;
    }

    private static string LeerTexto(JsonElement objeto, string propiedad)
    {
        return objeto.TryGetProperty(propiedad, out var valor)
            && valor.ValueKind == JsonValueKind.String
                ? valor.GetString() ?? string.Empty
                : string.Empty;
    }

    private static bool EsContenedorAesAnterior(JsonElement contenedor)
    {
        if (contenedor.TryGetProperty("Version", out var version)
            && version.TryGetInt32(out var numeroVersion)
            && numeroVersion is 1 or 2)
        {
            return true;
        }

        var algoritmo = LeerTexto(contenedor, "Algoritmo");
        return algoritmo.Contains("AES", StringComparison.OrdinalIgnoreCase)
            || contenedor.TryGetProperty("Nonce", out _)
            || contenedor.TryGetProperty("Etiqueta", out _)
            || contenedor.TryGetProperty("KeyId", out _);
    }

    private static bool EsConjuntoIdValido(string conjuntoId)
    {
        return conjuntoId.Length == LongitudConjuntoId
            && conjuntoId.All(caracter =>
                (caracter is >= '0' and <= '9') || (caracter is >= 'A' and <= 'F'));
    }

    private static void RechazarEnlacesSistema(string ruta)
    {
        // Rechaza archivos y carpetas enlazados antes de acceder al artefacto.
        if (File.Exists(ruta)
            && File.GetAttributes(ruta).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("El artefacto firmado no puede ser un enlace del sistema.");
        }

        var carpeta = new DirectoryInfo(
            Path.GetDirectoryName(ruta)
            ?? throw new IOException("No se pudo resolver la carpeta del artefacto firmado."));
        while (carpeta is not null)
        {
            if (carpeta.Exists
                && carpeta.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("La ruta del artefacto firmado no puede contener enlaces del sistema.");
            }

            carpeta = carpeta.Parent;
        }
    }

    private static void ValidarTipo(string tipo)
    {
        if (tipo is not TipoPermisos and not TipoCatalogoScripts)
        {
            throw new ArgumentException("El tipo de artefacto no esta permitido.", nameof(tipo));
        }
    }
}
