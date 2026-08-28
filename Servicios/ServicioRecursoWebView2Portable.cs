// (Autor: Alex Roman)
// Descripcion: Lee WebView2 desde el recurso del ejecutable portable distribuido.

using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace LanzadorScripts.Servicios;

internal static class ServicioRecursoWebView2Portable
{
    internal const int IdRecursoRuntime = 103;
    private const uint LoadLibraryAsDataFile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;
    private const int TipoRcData = 10;
    private const long TamanoMaximoRecurso = 512L * 1024 * 1024;

    public static Stream? Abrir()
    {
        if (!RutasAplicacion.Distribucion.EsPortable)
        {
            return null;
        }

        var rutaLanzador = Environment.GetEnvironmentVariable(
            ServicioEjecutableAplicacion.VariableEjecutableDistribuido);
        if (string.IsNullOrWhiteSpace(rutaLanzador))
        {
            return null;
        }

        if (rutaLanzador.Contains('/')
            || !Path.IsPathFullyQualified(rutaLanzador)
            || ServicioRutasSeguras.ContieneSegmentosNavegacion(rutaLanzador))
        {
            throw new InvalidDataException("La ruta del lanzador portable no es valida.");
        }

        var rutaCompleta = Path.GetFullPath(rutaLanzador);
        if (!File.Exists(rutaCompleta))
        {
            throw new FileNotFoundException(
                "No se encontro el ejecutable portable que contiene WebView2.",
                rutaCompleta);
        }

        var modulo = LoadLibraryEx(
            rutaCompleta,
            IntPtr.Zero,
            LoadLibraryAsDataFile | LoadLibraryAsImageResource);
        if (modulo == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var recurso = FindResource(
                modulo,
                new IntPtr(IdRecursoRuntime),
                new IntPtr(TipoRcData));
            if (recurso == IntPtr.Zero)
            {
                throw new InvalidDataException("El lanzador portable no contiene WebView2.");
            }

            var tamano = SizeofResource(modulo, recurso);
            var recursoCargado = LoadResource(modulo, recurso);
            var datos = recursoCargado == IntPtr.Zero
                ? IntPtr.Zero
                : LockResource(recursoCargado);
            if (datos == IntPtr.Zero || tamano == 0 || tamano > TamanoMaximoRecurso)
            {
                throw new InvalidDataException("El recurso WebView2 portable no es valido.");
            }

            var flujo = new FlujoRecursoNativo(modulo, datos, tamano);
            modulo = IntPtr.Zero;
            return flujo;
        }
        finally
        {
            if (modulo != IntPtr.Zero)
            {
                FreeLibrary(modulo);
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(
        string nombreArchivo,
        IntPtr archivo,
        uint indicadores);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResource(
        IntPtr modulo,
        IntPtr nombre,
        IntPtr tipo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr modulo, IntPtr recurso);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr modulo, IntPtr recurso);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr recursoCargado);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr modulo);

    private sealed class FlujoRecursoNativo : Stream
    {
        private IntPtr _modulo;
        private readonly IntPtr _datos;
        private readonly long _longitud;
        private long _posicion;

        public FlujoRecursoNativo(IntPtr modulo, IntPtr datos, long longitud)
        {
            _modulo = modulo;
            _datos = datos;
            _longitud = longitud;
        }

        public override bool CanRead => _modulo != IntPtr.Zero;

        public override bool CanSeek => _modulo != IntPtr.Zero;

        public override bool CanWrite => false;

        public override long Length => _longitud;

        public override long Position
        {
            get => _posicion;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_modulo == IntPtr.Zero, this);
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count)
            {
                throw new ArgumentException("El segmento de lectura no es valido.");
            }

            var disponibles = _longitud - _posicion;
            if (disponibles <= 0)
            {
                return 0;
            }

            var leidos = checked((int)Math.Min(count, disponibles));
            Marshal.Copy(IntPtr.Add(_datos, checked((int)_posicion)), buffer, offset, leidos);
            _posicion += leidos;
            return leidos;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_modulo == IntPtr.Zero, this);
            var nuevaPosicion = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_posicion + offset),
                SeekOrigin.End => checked(_longitud + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (nuevaPosicion < 0 || nuevaPosicion > _longitud)
            {
                throw new IOException("La posicion del recurso queda fuera de sus limites.");
            }

            _posicion = nuevaPosicion;
            return _posicion;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            var modulo = Interlocked.Exchange(ref _modulo, IntPtr.Zero);
            if (modulo != IntPtr.Zero)
            {
                FreeLibrary(modulo);
            }

            base.Dispose(disposing);
        }
    }
}
