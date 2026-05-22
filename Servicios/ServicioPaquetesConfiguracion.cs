// (Autor: Alex Roman)
// Descripcion: Exporta e importa paquetes cifrados con rutas de configuracion.

using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LanzadorScripts.Modelos;

namespace LanzadorScripts.Servicios;

public sealed class ServicioPaquetesConfiguracion
{
    public const string ExtensionPaquete = ".lanzadorconfig";
    private const string TipoCifrado = "configuracion-exportada";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly ServicioCifradoAplicacion _servicioCifrado = new();

    public PaqueteExportado Exportar(ConfiguracionLanzador configuracion)
    {
        var payload = new PayloadConfiguracionExportada(
            configuracion.RutaScripts,
            configuracion.RutaPermisos,
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            WindowsIdentity.GetCurrent().Name);
        var json = JsonSerializer.Serialize(payload, OpcionesJson);
        var cifrado = _servicioCifrado.CifrarTexto(TipoCifrado, json);
        var nombre = $"LanzadorScripts_{DateTime.Now:yyyyMMdd_HHmmss}{ExtensionPaquete}";

        return new PaqueteExportado(nombre, Convert.ToBase64String(Encoding.UTF8.GetBytes(cifrado)));
    }

    public ConfiguracionLanzador Importar(string rutaArchivo, ConfiguracionLanzador configuracionActual)
    {
        if (!File.Exists(rutaArchivo))
        {
            throw new FileNotFoundException("No se encontro el paquete de configuracion.", rutaArchivo);
        }

        var texto = File.ReadAllText(rutaArchivo, Encoding.UTF8);
        if (!_servicioCifrado.IntentarDescifrarTexto(TipoCifrado, texto, out var json))
        {
            throw new InvalidOperationException("El paquete de configuracion no es valido o fue modificado.");
        }

        var payload = JsonSerializer.Deserialize<PayloadConfiguracionExportada>(json, OpcionesJson)
            ?? throw new InvalidOperationException("El paquete de configuracion no contiene rutas validas.");

        configuracionActual.RutaScripts = payload.RutaScripts;
        configuracionActual.RutaPermisos = payload.RutaPermisos;
        configuracionActual.Normalizar();
        return configuracionActual;
    }

    private sealed record PayloadConfiguracionExportada(
        string RutaScripts,
        string RutaPermisos,
        DateTimeOffset Creado,
        string EquipoEmisor,
        string UsuarioEmisor);
}

public sealed record PaqueteExportado(string NombreArchivo, string ContenidoBase64);
