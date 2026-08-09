// (Autor: Alex Roman)
// Descripcion: Evita interferencias entre pruebas que cambian el entorno del proceso.

using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
