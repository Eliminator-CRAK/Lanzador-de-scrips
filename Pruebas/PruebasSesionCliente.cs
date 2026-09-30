// (Autor: Alex Roman)
// Descripcion: Comprueba la limpieza de sesiones sin afectar procesos activos ni carpetas ajenas.

using LanzadorScripts.Servicios;
using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasSesionCliente : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "LanzadorScripts-sesion-test", Guid.NewGuid().ToString("N"));
    private string NuevaSesion() => Path.Combine(_raiz, "Sesion-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CierreNormalEliminaSoloLaSesionInstalada()
    {
        var ruta = NuevaSesion();
        using (var sesion = new ServicioSesionCliente(ruta, portable: false))
        {
            File.WriteAllText(Path.Combine(ruta, "datos.tmp"), "temporal");
        }
        Assert.False(Directory.Exists(ruta));
        Assert.False(File.Exists(ruta + ".lock"));
    }

    [Fact]
    public void PortableDelegaElBorradoFinalAlLanzadorNativo()
    {
        var ruta = NuevaSesion();
        using (var sesion = new ServicioSesionCliente(ruta, portable: true))
            File.WriteAllText(Path.Combine(ruta, "datos.tmp"), "temporal");
        Assert.True(File.Exists(Path.Combine(ruta, "datos.tmp")));
    }

    [Fact]
    public void RecuperacionConservaSesionesActivasYCarpetasNoGestionadas()
    {
        var activa = NuevaSesion();
        using var sesion = new ServicioSesionCliente(activa, portable: false);
        Directory.SetCreationTimeUtc(activa, DateTime.UtcNow.AddMinutes(-5));
        var abandonada = NuevaSesion();
        Directory.CreateDirectory(abandonada);
        File.WriteAllText(abandonada + ".lock", "");
        File.WriteAllText(Path.Combine(abandonada, "datos.tmp"), "temporal");
        Directory.SetCreationTimeUtc(abandonada, DateTime.UtcNow.AddMinutes(-5));
        var ajena = NuevaSesion();
        Directory.CreateDirectory(ajena);
        Directory.SetCreationTimeUtc(ajena, DateTime.UtcNow.AddMinutes(-5));
        ServicioSesionCliente.LimpiarAbandonadas(_raiz, NuevaSesion());
        Assert.True(Directory.Exists(activa));
        Assert.True(Directory.Exists(ajena));
        Assert.False(Directory.Exists(abandonada));
        Assert.False(File.Exists(abandonada + ".lock"));
    }

    [Fact]
    public void RecuperacionEliminaMarcadoresHuerfanosSinTocarLosBloqueados()
    {
        Directory.CreateDirectory(_raiz);
        var huerfano = NuevaSesion() + ".lock";
        File.WriteAllText(huerfano, "");
        File.SetCreationTimeUtc(huerfano, DateTime.UtcNow.AddMinutes(-5));
        var bloqueado = NuevaSesion() + ".lock";
        using var bloqueo = new FileStream(bloqueado, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        File.SetCreationTimeUtc(bloqueado, DateTime.UtcNow.AddMinutes(-5));
        ServicioSesionCliente.LimpiarAbandonadas(_raiz, NuevaSesion());
        Assert.False(File.Exists(huerfano));
        Assert.True(File.Exists(bloqueado));
    }

    public void Dispose()
    {
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
    }
}
