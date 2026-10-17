// (Autor: Alex Roman)
// Descripcion: Ejecuta un script con el motor del sistema y transmite progreso por un pipe privado.

using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Reflection;
using Microsoft.PowerShell;

namespace LanzadorScripts.EjecutorPowerShell
{
    internal static class Programa
    {
        [STAThread]
        private static int Main(string[] argumentos)
        {
            try
            {
                if (argumentos.Length != 2 || !Path.IsPathRooted(argumentos[0])) return 2;
                Console.OutputEncoding = new UTF8Encoding(false);
                Console.InputEncoding = new UTF8Encoding(false);
                var token = Environment.GetEnvironmentVariable("LANZADOR_PROGRESS_TOKEN");
                Environment.SetEnvironmentVariable("LANZADOR_PROGRESS_TOKEN", null);
                if (token == null || token.Length != 64) return 2;
                using (var pipe = new NamedPipeClientStream(".", argumentos[1], PipeDirection.Out))
                {
                    pipe.Connect(15000);
                    var bytes = new byte[32];
                    for (var i = 0; i < bytes.Length; i++) bytes[i] = byte.Parse(token.Substring(i * 2, 2), NumberStyles.HexNumber);
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();
                    Array.Clear(bytes, 0, bytes.Length);
                    token = null;
                    using (var canal = new CanalProgreso(pipe))
                    {
                        var host = new HostScript(canal);
                        var estado = InitialSessionState.CreateDefault();
                        // Aplica las mismas politicas y directivas de Windows PowerShell, sin cargar perfiles.
                        estado.AuthorizationManager = new PSAuthorizationManager("Microsoft.PowerShell");
                        estado.ThreadOptions = PSThreadOptions.ReuseThread;
                        estado.ApartmentState = System.Threading.ApartmentState.STA;
                        using (var sesion = RunspaceFactory.CreateRunspace(host, estado))
                        using (var shell = PowerShell.Create())
                        {
                            sesion.Open();
                            sesion.SessionStateProxy.Path.SetLocation(Path.GetDirectoryName(argumentos[0]));
                            shell.Runspace = sesion;
                            shell.AddCommand(argumentos[0]);
                            shell.Commands.Commands[0].MergeMyResults(PipelineResultTypes.Error, PipelineResultTypes.Output);
                            shell.AddCommand("Out-Default");
                            PrepararSalidaDeArchivo(sesion);
                            try { shell.Invoke(); }
                            catch (RuntimeException ex) { Console.Error.WriteLine(ex.Message); return host.SolicitoSalida ? host.CodigoSalida : 1; }
                            if (host.SolicitoSalida) return host.CodigoSalida;
                            // Como -File, solo exit explicito o una excepcion terminante cambia el resultado.
                            return 0;
                        }
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }

        private static void PrepararSalidaDeArchivo(Runspace sesion)
        {
            // Activa el mismo tratamiento de exit que Windows PowerShell 5.1 utiliza con -File.
            var intrinsecos = sesion.SessionStateProxy.GetVariable("ExecutionContext");
            var campo = typeof(EngineIntrinsics).GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic);
            var contexto = campo == null ? null : campo.GetValue(intrinsecos);
            var propiedad = contexto == null ? null : contexto.GetType().GetProperty("ScriptCommandProcessorShouldRethrowExit", BindingFlags.Instance | BindingFlags.NonPublic);
            if (propiedad == null || !propiedad.CanWrite) throw new NotSupportedException("El motor no permite conservar los codigos de salida de archivos.");
            propiedad.SetValue(contexto, true, null);
        }
    }
}
