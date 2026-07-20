// (Autor: Alex Roman)
// Descripcion: Inicia la aplicacion sin extraer componentes en AppData.

#define WIN32_LEAN_AND_MEAN

#include <windows.h>
#include <aclapi.h>
#include <bcrypt.h>
#include <sddl.h>
#include <shellapi.h>
#include <shlobj.h>

#include <algorithm>
#include <cctype>
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

    // Resuelve una carpeta conocida de Windows.
    std::wstring ObtenerCarpetaSistema(REFKNOWNFOLDERID identificador)
    {
        PWSTR ruta = nullptr;
        const HRESULT resultado = SHGetKnownFolderPath(identificador, KF_FLAG_DEFAULT, nullptr, &ruta);
        if (FAILED(resultado) || ruta == nullptr)
        {
            throw ErrorLanzador(L"No se pudo resolver una carpeta local segura.");
        }

        std::wstring valor(ruta);
        CoTaskMemFree(ruta);
        return ObtenerRutaCompleta(valor);
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

    // Comprueba que una ruta permanece dentro de una raiz.
    bool EmpiezaPorRuta(const std::wstring& ruta, const std::wstring& raiz)
    {
        if (ruta.size() < raiz.size() || _wcsnicmp(ruta.c_str(), raiz.c_str(), raiz.size()) != 0)
        {
            return false;
        }

        return ruta.size() == raiz.size() || ruta[raiz.size()] == L'\\';
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
            const DWORD atributos = GetFileAttributesW(directorio.c_str());
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

        const DWORD resultado = SetNamedSecurityInfoW(
            const_cast<LPWSTR>(ruta.c_str()),
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
        HANDLE archivo = CreateFileW(
            ruta.c_str(),
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
        if (!GetFileAttributesExW(ruta.c_str(), GetFileExInfoStandard, &datos)
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
        DeleteFileW(temporal.c_str());
        HANDLE archivo = CreateFileW(
            temporal.c_str(),
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
            DeleteFileW(temporal.c_str());
            SetLastError(errorEscritura);
            LanzarErrorWindows(L"No se pudo guardar el componente interno");
        }

        if (_wcsicmp(CalcularHashArchivo(temporal).c_str(), hashEsperado.c_str()) != 0)
        {
            DeleteFileW(temporal.c_str());
            throw ErrorLanzador(L"El componente interno extraido no supera la validacion SHA-256.");
        }

        if (!MoveFileExW(
                temporal.c_str(),
                destino.c_str(),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        {
            const DWORD error = GetLastError();
            DeleteFileW(temporal.c_str());
            SetLastError(error);
            LanzarErrorWindows(L"No se pudo publicar el componente interno");
        }
    }

    // Genera el identificador corto usado por las rutas administradas.
    std::wstring CrearIdentificadorSid(const std::wstring& texto)
    {
        if (texto.empty())
        {
            return L"";
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
            LanzarErrorWindows(L"No se pudo codificar la identidad del usuario");
        }

        std::string utf8(static_cast<std::size_t>(longitud), '\0');
        if (WideCharToMultiByte(
                CP_UTF8,
                WC_ERR_INVALID_CHARS,
                texto.c_str(),
                static_cast<int>(texto.size()),
                utf8.data(),
                longitud,
                nullptr,
                nullptr) != longitud)
        {
            LanzarErrorWindows(L"No se pudo codificar la identidad del usuario");
        }

        const std::vector<BYTE> hash = CalcularSha256(
            reinterpret_cast<const BYTE*>(utf8.data()),
            utf8.size());
        std::wstring identificador = ConvertirHexadecimal(hash).substr(0, 32);
        std::transform(
            identificador.begin(),
            identificador.end(),
            identificador.begin(),
            [](wchar_t valor) { return static_cast<wchar_t>(std::towlower(valor)); });
        return L"sid-" + identificador;
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
                0,
                nullptr,
                nullptr,
                &inicio,
                &proceso))
        {
            LanzarErrorWindows(L"No se pudo iniciar LanzadorScripts");
        }

        CloseHandle(proceso.hThread);
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
        return codigo;
    }

    // Prepara rutas seguras y variables antes de iniciar .NET.
    void PrepararEntorno(
        const std::wstring& hashPayload,
        std::wstring& rutaPayload)
    {
        const std::wstring programFiles = ObtenerCarpetaSistema(FOLDERID_ProgramFiles);
        const std::wstring programData = ObtenerCarpetaSistema(FOLDERID_ProgramData);
        const std::wstring raizPrograma = UnirRuta(programFiles, L"LanzadorScripts");
        const std::wstring raizAplicacion = UnirRuta(raizPrograma, L"Aplicacion");
        const std::wstring versionAplicacion = UnirRuta(
            raizAplicacion,
            L"runtime-" + hashPayload.substr(0, 16));
        const std::wstring raizDotNet = UnirRuta(
            UnirRuta(raizPrograma, L"Runtimes\\DotNet"),
            L"runtime-" + hashPayload.substr(0, 16));

        CrearDirectorioSeguro(versionAplicacion, programFiles);
        CrearDirectorioSeguro(raizDotNet, programFiles);
        rutaPayload = UnirRuta(versionAplicacion, L"LanzadorScripts.Runtime.exe");

        const std::wstring sid = ObtenerSidUsuario();
        const std::wstring identificador = CrearIdentificadorSid(sid);
        const std::wstring raizDatos = UnirRuta(programData, L"LanzadorScripts");
        const std::wstring raizUsuarios = UnirRuta(raizDatos, L"Usuarios");
        const std::wstring raizUsuario = UnirRuta(raizUsuarios, identificador);
        const std::wstring temporales = UnirRuta(raizUsuario, L"Temporales");
        const std::wstring perfilWebView2 = UnirRuta(raizUsuario, L"WebView2\\Perfil");

        CrearDirectorioSeguro(raizDatos, programData);
        AplicarSeguridad(
            raizDatos,
            L"O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;GRGX;;;BU)");
        CrearDirectorioSeguro(raizUsuarios, programData);
        AplicarSeguridad(
            raizUsuarios,
            L"O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;GRGX;;;BU)");
        CrearDirectorioSeguro(raizUsuario, programData);
        AplicarSeguridad(
            raizUsuario,
            L"O:" + sid + L"D:P(A;OICI;FA;;;" + sid + L")(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)");
        CrearDirectorioSeguro(temporales, programData);
        CrearDirectorioSeguro(perfilWebView2, programData);

        if (!SetEnvironmentVariableW(L"DOTNET_BUNDLE_EXTRACT_BASE_DIR", raizDotNet.c_str())
            || !SetEnvironmentVariableW(L"TEMP", temporales.c_str())
            || !SetEnvironmentVariableW(L"TMP", temporales.c_str())
            || !SetEnvironmentVariableW(L"WEBVIEW2_USER_DATA_FOLDER", perfilWebView2.c_str())
            || !SetEnvironmentVariableW(
                L"LANZADOR_DISTRIBUTION_EXE",
                ObtenerRutaEjecutableActual().c_str()))
        {
            LanzarErrorWindows(L"No se pudo preparar el entorno local seguro");
        }
    }
}

// Valida, extrae e inicia la aplicacion embebida.
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
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

        std::wstring rutaPayload;
        PrepararEntorno(hashEsperado, rutaPayload);
        ExtraerPayload(rutaPayload, payload, hashEsperado);
        return static_cast<int>(EjecutarAplicacion(rutaPayload));
    }
    catch (const ErrorLanzador& error)
    {
        MessageBoxW(
            nullptr,
            error.Mensaje().c_str(),
            L"LanzadorScripts",
            MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
        return 1;
    }
    catch (...)
    {
        MessageBoxW(
            nullptr,
            L"Se produjo un error no controlado al preparar LanzadorScripts.",
            L"LanzadorScripts",
            MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
        return 1;
    }
}
