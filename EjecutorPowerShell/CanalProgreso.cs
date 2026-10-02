// (Autor: Alex Roman)
// Descripcion: Agrupa el ultimo progreso por actividad y limita mensajes del canal privado.

using System;
using System.Collections.Generic;
using System.IO;
using System.Management.Automation;
using System.Text;
using System.Threading;

namespace LanzadorScripts.EjecutorPowerShell
{
    internal sealed class CanalProgreso : IDisposable
    {
        private readonly Stream _pipe;
        private readonly object _bloqueo = new object();
        private readonly Dictionary<string, byte[]> _pendientes = new Dictionary<string, byte[]>();
        private readonly Timer _timer;
        private bool _cerrado;
        internal CanalProgreso(Stream pipe) { _pipe = pipe; _timer = new Timer(EnviarPendientes, null, 100, 100); }

        internal void Progreso(long origen, ProgressRecord progreso)
        {
            using (var memoria = new MemoryStream())
            using (var escritor = new BinaryWriter(memoria, Encoding.UTF8, true))
            {
                escritor.Write((byte)1);
                escritor.Write(origen);
                escritor.Write(progreso.ActivityId);
                escritor.Write(progreso.ParentActivityId);
                Texto(escritor, progreso.Activity);
                Texto(escritor, progreso.StatusDescription);
                Texto(escritor, progreso.CurrentOperation);
                escritor.Write(progreso.PercentComplete);
                escritor.Write(progreso.SecondsRemaining);
                escritor.Write(progreso.RecordType == ProgressRecordType.Completed);
                lock (_bloqueo)
                {
                    if (_cerrado) return;
                    var clave = origen + ":" + progreso.ActivityId;
                    if (_pendientes.Count < 128 || _pendientes.ContainsKey(clave)) _pendientes[clave] = memoria.ToArray();
                }
            }
        }

        internal void Entrada(bool protegida)
        {
            lock (_bloqueo) { if (!_cerrado) Enviar(new[] { (byte)2, protegida ? (byte)1 : (byte)0 }); }
        }

        private static void Texto(BinaryWriter escritor, string texto)
        {
            texto = texto ?? "";
            if (texto.Length > 1024) texto = texto.Substring(0, 1024);
            var bytes = Encoding.UTF8.GetBytes(texto);
            escritor.Write(bytes.Length);
            escritor.Write(bytes);
        }

        private void Enviar(byte[] mensaje)
        {
            var largo = BitConverter.GetBytes(mensaje.Length);
            _pipe.Write(largo, 0, largo.Length);
            _pipe.Write(mensaje, 0, mensaje.Length);
            _pipe.Flush();
        }

        private void EnviarPendientes(object estado)
        {
            lock (_bloqueo)
            {
                if (_cerrado) return;
                try { foreach (var mensaje in _pendientes.Values) Enviar(mensaje); _pendientes.Clear(); }
                catch (IOException) { _cerrado = true; _pendientes.Clear(); }
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
            EnviarPendientes(null);
            lock (_bloqueo) { _cerrado = true; _pendientes.Clear(); }
        }
    }
}
