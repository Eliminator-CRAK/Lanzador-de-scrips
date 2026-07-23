// (Autor: Alex Roman)
// Descripcion: Exporta e importa paquetes cifrados con rutas de configuracion.

using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LanzadorScripts.Modelos;

namespace LanzadorScripts.Servicios;

public sealed class ServicioPaquetesConfiguracion
{
    public const string ExtensionPaquete = ".lanzadorconfig";
    public const int LongitudMaximaContenido = 16 * 1024 * 1024;
    public const int LongitudMaximaBase64 = ((LongitudMaximaContenido + 2) / 3) * 4;
    private const string TipoCifrado = "configuracion-exportada";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly ServicioCifradoAplicacion _servicioCifrado;
    private readonly ServicioArtefactosProtegidos _servicioArtefactos;

    public ServicioPaquetesConfiguracion()
        : this(new ServicioCifradoAplicacion(), new ServicioArtefactosProtegidos())
    {
    }

    public ServicioPaquetesConfiguracion(ServicioCifradoAplicacion servicioCifrado)
        : this(servicioCifrado, new ServicioArtefactosProtegidos())
    {
    }

    internal ServicioPaquetesConfiguracion(
        ServicioCifradoAplicacion servicioCifrado,
        ServicioArtefactosProtegidos servicioArtefactos)
    {
        _servicioCifrado = servicioCifrado;
        _servicioArtefactos = servicioArtefactos;
    }

    public PaqueteExportado Exportar(ConfiguracionLanzador configuracion, JsonObject permisos)
    {
        var payload = new PayloadConfiguracionExportada(
            configuracion.RutaScripts,
            configuracion.RutaPermisos,
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            WindowsIdentity.GetCurrent().Name,
            permisos);
        var json = JsonSerializer.Serialize(payload, OpcionesJson);
        var cifrado = _servicioCifrado.CifrarTexto(TipoCifrado, json);
        var nombre = $"LanzadorScripts_{DateTime.Now:yyyyMMdd_HHmmss}{ExtensionPaquete}";

        return new PaqueteExportado(nombre, Convert.ToBase64String(Encoding.UTF8.GetBytes(cifrado)));
    }

    public ResultadoImportacionConfiguracion ImportarContenido(
        string contenido,
        ConfiguracionLanzador configuracionActual)
    {
        if (string.IsNullOrWhiteSpace(contenido))
        {
            throw new InvalidOperationException("El paquete de configuracion esta vacio.");
        }

        if (Encoding.UTF8.GetByteCount(contenido) > LongitudMaximaContenido)
        {
            throw new InvalidOperationException("El paquete de configuracion supera el limite de 16 MiB.");
        }

        if (!_servicioCifrado.IntentarDescifrarTexto(TipoCifrado, contenido, out var json))
        {
            throw new InvalidOperationException("El paquete de configuracion no es valido o fue modificado.");
        }

        var payload = JsonSerializer.Deserialize<PayloadConfiguracionExportada>(json, OpcionesJson)
            ?? throw new InvalidOperationException("El paquete de configuracion no contiene rutas validas.");

        var rutaCarpetaPermisos = RutasArtefactosProtegidos.NormalizarCarpetaConfigurada(
            payload.RutaPermisos,
            RutasArtefactosProtegidos.CarpetaPredeterminada);
        var validacion = new ServicioValidacionScripts().ValidarConfiguracionBasica(
            payload.RutaScripts,
            rutaCarpetaPermisos);
        if (!validacion.EsValida)
        {
            throw new InvalidOperationException(validacion.Mensaje);
        }

        configuracionActual.RutaScripts = payload.RutaScripts;
        configuracionActual.RutaPermisos = rutaCarpetaPermisos;
        configuracionActual.Normalizar();
        return new ResultadoImportacionConfiguracion(configuracionActual, payload.Permisos);
    }

    public void GuardarPermisosImportados(ConfiguracionLanzador configuracion, JsonObject permisos)
    {
        var rutaPermisos = new ServicioValidacionScripts().ResolverRutaPermisos(configuracion.RutaScripts, configuracion.RutaPermisos);
        var carpeta = Path.GetDirectoryName(rutaPermisos);
        if (!string.IsNullOrWhiteSpace(carpeta))
        {
            Directory.CreateDirectory(carpeta);
        }

        var json = ServicioSeguridadScripts.NormalizarPolitica(permisos["seguridadScripts"] as JsonObject);
        permisos["seguridadScripts"] = json;
        _servicioArtefactos.GuardarTextoProtegido(
            rutaPermisos,
            ServicioArtefactosProtegidos.TipoPermisos,
            permisos.ToJsonString(OpcionesJson));
    }

    public static string ResolverRutaImportacion(string rutaArchivo)
    {
        return ServicioRutasSeguras.ResolverArchivoAbsoluto(
            rutaArchivo,
            "paquete de configuracion",
            ExtensionPaquete);
    }

    public static bool EsRutaImportacionValida(string rutaArchivo)
    {
        try
        {
            return File.Exists(ResolverRutaImportacion(rutaArchivo));
        }
        catch
        {
            return false;
        }
    }

    private sealed record PayloadConfiguracionExportada(
        string RutaScripts,
        string RutaPermisos,
        DateTimeOffset Creado,
        string EquipoEmisor,
        string UsuarioEmisor,
        JsonObject? Permisos = null);
}

public sealed record PaqueteExportado(string NombreArchivo, string ContenidoBase64);

public sealed record ResultadoImportacionConfiguracion(ConfiguracionLanzador Configuracion, JsonObject? Permisos);
