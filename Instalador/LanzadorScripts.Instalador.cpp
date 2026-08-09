// (Autor: Alex Roman)
// Descripcion: Comprueba procesos, migra runtimes y limpia datos locales durante operaciones MSI.

#define WIN32_LEAN_AND_MEAN

#include <windows.h>
#include <shlobj.h>
#include <shellapi.h>
#include <tlhelp32.h>

#include <algorithm>
#include <cwctype>
#include <string>
#include <vector>

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")

namespace
{
    constexpr int CodigoErrorInstalacion = 1603;
    constexpr wchar_t NombreAplicacion[] = L"LanzadorScripts";

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

        const DWORD atributosRaiz = GetFileAttributesW(raiz.c_str());
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
            const DWORD atributos = GetFileAttributesW(parcial.c_str());
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
        SetFileAttributesW(ruta.c_str(), atributos & ~FILE_ATTRIBUTE_READONLY);
        return (atributos & FILE_ATTRIBUTE_DIRECTORY) != 0
            ? RemoveDirectoryW(ruta.c_str()) != FALSE
            : DeleteFileW(ruta.c_str()) != FALSE;
    }

    // Elimina un arbol validado sin atravesar enlaces ni puntos de reanalisis.
    bool EliminarArbolSeguroInterno(const std::wstring& ruta)
    {
        const DWORD atributosRaiz = GetFileAttributesW(ruta.c_str());
        if (atributosRaiz == INVALID_FILE_ATTRIBUTES)
        {
            return GetLastError() == ERROR_FILE_NOT_FOUND
                || GetLastError() == ERROR_PATH_NOT_FOUND;
        }

        if ((atributosRaiz & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            return false;
        }

        const std::wstring patron = UnirRuta(ruta, L"*");
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

        SetFileAttributesW(ruta.c_str(), atributosRaiz & ~FILE_ATTRIBUTE_READONLY);
        return RemoveDirectoryW(ruta.c_str()) != FALSE
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

        const DWORD atributosDestino = GetFileAttributesW(ruta.c_str());
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

    // Detecta cualquier variante de LanzadorScripts que siga ejecutandose.
    bool HayLanzadorActivo()
    {
        const DWORD procesoActual = GetCurrentProcessId();
        const std::wstring programas = ObtenerCarpetaConocida(FOLDERID_ProgramFiles);
        const std::wstring raizAplicacion = NormalizarRuta(UnirRuta(programas, NombreAplicacion));
        if (raizAplicacion.empty())
        {
            return true;
        }

        HANDLE captura = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (captura == INVALID_HANDLE_VALUE)
        {
            return true;
        }

        PROCESSENTRY32W entrada{};
        entrada.dwSize = sizeof(entrada);
        bool encontrado = true;
        if (Process32FirstW(captura, &entrada))
        {
            encontrado = false;
            do
            {
                if (entrada.th32ProcessID == procesoActual)
                {
                    continue;
                }

                std::wstring nombre = entrada.szExeFile;
                std::transform(nombre.begin(), nombre.end(), nombre.begin(), std::towlower);
                if (nombre == L"lanzadorscripts.exe"
                    || nombre.rfind(L"lanzadorscripts_portable-", 0) == 0)
                {
                    encontrado = true;
                    break;
                }

                const std::wstring ruta = ObtenerRutaProceso(entrada.th32ProcessID);
                if (!ruta.empty()
                    && (ruta == raizAplicacion || EstaDentroDeRuta(ruta, raizAplicacion)))
                {
                    encontrado = true;
                    break;
                }
            } while (Process32NextW(captura, &entrada));
        }

        CloseHandle(captura);
        return encontrado;
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
        const DWORD atributos = GetFileAttributesW(raizPrueba.c_str());
        if (atributos != INVALID_FILE_ATTRIBUTES)
        {
            return false;
        }

        const DWORD error = GetLastError();
        return (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND)
            && EliminarArbolSeguro(raizPrueba, UnirRuta(raizPrueba, L"Runtimes"));
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
        HANDLE busqueda = FindFirstFileW(UnirRuta(perfiles, L"*").c_str(), &datos);
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
    if (argumentos == nullptr || total != 2)
    {
        if (argumentos != nullptr)
        {
            LocalFree(argumentos);
        }

        return CodigoErrorInstalacion;
    }

    const std::wstring accion = argumentos[1];
    LocalFree(argumentos);
    if (accion == L"--validar-ruta-ausente")
    {
        return ValidarEliminacionRutaAusente() ? ERROR_SUCCESS : CodigoErrorInstalacion;
    }

    if (HayLanzadorActivo())
    {
        return CodigoErrorInstalacion;
    }

    if (accion == L"--comprobar-cierre")
    {
        return ERROR_SUCCESS;
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
