// (Autor: Alex Roman)
// Descripcion: Lee lineas y teclas UTF-8 del mismo stdin sin ocultar respuestas pendientes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LanzadorScripts.EjecutorPowerShell
{
    internal sealed class EntradaScript
    {
        private readonly Stream _entrada = Console.OpenStandardInput();
        private readonly Decoder _decodificador = new UTF8Encoding(false, true).GetDecoder();
        private readonly Queue<char> _caracteres = new Queue<char>();
        private readonly byte[] _byte = new byte[1];
        private readonly char[] _chars = new char[2];
        private bool _omitirLf;

        internal bool Disponible
        {
            get
            {
                uint disponibles;
                while (_caracteres.Count == 0 && PeekNamedPipe(GetStdHandle(-10), IntPtr.Zero, 0, IntPtr.Zero, out disponibles, IntPtr.Zero) && disponibles > 0)
                {
                    if (_entrada.Read(_byte, 0, 1) == 0) break;
                    var cantidad = _decodificador.GetChars(_byte, 0, 1, _chars, 0);
                    for (var i = 0; i < cantidad; i++)
                    {
                        var caracter = _chars[i];
                        if (_omitirLf) { _omitirLf = false; if (caracter == '\n') continue; }
                        _caracteres.Enqueue(caracter);
                    }
                }
                return _caracteres.Count > 0;
            }
        }

        internal string LeerLinea()
        {
            var texto = new StringBuilder();
            int caracter;
            while ((caracter = LeerCaracter()) >= 0)
            {
                if (caracter == '\r' || caracter == '\n') return texto.ToString();
                texto.Append((char)caracter);
            }
            return texto.ToString();
        }

        internal int LeerCaracter()
        {
            while (true)
            {
                while (_caracteres.Count == 0)
                {
                    if (_entrada.Read(_byte, 0, 1) == 0) return -1;
                    var cantidad = _decodificador.GetChars(_byte, 0, 1, _chars, 0);
                    for (var i = 0; i < cantidad; i++) _caracteres.Enqueue(_chars[i]);
                }
                var caracter = _caracteres.Dequeue();
                if (_omitirLf) { _omitirLf = false; if (caracter == '\n') continue; }
                if (caracter == '\r') _omitirLf = true;
                return caracter;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int identificador);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekNamedPipe(IntPtr pipe, IntPtr buffer, uint longitud, IntPtr leidos, out uint disponibles, IntPtr restantes);
    }
}
