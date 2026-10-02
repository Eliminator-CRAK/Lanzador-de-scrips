// (Autor: Alex Roman)
// Descripcion: Prepara el ejecutor incrustado y recibe mensajes autenticados separados de stdout.

using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace LanzadorScripts.Servicios;

internal sealed class ServicioProcesoScript : IDisposable
{
    private readonly NamedPipeServerStream? _pipe;
    private readonly byte[] _token = RandomNumberGenerator.GetBytes(32);
    private readonly string? _directorio;
    private readonly FileStream? _bloqueoArchivo;
    public Process Proceso { get; }

    public ServicioProcesoScript(ScriptInterno script, bool permitirBypass)
    {
        var inicio = new ProcessStartInfo
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(script.RutaCompleta) ?? Environment.CurrentDirectory
        };
        if (script.Tipo == "powershell")
        {
            try
            {
                _directorio = Path.Combine(Path.GetTempPath(), "LanzadorScriptsHost_" + Guid.NewGuid().ToString("N"));
                var seguridad = new DirectorySecurity();
                seguridad.SetAccessRuleProtection(true, false);
                using var identidad = WindowsIdentity.GetCurrent();
                var usuario = identidad.User ?? throw new InvalidOperationException("No se pudo identificar al usuario.");
                foreach (var sid in new[] { usuario, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                    seguridad.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                // Crea la carpeta con su ACL antes de escribir el ejecutable.
                FileSystemAclExtensions.CreateDirectory(seguridad, _directorio);
                if ((File.GetAttributes(_directorio) & FileAttributes.ReparsePoint) != 0) throw new IOException("Directorio temporal no seguro.");
                var ruta = Path.Combine(_directorio, "LanzadorScripts.EjecutorPowerShell.exe");
                using var recurso = typeof(ServicioProcesoScript).Assembly.GetManifestResourceStream("LanzadorScripts.EjecutorPowerShell.exe")
                    ?? throw new InvalidOperationException("No se encontro el ejecutor incrustado.");
                var contenido = new MemoryStream();
                recurso.CopyTo(contenido);
                var bytes = contenido.ToArray();
                using (var archivo = new FileStream(ruta, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { archivo.Write(bytes); archivo.Flush(true); }
                _bloqueoArchivo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(_bloqueoArchivo)))
                    throw new InvalidDataException("El ejecutor temporal no coincide con el recurso protegido.");
                var nombre = "LanzadorScriptsProgreso_" + Guid.NewGuid().ToString("N");
                _pipe = new NamedPipeServerStream(nombre, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                inicio.FileName = ruta;
                inicio.ArgumentList.Add(script.RutaCompleta);
                inicio.ArgumentList.Add(nombre);
                inicio.Environment["LANZADOR_PROGRESS_TOKEN"] = Convert.ToHexString(_token);
                if (permitirBypass) inicio.Environment["PSExecutionPolicyPreference"] = "Bypass";
                inicio.StandardOutputEncoding = Encoding.UTF8;
                inicio.StandardErrorEncoding = Encoding.UTF8;
                inicio.StandardInputEncoding = new UTF8Encoding(false);
            }
            catch { _pipe?.Dispose(); _bloqueoArchivo?.Dispose(); Limpiar(); throw; }
        }
        else
        {
            inicio.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            inicio.ArgumentList.Add("/d"); inicio.ArgumentList.Add("/c"); inicio.ArgumentList.Add(script.RutaCompleta);
            inicio.StandardOutputEncoding = Encoding.Default; inicio.StandardErrorEncoding = Encoding.Default;
        }
        Proceso = new Process { StartInfo = inicio, EnableRaisingEvents = true };
    }

    public async Task LeerControlAsync(Func<EventoCliente, Task> recibir, CancellationToken cancelacion)
    {
        try { await LeerMensajesAsync(recibir, cancelacion); }
        catch
        {
            // Un canal no autenticado o interrumpido no deja el script ejecutandose sin control.
            try { if (!Proceso.HasExited) Proceso.Kill(true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            throw;
        }
    }

    private async Task LeerMensajesAsync(Func<EventoCliente, Task> recibir, CancellationToken cancelacion)
    {
        if (_pipe is null) return;
        using var conexion = CancellationTokenSource.CreateLinkedTokenSource(cancelacion);
        conexion.CancelAfter(TimeSpan.FromSeconds(15));
        await _pipe.WaitForConnectionAsync(conexion.Token);
        if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle.DangerousGetHandle(), out var id) || id != Proceso.Id)
            throw new InvalidDataException("El proceso del canal de progreso no esta autorizado.");
        var token = new byte[32];
        await _pipe.ReadExactlyAsync(token, conexion.Token);
        if (!CryptographicOperations.FixedTimeEquals(token, _token)) throw new InvalidDataException("Canal de progreso no autenticado.");
        CryptographicOperations.ZeroMemory(token);
        var longitud = new byte[4];
        while (true)
        {
            var primero = await _pipe.ReadAsync(longitud.AsMemory(0, 1), cancelacion);
            if (primero == 0) return;
            await _pipe.ReadExactlyAsync(longitud.AsMemory(1), cancelacion);
            var largo = BitConverter.ToInt32(longitud);
            if (largo is < 1 or > 16384) throw new InvalidDataException("Mensaje de progreso demasiado grande.");
            var mensaje = new byte[largo];
            await _pipe.ReadExactlyAsync(mensaje, cancelacion);
            using var lector = new BinaryReader(new MemoryStream(mensaje), new UTF8Encoding(false, true));
            var tipo = lector.ReadByte();
            EventoCliente evento;
            if (tipo == 1)
            {
                var origen = lector.ReadInt64(); var actividad = lector.ReadInt32(); var padre = lector.ReadInt32();
                var descripcion = LeerTexto(lector); var estado = LeerTexto(lector); var operacion = LeerTexto(lector);
                var porcentaje = lector.ReadInt32(); var segundos = lector.ReadInt32(); var completo = lector.ReadBoolean();
                if (actividad < 0 || padre < -1 || porcentaje is < -1 or > 100 || segundos < -1)
                    throw new InvalidDataException("Progreso no valido.");
                evento = new EventoCliente("progreso", "", Progreso: new ProgresoScript(origen, actividad, padre, descripcion, estado, operacion, porcentaje, segundos, completo));
            }
            else if (tipo == 2) evento = new EventoCliente("entrada", "", EntradaProtegida: lector.ReadBoolean());
            else throw new InvalidDataException("Tipo de control no valido.");
            if (lector.BaseStream.Position != largo) throw new InvalidDataException("Mensaje de control no valido.");
            await recibir(evento);
        }
    }

    private static string LeerTexto(BinaryReader lector)
    {
        var largo = lector.ReadInt32();
        if (largo is < 0 or > 4096 || largo > lector.BaseStream.Length - lector.BaseStream.Position) throw new InvalidDataException("Descripcion demasiado grande.");
        return new UTF8Encoding(false, true).GetString(lector.ReadBytes(largo));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out int proceso);

    public void Dispose()
    {
        try { if (!Proceso.HasExited) Proceso.Kill(true); } catch (InvalidOperationException) { }
        Proceso.Dispose(); _pipe?.Dispose(); _bloqueoArchivo?.Dispose();
        CryptographicOperations.ZeroMemory(_token); Limpiar();
    }

    private void Limpiar()
    {
        try { if (_directorio is not null && Directory.Exists(_directorio)) Directory.Delete(_directorio, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
