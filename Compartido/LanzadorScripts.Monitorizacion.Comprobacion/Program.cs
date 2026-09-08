// (Autor: Alex Roman)
// Descripcion: Envia eventos sinteticos a GitLab solo con autorizacion explicita.

using LanzadorScripts.Monitorizacion;

if (args.Length != 1 || args[0] != "--enviar-prueba")
{
    Console.Error.WriteLine("Use --enviar-prueba para enviar tres senales sinteticas a GitLab.");
    return 2;
}

Environment.SetEnvironmentVariable("LANZADOR_MONITORIZACION_ENTORNO", "test");
using (var monitor = MonitorizacionAplicacion.Iniciar(ComponenteMonitorizado.ClienteInstalado))
{
    using (var medicion = monitor.Medir("monitorizacion.prueba"))
    {
        await Task.Delay(50);
        medicion.Completar();
    }
    // Un resultado negativo sintetico comprueba el canal de logs sin datos reales.
    using (monitor.Medir("monitorizacion.prueba")) { }
}
Console.WriteLine("Prueba finalizada. Verifique Services, Traces, Metrics y Logs en GitLab, entorno test.");
return 0;
