// (Autor: Alex Roman)
// Descripcion: Comprueba procesos, migra runtimes y limpia datos locales durante operaciones MSI.

#define WIN32_LEAN_AND_MEAN

#include <windows.h>
#include <shlobj.h>
#include <shellapi.h>
#include <tlhelp32.h>

#include <string>
#include <vector>

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "user32.lib")

namespace
{
    constexpr int CodigoErrorInstalacion = 1603;
    constexpr DWORD TiempoEsperaCierreMs = 15000;
    constexpr wchar_t NombreAplicacion[] = L"LanzadorScripts";
    constexpr wchar_t NombrePipeInstalado[] = L"\\\\.\\pipe\\LanzadorScripts_AlexRoman_ConfigPipe_Instalada";
    constexpr wchar_t NombreMensajeCierre[] = L"LanzadorScripts.CerrarMantenimiento.v1";

    // Une dos segmentos de una ruta local.
    std::wstring UnirRuta(const std::wstring& base, const std::wstring& nombre)
    {
        return base.empty() || base.back() == L'\\'
            ? base + nombre
            : base + L"\\" + nombre;
    }

    // Normaliza una ruta local sin exigir que exista.
    std::wstring NormalizarRuta(const std::wstring& ruta)
    {
        const DWORD requerida = GetFullPathNameW(ruta.c_str(), 0, nullptr, nullptr);
        if (requerida == 0)
        {
            return {};
        }

        std::vector<wchar_t> buffer(static_cast<std::size_t>(requerida) + 1);
        const DWORD escritos = GetFullPathNameW(
            ruta.c_str(),
            static_cast<DWORD>(buffer.size()),
            buffer.data(),
            nullptr);
        if (escritos == 0 || escritos >= buffer.size())
        {
            return {};
        }

        std::wstring resultado(buffer.data(), escritos);
        while (resultado.size() > 3 && resultado.back() == L'\\')
        {
            resultado.pop_back();
        }

        return resultado;
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

    // Resuelve una carpeta conocida de Windows.
    std::wstring ObtenerCarpetaConocida(REFKNOWNFOLDERID identificador)
    {
        PWSTR ruta = nullptr;
        if (FAILED(SHGetKnownFolderPath(identificador, KF_FLAG_DEFAULT, nullptr, &ruta)) || ruta == nullptr)
        {
            return {};
        }

        const std::wstring resultado = NormalizarRuta(ruta);
        CoTaskMemFree(ruta);
        return resultado;
    }

    // Comprueba que una ruta permanece dentro de una raiz conocida.
    bool EstaDentroDeRuta(const std::wstring& ruta, const std::wstring& raiz)
    {
        if (ruta.size() <= raiz.size()
            || _wcsnicmp(ruta.c_str(), raiz.c_str(), raiz.size()) != 0)
        {
            return false;
        }

        return ruta[raiz.size()] == L'\\';
    }

    // Rechaza redirecciones en cualquier segmento entre la raiz y el destino.
    bool RutaSinPuntosReanalisis(
        const std::wstring& raiz,
        const std::wstring& destino)
    {
        if (!EstaDentroDeRuta(destino, raiz))
        {
            return false;
        }

        const std::wstring raizWin32 = PrepararRutaWin32(raiz);
        const DWORD atributosRaiz = GetFileAttributesW(raizWin32.c_str());
        if (atributosRaiz == INVALID_FILE_ATTRIBUTES
            || (atributosRaiz & FILE_ATTRIBUTE_DIRECTORY) == 0
            || (atributosRaiz & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            return false;
        }

        std::size_t posicion = raiz.size() + 1;
        while (posicion <= destino.size())
        {
            const std::size_t separador = destino.find(L'\\', posicion);
            const std::wstring parcial = separador == std::wstring::npos
                ? destino
                : destino.substr(0, separador);
            const std::wstring parcialWin32 = PrepararRutaWin32(parcial);
            const DWORD atributos = GetFileAttributesW(parcialWin32.c_str());
            if (atributos == INVALID_FILE_ATTRIBUTES)
            {
                const DWORD error = GetLastError();
                return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND;
            }

            if ((atributos & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            {
                return false;
            }

            if (separador == std::wstring::npos)
            {
                break;
            }

            posicion = separador + 1;
        }

        return true;
    }

    // Retira un archivo o un enlace sin seguir puntos de reanalisis.
    bool EliminarEntradaSimple(const std::wstring& ruta, DWORD atributos)
    {
        const std::wstring rutaWin32 = PrepararRutaWin32(ruta);
        SetFileAttributesW(rutaWin32.c_str(), atributos & ~FILE_ATTRIBUTE_READONLY);
        return (atributos & FILE_ATTRIBUTE_DIRECTORY) != 0
            ? RemoveDirectoryW(rutaWin32.c_str()) != FALSE
            : DeleteFileW(rutaWin32.c_str()) != FALSE;
    }

    // Elimina un arbol validado sin atravesar enlaces ni puntos de reanalisis.
    bool EliminarArbolSeguroInterno(const std::wstring& ruta)
    {
        const std::wstring rutaWin32 = PrepararRutaWin32(ruta);
        const DWORD atributosRaiz = GetFileAttributesW(rutaWin32.c_str());
        if (atributosRaiz == INVALID_FILE_ATTRIBUTES)
        {
            return GetLastError() == ERROR_FILE_NOT_FOUND
                || GetLastError() == ERROR_PATH_NOT_FOUND;
        }

        if ((atributosRaiz & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            return false;
        }

        const std::wstring patron = PrepararRutaWin32(UnirRuta(ruta, L"*"));
        WIN32_FIND_DATAW datos{};
        HANDLE busqueda = FindFirstFileW(patron.c_str(), &datos);
        if (busqueda != INVALID_HANDLE_VALUE)
        {
            bool correcto = true;
            do
            {
                const std::wstring nombre = datos.cFileName;
                if (nombre == L"." || nombre == L"..")
                {
                    continue;
                }

                const std::wstring entrada = UnirRuta(ruta, nombre);
                if ((datos.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                {
                    correcto = EliminarEntradaSimple(entrada, datos.dwFileAttributes) && correcto;
                }
                else if ((datos.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                {
                    correcto = EliminarArbolSeguroInterno(entrada) && correcto;
                }
                else
                {
                    correcto = EliminarEntradaSimple(entrada, datos.dwFileAttributes) && correcto;
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

        SetFileAttributesW(rutaWin32.c_str(), atributosRaiz & ~FILE_ATTRIBUTE_READONLY);
        return RemoveDirectoryW(rutaWin32.c_str()) != FALSE
            || GetLastError() == ERROR_PATH_NOT_FOUND;
    }

    // Valida el destino antes de eliminar un arbol local conocido.
    bool EliminarArbolSeguro(const std::wstring& raizPermitida, const std::wstring& destino)
    {
        const std::wstring raiz = NormalizarRuta(raizPermitida);
        const std::wstring ruta = NormalizarRuta(destino);
        if (raiz.empty() || ruta.empty() || !EstaDentroDeRuta(ruta, raiz))
        {
            return false;
        }

        const std::wstring rutaWin32 = PrepararRutaWin32(ruta);
        const DWORD atributosDestino = GetFileAttributesW(rutaWin32.c_str());
        if (atributosDestino == INVALID_FILE_ATTRIBUTES)
        {
            const DWORD error = GetLastError();
            return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND;
        }

        return RutaSinPuntosReanalisis(raiz, ruta)
            && EliminarArbolSeguroInterno(ruta);
    }

    // Lee la ruta ejecutable de un proceso accesible.
    std::wstring ObtenerRutaProceso(DWORD identificador)
    {
        HANDLE proceso = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, identificador);
        if (proceso == nullptr)
        {
            return {};
        }

        std::vector<wchar_t> buffer(32768);
        DWORD longitud = static_cast<DWORD>(buffer.size());
        const BOOL correcto = QueryFullProcessImageNameW(
            proceso,
            0,
            buffer.data(),
            &longitud);
        CloseHandle(proceso);
        return correcto != FALSE
            ? NormalizarRuta(std::wstring(buffer.data(), longitud))
            : std::wstring();
    }

    // Obtiene procesos que pertenecen a la instalacion de Program Files.
    std::vector<DWORD> ObtenerProcesosInstalados()
    {
        const DWORD procesoActual = GetCurrentProcessId();
        const std::wstring programas = ObtenerCarpetaConocida(FOLDERID_ProgramFiles);
        const std::wstring raizAplicacion = NormalizarRuta(UnirRuta(programas, NombreAplicacion));
        if (raizAplicacion.empty())
        {
            return { procesoActual };
        }

        HANDLE captura = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (captura == INVALID_HANDLE_VALUE)
        {
            return { procesoActual };
        }

        PROCESSENTRY32W entrada{};
        entrada.dwSize = sizeof(entrada);
        std::vector<DWORD> procesos;
        if (Process32FirstW(captura, &entrada))
        {
            do
            {
                if (entrada.th32ProcessID == procesoActual)
                {
                    continue;
                }

                const std::wstring ruta = ObtenerRutaProceso(entrada.th32ProcessID);
                if (!ruta.empty()
                    && (ruta == raizAplicacion || EstaDentroDeRuta(ruta, raizAplicacion)))
                {
                    procesos.push_back(entrada.th32ProcessID);
                }
            } while (Process32NextW(captura, &entrada));
        }

        CloseHandle(captura);
        return procesos;
    }

    // Detecta procesos que pertenecen a la instalacion de Program Files.
    bool HayLanzadorInstaladoActivo()
    {
        return !ObtenerProcesosInstalados().empty();
    }

    struct ContextoMensajeCierre
    {
        const std::vector<DWORD>* procesos;
        UINT mensaje;
        bool enviado;
    };

    // Envia el mensaje registrado a ventanas de procesos instalados.
    BOOL CALLBACK EnviarMensajeCierreVentana(HWND ventana, LPARAM parametro)
    {
        auto contexto = reinterpret_cast<ContextoMensajeCierre*>(parametro);
        DWORD proceso = 0;
        GetWindowThreadProcessId(ventana, &proceso);
        for (const DWORD permitido : *contexto->procesos)
        {
            if (proceso == permitido)
            {
                contexto->enviado = PostMessageW(
                    ventana,
                    contexto->mensaje,
                    0,
                    0) != FALSE || contexto->enviado;
                break;
            }
        }

        return TRUE;
    }

    // Entrega a la aplicacion instalada una solicitud de cierre ordenado.
    bool EnviarSolicitudCierre()
    {
        const std::vector<DWORD> procesos = ObtenerProcesosInstalados();
        const UINT mensajeCierre = RegisterWindowMessageW(NombreMensajeCierre);
        ContextoMensajeCierre contexto{ &procesos, mensajeCierre, false };
        if (!procesos.empty() && mensajeCierre != 0)
        {
            EnumWindows(EnviarMensajeCierreVentana, reinterpret_cast<LPARAM>(&contexto));
        }

        if (contexto.enviado)
        {
            return true;
        }

        constexpr char Mensaje[] = "{\"accion\":2,\"ruta\":null}\n";
        for (int intento = 0; intento < 20; ++intento)
        {
            if (!WaitNamedPipeW(NombrePipeInstalado, 250)
                && GetLastError() != ERROR_SEM_TIMEOUT)
            {
                Sleep(100);
                continue;
            }

            HANDLE pipe = CreateFileW(
                NombrePipeInstalado,
                GENERIC_WRITE,
                0,
                nullptr,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                nullptr);
            if (pipe == INVALID_HANDLE_VALUE)
            {
                Sleep(100);
                continue;
            }

            DWORD escritos = 0;
            const BOOL correcto = WriteFile(
                pipe,
                Mensaje,
                static_cast<DWORD>(sizeof(Mensaje) - 1),
                &escritos,
                nullptr);
            FlushFileBuffers(pipe);
            CloseHandle(pipe);
            const bool enviadoPipe = correcto != FALSE
                && escritos == static_cast<DWORD>(sizeof(Mensaje) - 1);
            return enviadoPipe || contexto.enviado;
        }

        return contexto.enviado;
    }

    // Espera a que terminen la aplicacion y sus runtimes instalados.
    bool EsperarCierreInstalado(DWORD tiempoMaximoMs)
    {
        const ULONGLONG limite = GetTickCount64() + tiempoMaximoMs;
        do
        {
            if (!HayLanzadorInstaladoActivo())
            {
                return true;
            }

            Sleep(250);
        } while (GetTickCount64() < limite);

        return !HayLanzadorInstaladoActivo();
    }

    // Coordina el cierre y permite reintentar en una instalacion interactiva.
    bool CerrarParaMantenimiento(bool interfazCompleta)
    {
        if (!HayLanzadorInstaladoActivo())
        {
            return true;
        }

        EnviarSolicitudCierre();
        if (EsperarCierreInstalado(TiempoEsperaCierreMs))
        {
            return true;
        }

        while (interfazCompleta)
        {
            const int respuesta = MessageBoxW(
                nullptr,
                L"LanzadorScripts sigue abierto. Finalice los scripts activos y elija 'Cerrar LanzadorScripts' en el icono de la bandeja. Despues pulse Reintentar.",
                L"LanzadorScripts - mantenimiento",
                MB_RETRYCANCEL | MB_ICONWARNING | MB_SETFOREGROUND | MB_TOPMOST);
            if (respuesta != IDRETRY)
            {
                return false;
            }

            EnviarSolicitudCierre();
            if (EsperarCierreInstalado(TiempoEsperaCierreMs))
            {
                return true;
            }
        }

        return false;
    }

    // Retira runtimes antiguos sin modificar la configuracion instalada.
    bool MigrarVersionAnterior()
    {
        const std::wstring programas = ObtenerCarpetaConocida(FOLDERID_ProgramFiles);
        const std::wstring datosComunes = ObtenerCarpetaConocida(FOLDERID_ProgramData);
        if (programas.empty() || datosComunes.empty())
        {
            return false;
        }

        const std::wstring raizPrograma = UnirRuta(programas, NombreAplicacion);
        const std::wstring raizDatos = UnirRuta(datosComunes, NombreAplicacion);
        const bool runtimePrograma = EliminarArbolSeguro(
            raizPrograma,
            UnirRuta(raizPrograma, L"Runtimes"));
        const bool runtimeDatos = EliminarArbolSeguro(
            raizDatos,
            UnirRuta(raizDatos, L"Runtimes"));
        return runtimePrograma && runtimeDatos;
    }

    // Comprueba que una ruta heredada ausente no bloquea la instalacion.
    bool ValidarEliminacionRutaAusente()
    {
        const std::wstring programas = ObtenerCarpetaConocida(FOLDERID_ProgramFiles);
        if (programas.empty())
        {
            return false;
        }

        const std::wstring raizPrueba = UnirRuta(
            programas,
            L"LanzadorScripts-PruebaAusente-" + std::to_wstring(GetCurrentProcessId()));
        const std::wstring raizPruebaWin32 = PrepararRutaWin32(raizPrueba);
        const DWORD atributos = GetFileAttributesW(raizPruebaWin32.c_str());
        if (atributos != INVALID_FILE_ATTRIBUTES)
        {
            return false;
        }

        const DWORD error = GetLastError();
        return (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND)
            && EliminarArbolSeguro(raizPrueba, UnirRuta(raizPrueba, L"Runtimes"));
    }

    // Comprueba que el helper elimina rutas superiores a MAX_PATH.
    bool ValidarEliminacionRutaLarga()
    {
        const DWORD requerida = GetTempPathW(0, nullptr);
        if (requerida == 0)
        {
            return false;
        }

        std::vector<wchar_t> buffer(static_cast<std::size_t>(requerida) + 1);
        const DWORD escritosRuta = GetTempPathW(
            static_cast<DWORD>(buffer.size()),
            buffer.data());
        if (escritosRuta == 0 || escritosRuta >= buffer.size())
        {
            return false;
        }

        const std::wstring raizTemporal = NormalizarRuta(buffer.data());
        const std::wstring raizPrueba = UnirRuta(
            raizTemporal,
            L"LanzadorScripts-Msi-PruebaLarga-"
                + std::to_wstring(GetCurrentProcessId())
                + L"-"
                + std::to_wstring(GetTickCount64()));
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

        while (UnirRuta(directorio, L"archivo-prueba.dat").size() <= 280)
        {
            directorio = UnirRuta(directorio, L"segmento-ruta-larga-0123456789");
            if (!crearDirectorio(directorio))
            {
                EliminarArbolSeguro(raizTemporal, raizPrueba);
                return false;
            }
        }

        const std::wstring rutaArchivo = UnirRuta(directorio, L"archivo-prueba.dat");
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
            EliminarArbolSeguro(raizTemporal, raizPrueba);
            return false;
        }

        constexpr BYTE Contenido[] = { 0x4D, 0x53, 0x49 };
        DWORD escritos = 0;
        const BOOL correcto = WriteFile(
            archivo,
            Contenido,
            static_cast<DWORD>(sizeof(Contenido)),
            &escritos,
            nullptr);
        CloseHandle(archivo);
        if (correcto == FALSE
            || escritos != static_cast<DWORD>(sizeof(Contenido))
            || rutaArchivo.size() <= MAX_PATH)
        {
            EliminarArbolSeguro(raizTemporal, raizPrueba);
            return false;
        }

        return EliminarArbolSeguro(raizTemporal, raizPrueba);
    }

    // Elimina los perfiles locales conocidos de todos los usuarios.
    bool LimpiarPerfilesLocales()
    {
        const std::wstring perfiles = ObtenerCarpetaConocida(FOLDERID_UserProfiles);
        if (perfiles.empty())
        {
            return false;
        }

        WIN32_FIND_DATAW datos{};
        const std::wstring patronPerfiles = PrepararRutaWin32(UnirRuta(perfiles, L"*"));
        HANDLE busqueda = FindFirstFileW(patronPerfiles.c_str(), &datos);
        if (busqueda == INVALID_HANDLE_VALUE)
        {
            return GetLastError() == ERROR_FILE_NOT_FOUND;
        }

        bool correcto = true;
        do
        {
            const std::wstring nombre = datos.cFileName;
            if (nombre == L"."
                || nombre == L".."
                || (datos.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0
                || (datos.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            {
                continue;
            }

            const std::wstring perfil = UnirRuta(perfiles, nombre);
            const std::wstring local = UnirRuta(perfil, L"AppData\\Local");
            const std::wstring lanzador = UnirRuta(local, NombreAplicacion);
            const std::wstring recuperacion = UnirRuta(local, L"LanzadorScripts-WebView2-Recuperacion-v5");
            correcto = EliminarArbolSeguro(perfiles, lanzador) && correcto;
            correcto = EliminarArbolSeguro(perfiles, recuperacion) && correcto;
        } while (FindNextFileW(busqueda, &datos));

        FindClose(busqueda);
        return correcto;
    }

    // Elimina una clave conocida y acepta que ya no exista.
    bool EliminarClaveRegistro(HKEY raiz, const wchar_t* subclave)
    {
        const LSTATUS resultado = RegDeleteTreeW(raiz, subclave);
        return resultado == ERROR_SUCCESS
            || resultado == ERROR_FILE_NOT_FOUND
            || resultado == ERROR_PATH_NOT_FOUND;
    }

    // Elimina solo rutas locales y claves conocidas de la aplicacion.
    bool LimpiarDesinstalacionCompleta()
    {
        const std::wstring programas = ObtenerCarpetaConocida(FOLDERID_ProgramFiles);
        const std::wstring datosComunes = ObtenerCarpetaConocida(FOLDERID_ProgramData);
        if (programas.empty() || datosComunes.empty())
        {
            return false;
        }

        const bool perfiles = LimpiarPerfilesLocales();
        const bool datos = EliminarArbolSeguro(
            datosComunes,
            UnirRuta(datosComunes, NombreAplicacion));
        const bool binarios = EliminarArbolSeguro(
            programas,
            UnirRuta(programas, NombreAplicacion));

        const bool registroAplicacion = EliminarClaveRegistro(
            HKEY_LOCAL_MACHINE,
            L"SOFTWARE\\LanzadorScripts");
        const bool registroExtension = EliminarClaveRegistro(
            HKEY_CLASSES_ROOT,
            L".lanzadorconfig");
        const bool registroClase = EliminarClaveRegistro(
            HKEY_CLASSES_ROOT,
            L"LanzadorScripts.Configuracion");
        return perfiles
            && datos
            && binarios
            && registroAplicacion
            && registroExtension
            && registroClase;
    }
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    // Ejecuta una unica accion cerrada indicada por Windows Installer.
    int total = 0;
    LPWSTR* argumentos = CommandLineToArgvW(GetCommandLineW(), &total);
    if (argumentos == nullptr || total < 2 || total > 3)
    {
        if (argumentos != nullptr)
        {
            LocalFree(argumentos);
        }

        return CodigoErrorInstalacion;
    }

    const std::wstring accion = argumentos[1];
    const int nivelInterfaz = total == 3 ? _wtoi(argumentos[2]) : 5;
    LocalFree(argumentos);
    if (accion == L"--validar-ruta-ausente")
    {
        return ValidarEliminacionRutaAusente() ? ERROR_SUCCESS : CodigoErrorInstalacion;
    }

    if (accion == L"--validar-limpieza-ruta-larga")
    {
        return ValidarEliminacionRutaLarga() ? ERROR_SUCCESS : CodigoErrorInstalacion;
    }

    if (accion == L"--comprobar-cierre")
    {
        return CerrarParaMantenimiento(nivelInterfaz >= 4)
            ? ERROR_SUCCESS
            : CodigoErrorInstalacion;
    }

    if (total != 2 || HayLanzadorInstaladoActivo())
    {
        return CodigoErrorInstalacion;
    }

    if (accion == L"--migrar-1.6")
    {
        return MigrarVersionAnterior() ? ERROR_SUCCESS : CodigoErrorInstalacion;
    }

    if (accion == L"--limpiar-desinstalacion")
    {
        return LimpiarDesinstalacionCompleta() ? ERROR_SUCCESS : CodigoErrorInstalacion;
    }

    return CodigoErrorInstalacion;
}
