// (Autor: Alex Roman)
// Descripcion: Modelos de presentacion usados por la consola administrativa.

namespace LanzadorScripts.Servidor.Administracion;

public sealed record CatalogoVista(
    string ScriptId,
    string Extension,
    long Longitud,
    string Sha256);

public sealed record AuditoriaVista(
    DateTimeOffset FechaLocal,
    string Usuario,
    string Equipo,
    string Accion,
    string Script,
    string Resultado,
    int? CodigoSalida,
    string Detalle);

public sealed record EstadoServicioVista(
    bool Instalado,
    bool EnEjecucion,
    string Estado);
