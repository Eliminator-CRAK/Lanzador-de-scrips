// (Autor: Alex Roman)
// Descripcion: Inicia la aplicacion portable dentro de una sesion temporal aislada.

#define WIN32_LEAN_AND_MEAN

#include <windows.h>
#include <aclapi.h>
#include <bcrypt.h>
#include <sddl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <tlhelp32.h>

#include <algorithm>
#include <cctype>
#include <cstdio>
#include <cwctype>
#include <string>
#include <utility>
#include <vector>

#include "RecursosLanzador.h"

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "user32.lib")

namespace
{
    constexpr DWORD TamanoBloque = 1024 * 1024;
    constexpr DWORD CodigoCierreDefinitivo = 42;
    constexpr ULONGLONG TiempoMaximoLimpiezaMs = 20000;
    constexpr wchar_t NombreMutexLimpieza[] = L"Global\\LanzadorScripts_PortableCleanup_v2";
    constexpr wchar_t NombreBloqueoUso[] = L".runtime-use.lock";
    std::wstring RutaLogNativo;

    // Transporta un mensaje seguro hasta la interfaz.
    class ErrorLanzador
    {
    public:
        explicit ErrorLanzador(std::wstring mensaje)
            : mensaje_(std::move(mensaje))
        {
        }

        const std::wstring& Mensaje() const
        {
            return mensaje_;
        }

    private:
        std::wstring mensaje_;
    };

    // Representa un recurso incluido en el EXE.
    struct Recurso
    {
        const BYTE* datos;
        DWORD longitud;
    };

    // Conserva las rutas y el bloqueo durante toda la ejecucion.
    struct EntornoPreparado
    {
        std::wstring raizPrograma;
        std::wstring raizSesiones;
        std::wstring rutaPayload;
        HANDLE bloqueoUso = INVALID_HANDLE_VALUE;
    };

    // Convierte un codigo de Windows en texto.
    std::wstring TextoErrorWindows(DWORD codigo)
    {
        wchar_t* buffer = nullptr;
        const DWORD longitud = FormatMessageW(
            FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
            nullptr,
            codigo,
            0,
            reinterpret_cast<wchar_t*>(&buffer),
            0,
            nullptr);
        std::wstring mensaje = longitud > 0 && buffer != nullptr
            ? std::wstring(buffer, longitud)
            : L"Error de Windows " + std::to_wstring(codigo);
        if (buffer != nullptr)
        {
            LocalFree(buffer);
        }

        while (!mensaje.empty() && std::iswspace(mensaje.back()))
        {
            mensaje.pop_back();
        }

        return mensaje;
    }

    // Genera un error con el ultimo resultado de Windows.
    [[noreturn]] void LanzarErrorWindows(const std::wstring& accion)
    {
        throw ErrorLanzador(accion + L": " + TextoErrorWindows(GetLastError()));
    }

    // Normaliza una ruta local.
    std::wstring ObtenerRutaCompleta(const std::wstring& ruta)
    {
        const DWORD requerida = GetFullPathNameW(ruta.c_str(), 0, nullptr, nullptr);
        if (requerida == 0)
        {
            LanzarErrorWindows(L"No se pudo normalizar una ruta local");
        }

        std::vector<wchar_t> buffer(requerida + 1);
        const DWORD escritos = GetFullPathNameW(
            ruta.c_str(),
            static_cast<DWORD>(buffer.size()),
            buffer.data(),
            nullptr);
        if (escritos == 0 || escritos >= buffer.size())
        {
            LanzarErrorWindows(L"No se pudo normalizar una ruta local");
        }

        std::wstring resultado(buffer.data(), escritos);
        while (resultado.size() > 3 && (resultado.back() == L'\\' || resultado.back() == L'/'))
        {
            resultado.pop_back();
        }

        return resultado;
    }

    // Resuelve la carpeta temporal antes de modificar TEMP y TMP.
    std::wstring ObtenerCarpetaTemporal()
    {
        const DWORD requerida = GetTempPathW(0, nullptr);
        if (requerida == 0)
        {
            LanzarErrorWindows(L"No se pudo resolver la carpeta temporal");
        }

        std::vector<wchar_t> buffer(requerida + 1);
        const DWORD escritos = GetTempPathW(
            static_cast<DWORD>(buffer.size()),
            buffer.data());
        if (escritos == 0 || escritos >= buffer.size())
        {
            LanzarErrorWindows(L"No se pudo resolver la carpeta temporal");
        }

        return ObtenerRutaCompleta(std::wstring(buffer.data(), escritos));
    }

    // Crea un nombre impredecible y validable para la sesion portable.
    std::wstring CrearNombreSesion()
    {
        GUID identificador{};
        if (FAILED(CoCreateGuid(&identificador)))
        {
            throw ErrorLanzador(L"No se pudo crear el identificador de la sesion portable.");
        }

        wchar_t texto[39]{};
        if (StringFromGUID2(identificador, texto, _countof(texto)) == 0)
        {
            throw ErrorLanzador(L"No se pudo convertir el identificador de la sesion portable.");
        }

        std::wstring hexadecimal;
        hexadecimal.reserve(32);
        for (const wchar_t caracter : std::wstring(texto))
        {
            if (std::iswxdigit(caracter) != 0)
            {
                hexadecimal.push_back(static_cast<wchar_t>(std::towlower(caracter)));
            }
        }

        if (hexadecimal.size() != 32)
        {
            throw ErrorLanzador(L"El identificador de la sesion portable no es valido.");
        }

        return L"Sesion-" + hexadecimal;
    }

    // Une dos segmentos de una ruta.
    std::wstring UnirRuta(const std::wstring& base, const std::wstring& nombre)
    {
        if (base.empty())
        {
            return nombre;
        }

        return base.back() == L'\\'
            ? base + nombre
            : base + L"\\" + nombre;
    }

    // Prepara una ruta absoluta para las API Win32 con soporte de rutas largas.
    std::wstring PrepararRutaWin32(const std::wstring& ruta)
    {
        if (ruta.rfind(L"\\\\?\\", 0) == 0)
        {
            return ruta;
        }

        if (ruta.rfind(L"\\\\", 0) == 0)
        {
            return L"\\\\?\\UNC\\" + ruta.substr(2);
        }

        return ruta.size() >= 3 && ruta[1] == L':' && ruta[2] == L'\\'
            ? L"\\\\?\\" + ruta
            : ruta;
    }

    // Convierte texto de Windows para escribir el log en UTF-8.
    std::string ConvertirUtf8(const std::wstring& texto)
    {
        if (texto.empty())
        {
            return {};
        }

        const int longitud = WideCharToMultiByte(
            CP_UTF8,
            WC_ERR_INVALID_CHARS,
            texto.c_str(),
            static_cast<int>(texto.size()),
            nullptr,
            0,
            nullptr,
            nullptr);
        if (longitud <= 0)
        {
            return {};
        }

        std::string resultado(static_cast<std::size_t>(longitud), '\0');
        if (WideCharToMultiByte(
                CP_UTF8,
                WC_ERR_INVALID_CHARS,
                texto.c_str(),
                static_cast<int>(texto.size()),
                resultado.data(),
                longitud,
                nullptr,
                nullptr) != longitud)
        {
            return {};
        }

        return resultado;
    }

    // Registra una fase del lanzador sin interrumpir su ejecucion.
    void RegistrarLog(const std::wstring& evento, const std::wstring& detalle = L"")
    {
        if (RutaLogNativo.empty())
        {
            return;
        }

        SYSTEMTIME ahora{};
        GetSystemTime(&ahora);
        wchar_t fecha[40]{};
        _snwprintf_s(
            fecha,
            _countof(fecha),
            _TRUNCATE,
            L"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
            ahora.wYear,
            ahora.wMonth,
            ahora.wDay,
            ahora.wHour,
            ahora.wMinute,
            ahora.wSecond,
            ahora.wMilliseconds);
        const std::wstring linea = std::wstring(fecha)
            + L" | "
            + evento
            + (detalle.empty() ? L"" : L" | " + detalle)
            + L"\r\n";
        const std::string utf8 = ConvertirUtf8(linea);
        if (utf8.empty())
        {
            return;
        }

        const std::wstring rutaLogWin32 = PrepararRutaWin32(RutaLogNativo);
        HANDLE archivo = CreateFileW(
            rutaLogWin32.c_str(),
            FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (archivo == INVALID_HANDLE_VALUE)
        {
            return;
        }

        DWORD escritos = 0;
        WriteFile(
            archivo,
            utf8.data(),
            static_cast<DWORD>(utf8.size()),
            &escritos,
            nullptr);
        CloseHandle(archivo);
    }

    // Comprueba que una ruta permanece dentro de una raiz.
    bool EmpiezaPorRuta(const std::wstring& ruta, const std::wstring& raiz)
    {
        if (ruta.size() < raiz.size() || _wcsnicmp(ruta.c_str(), raiz.c_str(), raiz.size()) != 0)
        {
            return false;
        }

        return ruta.size() == raiz.size() || ruta[raiz.size()] == L'\\';
    }

    // Serializa la preparacion y limpieza de runtimes entre sesiones.
    HANDLE AdquirirMutexLimpieza()
    {
        HANDLE mutex = CreateMutexW(nullptr, FALSE, NombreMutexLimpieza);
        if (mutex == nullptr)
        {
            RegistrarLog(L"runtime.mutex.error", TextoErrorWindows(GetLastError()));
            return nullptr;
        }

        const DWORD espera = WaitForSingleObject(mutex, 30000);
        if (espera != WAIT_OBJECT_0 && espera != WAIT_ABANDONED)
        {
            RegistrarLog(L"runtime.mutex.timeout");
            CloseHandle(mutex);
            return nullptr;
        }

        return mutex;
    }

    // Libera el mutex global de runtimes.
    void LiberarMutexLimpieza(HANDLE mutex)
    {
        if (mutex != nullptr)
        {
            ReleaseMutex(mutex);
            CloseHandle(mutex);
        }
    }

    // Abre el marcador compartido mientras una version esta en uso.
    HANDLE AbrirBloqueoUsoCompartido(const std::wstring& raizPrograma)
    {
        const std::wstring ruta = UnirRuta(raizPrograma, NombreBloqueoUso);
        const std::wstring rutaWin32 = PrepararRutaWin32(ruta);
        return CreateFileW(
            rutaWin32.c_str(),
            GENERIC_READ,
            FILE_SHARE_READ,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_HIDDEN,
            nullptr);
    }

    // Comprueba que ningun otro lanzador conserva el marcador abierto.
    bool BloqueoUsoDisponibleEnExclusiva(const std::wstring& raizPrograma)
    {
        const std::wstring ruta = UnirRuta(raizPrograma, NombreBloqueoUso);
        const std::wstring rutaWin32 = PrepararRutaWin32(ruta);
        HANDLE bloqueo = CreateFileW(
            rutaWin32.c_str(),
            GENERIC_READ | GENERIC_WRITE,
            0,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_HIDDEN,
            nullptr);
        if (bloqueo == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        CloseHandle(bloqueo);
        return true;
    }

    // Comprueba el formato cerrado de una carpeta de sesion portable.
    bool EsNombreSesionValido(const std::wstring& nombre)
    {
        constexpr wchar_t prefijo[] = L"Sesion-";
        constexpr std::size_t longitudPrefijo = _countof(prefijo) - 1;
        return nombre.size() == longitudPrefijo + 32
            && nombre.compare(0, longitudPrefijo, prefijo) == 0
            && std::all_of(
                nombre.begin() + static_cast<std::ptrdiff_t>(longitudPrefijo),
                nombre.end(),
                [](wchar_t valor) { return std::iswxdigit(valor) != 0; });
    }

    // Detecta procesos cuyo ejecutable pertenece a una ruta administrada.
    bool HayProcesoEnRuta(const std::wstring& ruta)
    {
        const std::wstring completa = ObtenerRutaCompleta(ruta);
        HANDLE captura = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (captura == INVALID_HANDLE_VALUE)
        {
            return true;
        }

        PROCESSENTRY32W entrada{};
        entrada.dwSize = sizeof(entrada);
        bool encontrado = false;
        if (Process32FirstW(captura, &entrada))
        {
            do
            {
                HANDLE proceso = OpenProcess(
                    PROCESS_QUERY_LIMITED_INFORMATION,
                    FALSE,
                    entrada.th32ProcessID);
                if (proceso == nullptr)
                {
                    continue;
                }

                std::vector<wchar_t> buffer(32768);
                DWORD longitud = static_cast<DWORD>(buffer.size());
                if (QueryFullProcessImageNameW(
                        proceso,
                        0,
                        buffer.data(),
                        &longitud))
                {
                    const std::wstring ejecutable = ObtenerRutaCompleta(
                        std::wstring(buffer.data(), longitud));
                    if (EmpiezaPorRuta(ejecutable, completa))
                    {
                        encontrado = true;
                    }
                }
                CloseHandle(proceso);
            } while (!encontrado && Process32NextW(captura, &entrada));
        }

        CloseHandle(captura);
        return encontrado;
    }

    // Finaliza auxiliares que siguen ejecutandose dentro de la sesion cerrada.
    void TerminarProcesosEnRuta(const std::wstring& ruta)
    {
        const std::wstring completa = ObtenerRutaCompleta(ruta);
        HANDLE captura = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (captura == INVALID_HANDLE_VALUE)
        {
            RegistrarLog(L"runtime.limpieza.captura_error");
            return;
        }

        PROCESSENTRY32W entrada{};
        entrada.dwSize = sizeof(entrada);
        if (Process32FirstW(captura, &entrada))
        {
            do
            {
                if (entrada.th32ProcessID == GetCurrentProcessId())
                {
                    continue;
                }

                HANDLE proceso = OpenProcess(
                    PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | SYNCHRONIZE,
                    FALSE,
                    entrada.th32ProcessID);
                if (proceso == nullptr)
                {
                    continue;
                }

                std::vector<wchar_t> buffer(32768);
                DWORD longitud = static_cast<DWORD>(buffer.size());
                if (QueryFullProcessImageNameW(proceso, 0, buffer.data(), &longitud))
                {
                    const std::wstring ejecutable = ObtenerRutaCompleta(
                        std::wstring(buffer.data(), longitud));
                    if (EmpiezaPorRuta(ejecutable, completa))
                    {
                        RegistrarLog(
                            L"runtime.limpieza.proceso_finalizado",
                            L"pid=" + std::to_wstring(entrada.th32ProcessID));
                        TerminateProcess(proceso, ERROR_PROCESS_ABORTED);
                        WaitForSingleObject(proceso, 3000);
                    }
                }

                CloseHandle(proceso);
            } while (Process32NextW(captura, &entrada));
        }

        CloseHandle(captura);
    }

    // Elimina un arbol sin atravesar enlaces ni salir de la raiz autorizada.
    bool EliminarArbolSeguroUnaVez(
        const std::wstring& ruta,
        const std::wstring& raizAutorizada)
    {
        const std::wstring completa = ObtenerRutaCompleta(ruta);
        const std::wstring raiz = ObtenerRutaCompleta(raizAutorizada);
        if (_wcsicmp(completa.c_str(), raiz.c_str()) == 0
            || !EmpiezaPorRuta(completa, raiz))
        {
            RegistrarLog(L"runtime.limpieza.ruta_rechazada", completa);
            return false;
        }

        const std::wstring completaWin32 = PrepararRutaWin32(completa);
        const DWORD atributos = GetFileAttributesW(completaWin32.c_str());
        if (atributos == INVALID_FILE_ATTRIBUTES)
        {
            return GetLastError() == ERROR_FILE_NOT_FOUND
                || GetLastError() == ERROR_PATH_NOT_FOUND;
        }

        if ((atributos & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            // Elimina el enlace sin acceder a la ubicacion que referencia.
            const BOOL eliminado = (atributos & FILE_ATTRIBUTE_DIRECTORY) != 0
                ? RemoveDirectoryW(completaWin32.c_str())
                : DeleteFileW(completaWin32.c_str());
            if (eliminado != FALSE)
            {
                RegistrarLog(L"runtime.limpieza.reparse_retirado", completa);
                return true;
            }

            return false;
        }

        if ((atributos & FILE_ATTRIBUTE_DIRECTORY) == 0)
        {
            SetFileAttributesW(completaWin32.c_str(), FILE_ATTRIBUTE_NORMAL);
            return DeleteFileW(completaWin32.c_str()) != FALSE;
        }

        WIN32_FIND_DATAW datos{};
        const std::wstring patronWin32 = PrepararRutaWin32(UnirRuta(completa, L"*"));
        HANDLE busqueda = FindFirstFileW(patronWin32.c_str(), &datos);
        if (busqueda != INVALID_HANDLE_VALUE)
        {
            bool correcto = true;
            do
            {
                const std::wstring nombre(datos.cFileName);
                if (nombre == L"." || nombre == L"..")
                {
                    continue;
                }

                const std::wstring elemento = UnirRuta(completa, nombre);
                if (!EliminarArbolSeguroUnaVez(elemento, raiz))
                {
                    correcto = false;
                    break;
                }
            } while (FindNextFileW(busqueda, &datos));

            FindClose(busqueda);
            if (!correcto)
            {
                return false;
            }
        }
        else if (GetLastError() != ERROR_FILE_NOT_FOUND)
        {
            return false;
        }

        SetFileAttributesW(completaWin32.c_str(), FILE_ATTRIBUTE_NORMAL);
        return RemoveDirectoryW(completaWin32.c_str()) != FALSE;
    }

    // Reintenta bloqueos transitorios durante un tiempo limitado.
    bool EliminarArbolSeguroConReintentos(
        const std::wstring& ruta,
        const std::wstring& raizAutorizada)
    {
        const ULONGLONG limite = GetTickCount64() + TiempoMaximoLimpiezaMs;
        do
        {
            if (EliminarArbolSeguroUnaVez(ruta, raizAutorizada))
            {
                return true;
            }

            Sleep(250);
        } while (GetTickCount64() < limite);

        RegistrarLog(L"runtime.limpieza.bloqueada", ruta);
        return false;
    }

    // Retira sesiones portables abandonadas sin tocar una sesion activa.
    void LimpiarSesionesAbandonadas(const std::wstring& raizSesiones)
    {
        WIN32_FIND_DATAW datos{};
        const std::wstring patronWin32 = PrepararRutaWin32(
            UnirRuta(raizSesiones, L"Sesion-*"));
        HANDLE busqueda = FindFirstFileW(patronWin32.c_str(), &datos);
        if (busqueda == INVALID_HANDLE_VALUE)
        {
            return;
        }

        do
        {
            const std::wstring nombre(datos.cFileName);
            if ((datos.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0
                || (datos.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0
                || !EsNombreSesionValido(nombre))
            {
                continue;
            }

            const std::wstring sesion = UnirRuta(raizSesiones, nombre);
            if (!HayProcesoEnRuta(sesion)
                && BloqueoUsoDisponibleEnExclusiva(sesion))
            {
                EliminarArbolSeguroConReintentos(sesion, raizSesiones);
            }
        } while (FindNextFileW(busqueda, &datos));

        FindClose(busqueda);
    }

    // Rechaza archivos y redirecciones en la ruta preparada.
    void ValidarSinPuntosReanalisis(const std::wstring& ruta, const std::wstring& raizConfiable)
    {
        const std::wstring completa = ObtenerRutaCompleta(ruta);
        const std::wstring raiz = ObtenerRutaCompleta(raizConfiable);
        if (!EmpiezaPorRuta(completa, raiz))
        {
            throw ErrorLanzador(L"La carpeta local esta fuera de la raiz permitida: " + completa);
        }

        const auto validarDirectorio = [](const std::wstring& directorio)
        {
            const std::wstring directorioWin32 = PrepararRutaWin32(directorio);
            const DWORD atributos = GetFileAttributesW(directorioWin32.c_str());
            if (atributos == INVALID_FILE_ATTRIBUTES)
            {
                LanzarErrorWindows(L"No se pudo comprobar la carpeta local " + directorio);
            }

            if ((atributos & FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                throw ErrorLanzador(L"La ruta local no es una carpeta: " + directorio);
            }

            if ((atributos & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            {
                throw ErrorLanzador(L"La carpeta local contiene un punto de reanalisis no permitido: " + directorio);
            }
        };

        validarDirectorio(raiz);
        std::size_t posicion = raiz.size();
        while (posicion < completa.size())
        {
            while (posicion < completa.size() && completa[posicion] == L'\\')
            {
                ++posicion;
            }

            const std::size_t separador = completa.find(L'\\', posicion);
            const std::wstring parcial = separador == std::wstring::npos
                ? completa
                : completa.substr(0, separador);
            validarDirectorio(parcial);

            if (separador == std::wstring::npos)
            {
                break;
            }

            posicion = separador + 1;
        }
    }

    // Crea una carpeta y valida su ruta completa.
    void CrearDirectorioSeguro(const std::wstring& ruta, const std::wstring& raizConfiable)
    {
        const int resultado = SHCreateDirectoryExW(nullptr, ruta.c_str(), nullptr);
        if (resultado != ERROR_SUCCESS && resultado != ERROR_ALREADY_EXISTS && resultado != ERROR_FILE_EXISTS)
        {
            SetLastError(static_cast<DWORD>(resultado));
            LanzarErrorWindows(L"No se pudo crear la carpeta local " + ruta);
        }

        ValidarSinPuntosReanalisis(ruta, raizConfiable);
    }

    // Comprueba la eliminacion real de un arbol que supera MAX_PATH.
    bool ValidarLimpiezaRutaLarga()
    {
        const std::wstring raizTemporal = ObtenerCarpetaTemporal();
        const std::wstring raizPrueba = UnirRuta(
            raizTemporal,
            L"LanzadorScripts-Prueba-" + CrearNombreSesion().substr(7));
        std::wstring directorio = raizPrueba;
        const auto crearDirectorio = [](const std::wstring& ruta)
        {
            const std::wstring rutaWin32 = PrepararRutaWin32(ruta);
            return CreateDirectoryW(rutaWin32.c_str(), nullptr) != FALSE
                || GetLastError() == ERROR_ALREADY_EXISTS;
        };

        if (!crearDirectorio(raizPrueba))
        {
            return false;
        }

        while (UnirRuta(directorio, L"archivo-prueba-ruta-larga.dat").size() <= 280)
        {
            directorio = UnirRuta(directorio, L"segmento-ruta-larga-0123456789");
            if (!crearDirectorio(directorio))
            {
                EliminarArbolSeguroConReintentos(raizPrueba, raizTemporal);
                return false;
            }
        }

        const std::wstring rutaArchivo = UnirRuta(
            directorio,
            L"archivo-prueba-ruta-larga.dat");
        const std::wstring rutaArchivoWin32 = PrepararRutaWin32(rutaArchivo);
        HANDLE archivo = CreateFileW(
            rutaArchivoWin32.c_str(),
            GENERIC_WRITE,
            0,
            nullptr,
            CREATE_NEW,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (archivo == INVALID_HANDLE_VALUE)
        {
            EliminarArbolSeguroConReintentos(raizPrueba, raizTemporal);
            return false;
        }

        constexpr BYTE Contenido[] = { 0x4C, 0x53 };
        DWORD escritos = 0;
        const BOOL escrituraCorrecta = WriteFile(
            archivo,
            Contenido,
            static_cast<DWORD>(sizeof(Contenido)),
            &escritos,
            nullptr);
        CloseHandle(archivo);
        if (escrituraCorrecta == FALSE
            || escritos != static_cast<DWORD>(sizeof(Contenido))
            || rutaArchivo.size() <= MAX_PATH)
        {
            EliminarArbolSeguroConReintentos(raizPrueba, raizTemporal);
            return false;
        }

        if (!EliminarArbolSeguroConReintentos(raizPrueba, raizTemporal))
        {
            return false;
        }

        const std::wstring raizPruebaWin32 = PrepararRutaWin32(raizPrueba);
        const DWORD atributos = GetFileAttributesW(raizPruebaWin32.c_str());
        const DWORD error = GetLastError();
        return atributos == INVALID_FILE_ATTRIBUTES
            && (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND);
    }

    // Comprueba si la linea de comandos contiene una unica accion interna.
    bool EsAccionInterna(const wchar_t* accion)
    {
        int cantidad = 0;
        LPWSTR* argumentos = CommandLineToArgvW(GetCommandLineW(), &cantidad);
        if (argumentos == nullptr)
        {
            return false;
        }

        const bool coincide = cantidad == 2 && wcscmp(argumentos[1], accion) == 0;
        LocalFree(argumentos);
        return coincide;
    }

    // Obtiene el SID de la identidad que abre la aplicacion.
    std::wstring ObtenerSidUsuario()
    {
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
        {
            LanzarErrorWindows(L"No se pudo identificar al usuario");
        }

        DWORD longitud = 0;
        GetTokenInformation(token, TokenUser, nullptr, 0, &longitud);
        if (longitud == 0)
        {
            const DWORD error = GetLastError();
            CloseHandle(token);
            SetLastError(error);
            LanzarErrorWindows(L"No se pudo leer la identidad del usuario");
        }

        std::vector<BYTE> buffer(longitud);
        if (!GetTokenInformation(token, TokenUser, buffer.data(), longitud, &longitud))
        {
            const DWORD error = GetLastError();
            CloseHandle(token);
            SetLastError(error);
            LanzarErrorWindows(L"No se pudo leer la identidad del usuario");
        }

        CloseHandle(token);
        const auto usuario = reinterpret_cast<TOKEN_USER*>(buffer.data());
        LPWSTR sidTexto = nullptr;
        if (!ConvertSidToStringSidW(usuario->User.Sid, &sidTexto) || sidTexto == nullptr)
        {
            LanzarErrorWindows(L"No se pudo convertir la identidad del usuario");
        }

        std::wstring resultado(sidTexto);
        LocalFree(sidTexto);
        return resultado;
    }

    // Aplica propietario y permisos a una carpeta.
    void AplicarSeguridad(const std::wstring& ruta, const std::wstring& sddl)
    {
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl.c_str(),
                SDDL_REVISION_1,
                &descriptor,
                nullptr))
        {
            LanzarErrorWindows(L"No se pudo preparar la seguridad de " + ruta);
        }

        PSID propietario = nullptr;
        BOOL propietarioPredeterminado = FALSE;
        PACL dacl = nullptr;
        BOOL daclPresente = FALSE;
        BOOL daclPredeterminada = FALSE;
        if (!GetSecurityDescriptorOwner(descriptor, &propietario, &propietarioPredeterminado)
            || !GetSecurityDescriptorDacl(descriptor, &daclPresente, &dacl, &daclPredeterminada)
            || !daclPresente)
        {
            LocalFree(descriptor);
            throw ErrorLanzador(L"No se pudo leer la seguridad preparada para " + ruta);
        }

        std::wstring rutaWin32 = PrepararRutaWin32(ruta);
        const DWORD resultado = SetNamedSecurityInfoW(
            rutaWin32.data(),
            SE_FILE_OBJECT,
            OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
            propietario,
            nullptr,
            dacl,
            nullptr);
        LocalFree(descriptor);
        if (resultado != ERROR_SUCCESS)
        {
            SetLastError(resultado);
            LanzarErrorWindows(L"No se pudieron aplicar permisos seguros a " + ruta);
        }
    }

    // Calcula SHA-256 sobre un bloque de memoria.
    std::vector<BYTE> CalcularSha256(const BYTE* datos, std::size_t longitud)
    {
        BCRYPT_ALG_HANDLE algoritmo = nullptr;
        BCRYPT_HASH_HANDLE hash = nullptr;
        DWORD longitudObjeto = 0;
        DWORD longitudHash = 0;
        DWORD escritos = 0;

        if (BCryptOpenAlgorithmProvider(&algoritmo, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0
            || BCryptGetProperty(
                   algoritmo,
                   BCRYPT_OBJECT_LENGTH,
                   reinterpret_cast<PUCHAR>(&longitudObjeto),
                   sizeof(longitudObjeto),
                   &escritos,
                   0) < 0
            || BCryptGetProperty(
                   algoritmo,
                   BCRYPT_HASH_LENGTH,
                   reinterpret_cast<PUCHAR>(&longitudHash),
                   sizeof(longitudHash),
                   &escritos,
                   0) < 0)
        {
            if (algoritmo != nullptr)
            {
                BCryptCloseAlgorithmProvider(algoritmo, 0);
            }

            throw ErrorLanzador(L"No se pudo iniciar la validacion SHA-256.");
        }

        std::vector<BYTE> objeto(longitudObjeto);
        std::vector<BYTE> resultado(longitudHash);
        if (BCryptCreateHash(algoritmo, &hash, objeto.data(), longitudObjeto, nullptr, 0, 0) < 0)
        {
            BCryptCloseAlgorithmProvider(algoritmo, 0);
            throw ErrorLanzador(L"No se pudo iniciar la validacion SHA-256.");
        }

        std::size_t posicion = 0;
        while (posicion < longitud)
        {
            const ULONG bloque = static_cast<ULONG>(std::min<std::size_t>(TamanoBloque, longitud - posicion));
            if (BCryptHashData(hash, const_cast<PUCHAR>(datos + posicion), bloque, 0) < 0)
            {
                BCryptDestroyHash(hash);
                BCryptCloseAlgorithmProvider(algoritmo, 0);
                throw ErrorLanzador(L"No se pudo calcular la validacion SHA-256.");
            }

            posicion += bloque;
        }

        if (BCryptFinishHash(hash, resultado.data(), longitudHash, 0) < 0)
        {
            BCryptDestroyHash(hash);
            BCryptCloseAlgorithmProvider(algoritmo, 0);
            throw ErrorLanzador(L"No se pudo finalizar la validacion SHA-256.");
        }

        BCryptDestroyHash(hash);
        BCryptCloseAlgorithmProvider(algoritmo, 0);
        return resultado;
    }

    // Convierte bytes en texto hexadecimal.
    std::wstring ConvertirHexadecimal(const std::vector<BYTE>& datos)
    {
        static constexpr wchar_t Hexadecimal[] = L"0123456789ABCDEF";
        std::wstring resultado;
        resultado.reserve(datos.size() * 2);
        for (const BYTE valor : datos)
        {
            resultado.push_back(Hexadecimal[(valor >> 4) & 0x0F]);
            resultado.push_back(Hexadecimal[valor & 0x0F]);
        }

        return resultado;
    }

    // Calcula SHA-256 sobre un archivo local.
    std::wstring CalcularHashArchivo(const std::wstring& ruta)
    {
        const std::wstring rutaWin32 = PrepararRutaWin32(ruta);
        HANDLE archivo = CreateFileW(
            rutaWin32.c_str(),
            GENERIC_READ,
            FILE_SHARE_READ,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
            nullptr);
        if (archivo == INVALID_HANDLE_VALUE)
        {
            LanzarErrorWindows(L"No se pudo abrir el componente interno");
        }

        BCRYPT_ALG_HANDLE algoritmo = nullptr;
        BCRYPT_HASH_HANDLE hash = nullptr;
        DWORD longitudObjeto = 0;
        DWORD longitudHash = 0;
        DWORD escritos = 0;
        if (BCryptOpenAlgorithmProvider(&algoritmo, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0
            || BCryptGetProperty(
                   algoritmo,
                   BCRYPT_OBJECT_LENGTH,
                   reinterpret_cast<PUCHAR>(&longitudObjeto),
                   sizeof(longitudObjeto),
                   &escritos,
                   0) < 0
            || BCryptGetProperty(
                   algoritmo,
                   BCRYPT_HASH_LENGTH,
                   reinterpret_cast<PUCHAR>(&longitudHash),
                   sizeof(longitudHash),
                   &escritos,
                   0) < 0)
        {
            CloseHandle(archivo);
            if (algoritmo != nullptr)
            {
                BCryptCloseAlgorithmProvider(algoritmo, 0);
            }

            throw ErrorLanzador(L"No se pudo iniciar la validacion del componente interno.");
        }

        std::vector<BYTE> objeto(longitudObjeto);
        std::vector<BYTE> resultado(longitudHash);
        std::vector<BYTE> buffer(TamanoBloque);
        if (BCryptCreateHash(algoritmo, &hash, objeto.data(), longitudObjeto, nullptr, 0, 0) < 0)
        {
            CloseHandle(archivo);
            BCryptCloseAlgorithmProvider(algoritmo, 0);
            throw ErrorLanzador(L"No se pudo iniciar la validacion del componente interno.");
        }

        DWORD leidos = 0;
        DWORD errorLectura = ERROR_SUCCESS;
        while (true)
        {
            if (!ReadFile(archivo, buffer.data(), static_cast<DWORD>(buffer.size()), &leidos, nullptr))
            {
                errorLectura = GetLastError();
                break;
            }

            if (leidos == 0)
            {
                break;
            }

            if (BCryptHashData(hash, buffer.data(), leidos, 0) < 0)
            {
                CloseHandle(archivo);
                BCryptDestroyHash(hash);
                BCryptCloseAlgorithmProvider(algoritmo, 0);
                throw ErrorLanzador(L"No se pudo validar el componente interno.");
            }
        }

        CloseHandle(archivo);
        if (errorLectura != ERROR_SUCCESS)
        {
            BCryptDestroyHash(hash);
            BCryptCloseAlgorithmProvider(algoritmo, 0);
            SetLastError(errorLectura);
            LanzarErrorWindows(L"No se pudo leer el componente interno");
        }

        if (BCryptFinishHash(hash, resultado.data(), longitudHash, 0) < 0)
        {
            BCryptDestroyHash(hash);
            BCryptCloseAlgorithmProvider(algoritmo, 0);
            throw ErrorLanzador(L"No se pudo finalizar la validacion del componente interno.");
        }

        BCryptDestroyHash(hash);
        BCryptCloseAlgorithmProvider(algoritmo, 0);
        return ConvertirHexadecimal(resultado);
    }

    // Lee un recurso binario del ejecutable actual.
    Recurso ObtenerRecurso(WORD identificador)
    {
        HRSRC recurso = FindResourceW(nullptr, MAKEINTRESOURCEW(identificador), RT_RCDATA);
        if (recurso == nullptr)
        {
            LanzarErrorWindows(L"No se encontro un recurso interno de la aplicacion");
        }

        const DWORD longitud = SizeofResource(nullptr, recurso);
        HGLOBAL cargado = LoadResource(nullptr, recurso);
        const void* datos = cargado == nullptr ? nullptr : LockResource(cargado);
        if (longitud == 0 || datos == nullptr)
        {
            LanzarErrorWindows(L"No se pudo leer un recurso interno de la aplicacion");
        }

        return Recurso{ static_cast<const BYTE*>(datos), longitud };
    }

    // Lee y valida el hash publicado junto al payload.
    std::wstring LeerHashEsperado()
    {
        const Recurso recurso = ObtenerRecurso(IDR_HASH_APLICACION_DOTNET);
        std::string texto(
            reinterpret_cast<const char*>(recurso.datos),
            reinterpret_cast<const char*>(recurso.datos) + recurso.longitud);
        texto.erase(
            std::remove_if(
                texto.begin(),
                texto.end(),
                [](unsigned char valor) { return std::isspace(valor) != 0; }),
            texto.end());
        if (texto.size() != 64
            || !std::all_of(
                texto.begin(),
                texto.end(),
                [](unsigned char valor) { return std::isxdigit(valor) != 0; }))
        {
            throw ErrorLanzador(L"El hash interno de la aplicacion no es valido.");
        }

        std::wstring resultado(texto.begin(), texto.end());
        std::transform(
            resultado.begin(),
            resultado.end(),
            resultado.begin(),
            [](wchar_t valor) { return static_cast<wchar_t>(std::towupper(valor)); });
        return resultado;
    }

    // Comprueba tamano y hash del payload ya extraido.
    bool ArchivoCoincide(
        const std::wstring& ruta,
        const Recurso& payload,
        const std::wstring& hashEsperado)
    {
        WIN32_FILE_ATTRIBUTE_DATA datos{};
        const std::wstring rutaWin32 = PrepararRutaWin32(ruta);
        if (!GetFileAttributesExW(rutaWin32.c_str(), GetFileExInfoStandard, &datos)
            || (datos.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0
            || (datos.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        {
            return false;
        }

        ULARGE_INTEGER longitud{};
        longitud.HighPart = datos.nFileSizeHigh;
        longitud.LowPart = datos.nFileSizeLow;
        return longitud.QuadPart == payload.longitud
            && _wcsicmp(CalcularHashArchivo(ruta).c_str(), hashEsperado.c_str()) == 0;
    }

    // Publica el payload mediante sustitucion atomica.
    void ExtraerPayload(
        const std::wstring& destino,
        const Recurso& payload,
        const std::wstring& hashEsperado)
    {
        if (ArchivoCoincide(destino, payload, hashEsperado))
        {
            return;
        }

        const std::wstring temporal = destino
            + L"."
            + std::to_wstring(GetCurrentProcessId())
            + L".tmp";
        const std::wstring temporalWin32 = PrepararRutaWin32(temporal);
        const std::wstring destinoWin32 = PrepararRutaWin32(destino);
        DeleteFileW(temporalWin32.c_str());
        HANDLE archivo = CreateFileW(
            temporalWin32.c_str(),
            GENERIC_WRITE,
            0,
            nullptr,
            CREATE_NEW,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
            nullptr);
        if (archivo == INVALID_HANDLE_VALUE)
        {
            LanzarErrorWindows(L"No se pudo crear el componente interno");
        }

        DWORD posicion = 0;
        bool correcto = true;
        while (posicion < payload.longitud)
        {
            const DWORD bloque = std::min<DWORD>(TamanoBloque, payload.longitud - posicion);
            DWORD escritos = 0;
            if (!WriteFile(archivo, payload.datos + posicion, bloque, &escritos, nullptr) || escritos != bloque)
            {
                correcto = false;
                break;
            }

            posicion += escritos;
        }

        if (correcto)
        {
            correcto = FlushFileBuffers(archivo) != FALSE;
        }

        const DWORD errorEscritura = GetLastError();
        CloseHandle(archivo);
        if (!correcto)
        {
            DeleteFileW(temporalWin32.c_str());
            SetLastError(errorEscritura);
            LanzarErrorWindows(L"No se pudo guardar el componente interno");
        }

        if (_wcsicmp(CalcularHashArchivo(temporal).c_str(), hashEsperado.c_str()) != 0)
        {
            DeleteFileW(temporalWin32.c_str());
            throw ErrorLanzador(L"El componente interno extraido no supera la validacion SHA-256.");
        }

        if (!MoveFileExW(
                temporalWin32.c_str(),
                destinoWin32.c_str(),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        {
            const DWORD error = GetLastError();
            DeleteFileW(temporalWin32.c_str());
            SetLastError(error);
            LanzarErrorWindows(L"No se pudo publicar el componente interno");
        }
    }

    // Escapa un argumento para CreateProcess.
    std::wstring CitarArgumento(const std::wstring& argumento)
    {
        if (argumento.empty())
        {
            return L"\"\"";
        }

        if (argumento.find_first_of(L" \t\n\v\"") == std::wstring::npos)
        {
            return argumento;
        }

        std::wstring resultado = L"\"";
        std::size_t barras = 0;
        for (const wchar_t caracter : argumento)
        {
            if (caracter == L'\\')
            {
                ++barras;
                continue;
            }

            if (caracter == L'"')
            {
                resultado.append(barras * 2 + 1, L'\\');
                resultado.push_back(L'"');
                barras = 0;
                continue;
            }

            resultado.append(barras, L'\\');
            barras = 0;
            resultado.push_back(caracter);
        }

        resultado.append(barras * 2, L'\\');
        resultado.push_back(L'"');
        return resultado;
    }

    // Conserva los argumentos recibidos por el EXE exterior.
    std::wstring ObtenerArgumentos()
    {
        int cantidad = 0;
        LPWSTR* argumentos = CommandLineToArgvW(GetCommandLineW(), &cantidad);
        if (argumentos == nullptr)
        {
            LanzarErrorWindows(L"No se pudieron leer los argumentos de inicio");
        }

        std::wstring resultado;
        for (int indice = 1; indice < cantidad; ++indice)
        {
            resultado += L" ";
            resultado += CitarArgumento(argumentos[indice]);
        }

        LocalFree(argumentos);
        return resultado;
    }

    // Obtiene la ruta del EXE distribuido.
    std::wstring ObtenerRutaEjecutableActual()
    {
        std::vector<wchar_t> buffer(32768);
        const DWORD longitud = GetModuleFileNameW(
            nullptr,
            buffer.data(),
            static_cast<DWORD>(buffer.size()));
        if (longitud == 0 || longitud >= buffer.size())
        {
            LanzarErrorWindows(L"No se pudo resolver el ejecutable distribuido");
        }

        return ObtenerRutaCompleta(std::wstring(buffer.data(), longitud));
    }

    // Inicia el componente .NET y devuelve su codigo final.
    DWORD EjecutarAplicacion(const std::wstring& ejecutable)
    {
        std::wstring comando = CitarArgumento(ejecutable) + ObtenerArgumentos();
        std::vector<wchar_t> buffer(comando.begin(), comando.end());
        buffer.push_back(L'\0');

        STARTUPINFOW inicio{};
        inicio.cb = sizeof(inicio);
        PROCESS_INFORMATION proceso{};
        if (!CreateProcessW(
                ejecutable.c_str(),
                buffer.data(),
                nullptr,
                nullptr,
                FALSE,
                CREATE_NO_WINDOW,
                nullptr,
                nullptr,
                &inicio,
                &proceso))
        {
            LanzarErrorWindows(L"No se pudo iniciar LanzadorScripts");
        }

        CloseHandle(proceso.hThread);
        RegistrarLog(
            L"proceso.dotnet.iniciado",
            L"pid=" + std::to_wstring(proceso.dwProcessId));
        const DWORD espera = WaitForSingleObject(proceso.hProcess, INFINITE);
        if (espera != WAIT_OBJECT_0)
        {
            const DWORD error = espera == WAIT_FAILED ? GetLastError() : ERROR_GEN_FAILURE;
            CloseHandle(proceso.hProcess);
            SetLastError(error);
            LanzarErrorWindows(L"No se pudo esperar al cierre de LanzadorScripts");
        }

        DWORD codigo = ERROR_GEN_FAILURE;
        if (!GetExitCodeProcess(proceso.hProcess, &codigo))
        {
            const DWORD error = GetLastError();
            CloseHandle(proceso.hProcess);
            SetLastError(error);
            LanzarErrorWindows(L"No se pudo leer el resultado de LanzadorScripts");
        }

        CloseHandle(proceso.hProcess);
        RegistrarLog(
            L"proceso.dotnet.finalizado",
            L"codigo=" + std::to_wstring(codigo));
        return codigo;
    }

    // Prepara una sesion temporal privada antes de iniciar .NET.
    EntornoPreparado PrepararEntorno()
    {
        EntornoPreparado entorno;
        const std::wstring raizTemporal = ObtenerCarpetaTemporal();
        const std::wstring raizLanzador = UnirRuta(raizTemporal, L"LanzadorScripts");
        const std::wstring raizSesiones = UnirRuta(raizLanzador, L"Portable");
        CrearDirectorioSeguro(raizLanzador, raizTemporal);
        CrearDirectorioSeguro(raizSesiones, raizTemporal);

        HANDLE mutexLimpieza = AdquirirMutexLimpieza();
        if (mutexLimpieza != nullptr)
        {
            LimpiarSesionesAbandonadas(raizSesiones);
            LiberarMutexLimpieza(mutexLimpieza);
        }

        const std::wstring raizSesion = UnirRuta(raizSesiones, CrearNombreSesion());
        CrearDirectorioSeguro(raizSesion, raizSesiones);
        const std::wstring sid = ObtenerSidUsuario();
        AplicarSeguridad(
            raizSesion,
            L"O:" + sid + L"D:P(A;OICI;FA;;;" + sid + L")(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)");

        const std::wstring aplicacion = UnirRuta(raizSesion, L"Aplicacion");
        const std::wstring dotnet = UnirRuta(raizSesion, L"Runtimes\\DotNet");
        const std::wstring temporales = UnirRuta(raizSesion, L"Temporales");
        const std::wstring logs = UnirRuta(raizSesion, L"Logs");
        CrearDirectorioSeguro(aplicacion, raizSesion);
        CrearDirectorioSeguro(dotnet, raizSesion);
        CrearDirectorioSeguro(temporales, raizSesion);
        CrearDirectorioSeguro(logs, raizSesion);
        RutaLogNativo = UnirRuta(logs, L"lanzador-nativo.log");
        RegistrarLog(L"runtime.preparacion.inicio", L"variante=portable");

        entorno.bloqueoUso = AbrirBloqueoUsoCompartido(raizSesion);
        if (entorno.bloqueoUso == INVALID_HANDLE_VALUE)
        {
            LanzarErrorWindows(L"No se pudo bloquear la sesion portable");
        }

        const std::wstring rutaPayload = UnirRuta(
            aplicacion,
            L"LanzadorScripts.Runtime.exe");
        if (!SetEnvironmentVariableW(L"DOTNET_BUNDLE_EXTRACT_BASE_DIR", dotnet.c_str())
            || !SetEnvironmentVariableW(L"TEMP", temporales.c_str())
            || !SetEnvironmentVariableW(L"TMP", temporales.c_str())
            || !SetEnvironmentVariableW(
                L"LANZADOR_DISTRIBUTION_EXE",
                ObtenerRutaEjecutableActual().c_str())
            || !SetEnvironmentVariableW(L"LANZADOR_VARIANTE", L"portable")
            || !SetEnvironmentVariableW(L"LANZADOR_PORTABLE_ROOT", raizSesion.c_str())
            || !SetEnvironmentVariableW(
                L"LANZADOR_PORTABLE_SESSIONS_ROOT",
                raizSesiones.c_str()))
        {
            LanzarErrorWindows(L"No se pudo preparar el entorno portable seguro");
        }

        entorno.raizPrograma = raizSesion;
        entorno.raizSesiones = raizSesiones;
        entorno.rutaPayload = rutaPayload;
        RegistrarLog(L"runtime.preparacion.correcta", rutaPayload);
        return entorno;
    }

    // Espera un tiempo limitado a que terminen procesos auxiliares.
    bool EsperarRutaSinProcesos(const std::wstring& ruta, ULONGLONG tiempoMaximoMs)
    {
        const ULONGLONG limite = GetTickCount64() + tiempoMaximoMs;
        do
        {
            if (!HayProcesoEnRuta(ruta))
            {
                return true;
            }

            Sleep(250);
        } while (GetTickCount64() < limite);

        return false;
    }

    // Elimina la sesion portable despues de cualquier salida del proceso hijo.
    void LimpiarDespuesDelCierre(EntornoPreparado& entorno)
    {
        RegistrarLog(L"runtime.limpieza_final.inicio", L"variante=portable");

        HANDLE mutexLimpieza = AdquirirMutexLimpieza();
        if (mutexLimpieza == nullptr)
        {
            if (entorno.bloqueoUso != INVALID_HANDLE_VALUE)
            {
                CloseHandle(entorno.bloqueoUso);
                entorno.bloqueoUso = INVALID_HANDLE_VALUE;
            }

            RegistrarLog(L"runtime.limpieza_final.omitida_sin_mutex");
            return;
        }

        if (entorno.bloqueoUso != INVALID_HANDLE_VALUE)
        {
            CloseHandle(entorno.bloqueoUso);
            entorno.bloqueoUso = INVALID_HANDLE_VALUE;
        }

        if (!BloqueoUsoDisponibleEnExclusiva(entorno.raizPrograma))
        {
            RegistrarLog(L"runtime.limpieza_final.omitida_otra_sesion");
            LiberarMutexLimpieza(mutexLimpieza);
            return;
        }

        if (!EsperarRutaSinProcesos(entorno.raizPrograma, 3000))
        {
            TerminarProcesosEnRuta(entorno.raizPrograma);
        }

        if (EsperarRutaSinProcesos(entorno.raizPrograma, 5000))
        {
            EliminarArbolSeguroConReintentos(
                entorno.raizPrograma,
                entorno.raizSesiones);
        }
        else
        {
            RegistrarLog(L"runtime.limpieza_final.procesos_activos");
        }

        LiberarMutexLimpieza(mutexLimpieza);
        const std::wstring sesionesWin32 = PrepararRutaWin32(entorno.raizSesiones);
        RemoveDirectoryW(sesionesWin32.c_str());
        const std::size_t separador = entorno.raizSesiones.find_last_of(L"\\/");
        if (separador != std::wstring::npos)
        {
            const std::wstring lanzadorWin32 = PrepararRutaWin32(
                entorno.raizSesiones.substr(0, separador));
            RemoveDirectoryW(lanzadorWin32.c_str());
        }
    }
}

// Valida, extrae e inicia la aplicacion embebida.
int WINAPI wWinMain(
    _In_ HINSTANCE,
    _In_opt_ HINSTANCE,
    _In_ LPWSTR,
    _In_ int)
{
    if (EsAccionInterna(L"--validar-limpieza-ruta-larga"))
    {
        return ValidarLimpiezaRutaLarga() ? 0 : 1;
    }

    EntornoPreparado entorno;
    bool entornoPreparado = false;
    try
    {
        const Recurso payload = ObtenerRecurso(IDR_APLICACION_DOTNET);
        const std::wstring hashEsperado = LeerHashEsperado();
        const std::wstring hashEmbebido = ConvertirHexadecimal(
            CalcularSha256(payload.datos, payload.longitud));
        if (_wcsicmp(hashEsperado.c_str(), hashEmbebido.c_str()) != 0)
        {
            throw ErrorLanzador(L"El componente interno embebido esta manipulado.");
        }

        entorno = PrepararEntorno();
        entornoPreparado = true;
        ExtraerPayload(entorno.rutaPayload, payload, hashEsperado);
        const DWORD codigo = EjecutarAplicacion(entorno.rutaPayload);
        LimpiarDespuesDelCierre(entorno);
        entornoPreparado = false;
        return codigo == CodigoCierreDefinitivo
            ? 0
            : static_cast<int>(codigo);
    }
    catch (const ErrorLanzador& error)
    {
        if (entornoPreparado)
        {
            LimpiarDespuesDelCierre(entorno);
        }

        MessageBoxW(
            nullptr,
            error.Mensaje().c_str(),
            L"LanzadorScripts",
            MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
        return 1;
    }
    catch (...)
    {
        if (entornoPreparado)
        {
            LimpiarDespuesDelCierre(entorno);
        }

        MessageBoxW(
            nullptr,
            L"Se produjo un error no controlado al preparar LanzadorScripts.",
            L"LanzadorScripts",
            MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
        return 1;
    }
}
