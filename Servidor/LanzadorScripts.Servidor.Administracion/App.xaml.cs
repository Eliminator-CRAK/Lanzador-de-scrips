// (Autor: Alex Roman)
// Descripcion: Punto de entrada de la consola administrativa.

using System.Windows;
using LanzadorScripts.Monitorizacion;

namespace LanzadorScripts.Servidor.Administracion;

public partial class App : Application
{
    private MonitorizacionAplicacion? _monitorizacion;

    protected override void OnStartup(StartupEventArgs e)
    {
        _monitorizacion = MonitorizacionAplicacion.Iniciar(ComponenteMonitorizado.ConsolaServidor);
        using var medicion = _monitorizacion.Medir("consola.inicio");
        base.OnStartup(e);
        medicion.Completar();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _monitorizacion?.Dispose();
        base.OnExit(e);
    }
}
