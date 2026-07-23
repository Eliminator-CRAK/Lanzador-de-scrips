// (Autor: Alex Roman)
// Descripcion: Contratos internos usados por la API web local.

using System.IO;
using System.Text;

namespace LanzadorScripts.Servicios;

public sealed record UsuarioCliente(
    string NombreUsuario,
    string Rol,
    int MaxScriptsSimultaneos,
    bool EstaAutorizado,
    string MotivoBloqueo = "",
    IReadOnlySet<string>? CarpetasPermitidas = null);

public sealed class RutaScriptValidada
{
    internal RutaScriptValidada(
        string raizAutorizada,
        string rutaCompleta,
        string identificador,
        string extension)
    {
        RaizAutorizada = raizAutorizada;
        RutaCompleta = rutaCompleta;
        Identificador = identificador;
        Extension = extension;
    }

    public string RaizAutorizada { get; }

    public string RutaCompleta { get; }

    public string Identificador { get; }

    public string Extension { get; }

    public string Directorio => Path.GetDirectoryName(RutaCompleta)
        ?? throw new InvalidOperationException("No se pudo resolver el directorio del script validado.");

    internal FileStream AbrirLectura()
    {
        // Abre unicamente la ruta aprobada por el validador.
        return new FileStream(
            RutaCompleta,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
    }

    internal long ObtenerLongitud()
    {
        using var flujo = AbrirLectura();
        return flujo.Length;
    }

    internal string LeerTexto(Encoding codificacion)
    {
        using var flujo = AbrirLectura();
        using var lector = new StreamReader(flujo, codificacion, detectEncodingFromByteOrderMarks: true);
        return lector.ReadToEnd();
    }
}

public sealed record ScriptInterno
{
    internal ScriptInterno(
        string id,
        string nombre,
        string tipo,
        RutaScriptValidada rutaValidada)
    {
        Id = id;
        Nombre = nombre;
        Tipo = tipo;
        RutaValidada = rutaValidada;
    }

    public string Id { get; }

    public string Nombre { get; }

    public string Tipo { get; }

    public RutaScriptValidada RutaValidada { get; }

    public string RutaCompleta => RutaValidada.RutaCompleta;
}

public sealed record EventoCliente(string Tipo, string Mensaje, string? Color = null, bool Finalizado = false);

public sealed record EstadoCatalogoScriptCliente(
    string ScriptId,
    string Tipo,
    long Longitud,
    string Sha256,
    string Estado,
    bool Incluido);
