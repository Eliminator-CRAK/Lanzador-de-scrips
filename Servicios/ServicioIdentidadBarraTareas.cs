// (Autor: Alex Roman)
// Descripcion: Asocia la ventana con el ejecutable distribuido en la barra de tareas.

using System.IO;
using System.Runtime.InteropServices;

namespace LanzadorScripts.Servicios;

public static class ServicioIdentidadBarraTareas
{
    private const string IdPortable = "Aena.LanzadorScripts.Portable";
    private const int IdRecursoNombrePortable = 201;
    private const ushort TipoCadenaUnicode = 31;
    private static readonly Guid FormatoAppUserModel = new(
        "9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    public static bool ConfigurarProceso(ContextoDistribucion distribucion)
    {
        if (!distribucion.EsPortable)
        {
            return true;
        }

        var resultado = SetCurrentProcessExplicitAppUserModelID(IdPortable);
        return resultado >= 0;
    }

    public static bool ConfigurarVentana(
        IntPtr ventana,
        ContextoDistribucion distribucion,
        string? rutaEjecutable)
    {
        if (!distribucion.EsPortable)
        {
            return true;
        }

        if (ventana == IntPtr.Zero
            || string.IsNullOrWhiteSpace(rutaEjecutable)
            || rutaEjecutable.Contains('/')
            || !Path.IsPathFullyQualified(rutaEjecutable)
            || ServicioRutasSeguras.ContieneSegmentosNavegacion(rutaEjecutable))
        {
            return false;
        }

        var rutaCompleta = Path.GetFullPath(rutaEjecutable);
        if (!File.Exists(rutaCompleta))
        {
            return false;
        }

        var interfaz = typeof(IPropertyStore).GUID;
        var resultado = SHGetPropertyStoreForWindow(
            ventana,
            ref interfaz,
            out var propiedades);
        if (resultado < 0 || propiedades is null)
        {
            return false;
        }

        try
        {
            // Establece primero el relanzamiento y al final el identificador.
            return EstablecerCadena(
                    propiedades,
                    new PropertyKey(FormatoAppUserModel, 2),
                    CrearComandoRelanzamiento(rutaCompleta))
                && EstablecerCadena(
                    propiedades,
                    new PropertyKey(FormatoAppUserModel, 3),
                    $"{rutaCompleta},0")
                && EstablecerCadena(
                    propiedades,
                    new PropertyKey(FormatoAppUserModel, 4),
                    $"@{rutaCompleta},-{IdRecursoNombrePortable}")
                && EstablecerCadena(
                    propiedades,
                    new PropertyKey(FormatoAppUserModel, 5),
                    IdPortable)
                && propiedades.Commit() >= 0;
        }
        finally
        {
            Marshal.FinalReleaseComObject(propiedades);
        }
    }

    internal static string CrearComandoRelanzamiento(string rutaEjecutable)
    {
        return $"\"{rutaEjecutable.Replace("\"", "\\\"")}\"";
    }

    private static bool EstablecerCadena(
        IPropertyStore propiedades,
        PropertyKey clave,
        string valor)
    {
        var variante = PropVariant.DesdeCadena(valor);
        try
        {
            return propiedades.SetValue(ref clave, ref variante) >= 0;
        }
        finally
        {
            PropVariantClear(ref variante);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        string identificador);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr ventana,
        ref Guid interfaz,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propiedades);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variante);

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint cantidad);

        [PreserveSig]
        int GetAt(uint indice, out PropertyKey clave);

        [PreserveSig]
        int GetValue(ref PropertyKey clave, out PropVariant valor);

        [PreserveSig]
        int SetValue(ref PropertyKey clave, ref PropVariant valor);

        [PreserveSig]
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid formato, uint identificador)
    {
        public Guid Formato = formato;
        public uint Identificador = identificador;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Tipo;

        [FieldOffset(8)]
        public IntPtr Valor;

        public static PropVariant DesdeCadena(string valor)
        {
            return new PropVariant
            {
                Tipo = TipoCadenaUnicode,
                Valor = Marshal.StringToCoTaskMemUni(valor)
            };
        }
    }
}
