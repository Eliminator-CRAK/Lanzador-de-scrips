// (Autor: Alex Roman)
// Descripcion: Retira el MSI identificado por su hash utilizando el mismo handle protegido.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace LanzadorScripts.Servidor.Core;

internal static class RetiradaPaqueteSeguro
{
    internal static void Eliminar(string ruta, string hashEsperado)
    {
        // Impide reemplazar o modificar el archivo entre la comprobacion y su retirada.
        using var handle = CreateFile(ruta, 0x80010000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo abrir el paquete para retirarlo.");
        if (!GetFileInformationByHandleEx(handle, 0, out var atributos, Marshal.SizeOf<InformacionAtributos>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((atributos.Atributos & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("El paquete no puede ser un directorio ni un enlace.");
        using var archivo = new FileStream(handle, FileAccess.Read);
        var hash = Convert.ToHexString(SHA256.HashData(archivo));
        if (!string.Equals(hash, hashEsperado, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("El paquete ha cambiado. Actualice la lista antes de continuar.");
        var disposicion = new DisposicionArchivo { Eliminar = 1 };
        if (!SetFileInformationByHandle(handle, 4, ref disposicion, Marshal.SizeOf<DisposicionArchivo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows no pudo retirar el paquete.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InformacionAtributos { public long Creacion, Acceso, Escritura, Cambio; public uint Atributos; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DisposicionArchivo { public byte Eliminar; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string ruta, uint acceso, uint compartir, IntPtr seguridad, uint creacion, uint opciones, IntPtr plantilla);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle archivo, int clase, out InformacionAtributos datos, int longitud);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle archivo, int clase, ref DisposicionArchivo datos, int longitud);
}
