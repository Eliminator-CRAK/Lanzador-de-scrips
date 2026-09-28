// (Autor: Alex Roman)
// Descripcion: Identifica una preparacion local sin aceptar rutas arbitrarias por el protocolo.
namespace LanzadorScripts.Protocolo;

public sealed record PublicarActualizacionServidor(Guid PreparacionId, string NombreArchivo, string Sha256);
