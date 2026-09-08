// (Autor: Alex Roman)
// Descripcion: Instala un MSI validado y relanza LanzadorScripts sin usar PowerShell.

#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#include <msi.h>
#include <msiquery.h>
#include <shellapi.h>
#include <shlobj.h>
#include <softpub.h>
#include <wincrypt.h>
#include <wintrust.h>

#include <algorithm>
#include <array>
#include <cwctype>
#include <limits>
#include <string>
#include <vector>

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "msi.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "wintrust.lib")

namespace
{
    constexpr wchar_t NombreActualizador[] = L"LanzadorScripts.Actualizador.exe";
    constexpr wchar_t NombreProducto[] = L"LanzadorScripts";
    constexpr wchar_t UpgradeCode[] = L"{24169C78-5164-45C8-AB1A-AFC281D86DE9}";
    constexpr wchar_t HuellaFirma[] = L"6C654649369000DDE0AA70F62645058D9A3437F5";
    constexpr DWORD TiempoCierreMs = 60000;
    constexpr DWORD TiempoInstalacionMs = 2 * 60 * 60 * 1000;
    constexpr DWORD CodigoReinicioIniciado = 1641;
    constexpr DWORD CodigoReinicioNecesario = 3010;
    constexpr DWORD CodigoCancelado = 1602;
    constexpr UINT PropiedadPlantilla = 7;
    constexpr LONGLONG LongitudMaximaMsi = 2LL * 1024 * 1024 * 1024;

    std::wstring UnirRuta(const std::wstring& izquierda, const std::wstring& derecha)
    {
        if (izquierda.empty())
        {
            return derecha;
        }

        return izquierda.back() == L'\\'
            ? izquierda + derecha
            : izquierda + L"\\" + derecha;
    }

    std::wstring NormalizarRuta(const std::wstring& ruta)
    {
        if (ruta.empty() || ruta.find(L'/') != std::wstring::npos)
        {
            return {};
        }

        const DWORD necesaria = GetFullPathNameW(ruta.c_str(), 0, nullptr, nullptr);
        if (necesaria == 0 || necesaria > 32767)
        {
            return {};
        }

        std::vector<wchar_t> buffer(static_cast<std::size_t>(necesaria) + 1);
        const DWORD escritos = GetFullPathNameW(
            ruta.c_str(),
            static_cast<DWORD>(buffer.size()),
            buffer.data(),
            nullptr);
        if (escritos == 0 || escritos >= buffer.size())
        {
            return {};
        }

        std::wstring completa(buffer.data(), escritos);
        while (completa.size() > 3 && completa.back() == L'\\')
        {
            completa.pop_back();
        }

        return completa;
    }

    std::wstring ObtenerCarpetaConocida(REFKNOWNFOLDERID identificador)
    {
        PWSTR ruta = nullptr;
        const HRESULT resultado = SHGetKnownFolderPath(
            identificador,
            KF_FLAG_DEFAULT,
            nullptr,
            &ruta);
        if (FAILED(resultado) || ruta == nullptr)
        {
            return {};
        }

        const std::wstring valor = NormalizarRuta(ruta);
        CoTaskMemFree(ruta);
        return valor;
    }

    std::wstring ObtenerRutaPropia()
    {
        std::vector<wchar_t> buffer(32768);
        const DWORD escritos = GetModuleFileNameW(
            nullptr,
            buffer.data(),
            static_cast<DWORD>(buffer.size()));
        return escritos > 0 && escritos < buffer.size()
            ? NormalizarRuta(std::wstring(buffer.data(), escritos))
            : std::wstring();
    }

    bool EmpiezaPorRuta(const std::wstring& ruta, const std::wstring& raiz)
    {
        return ruta.size() > raiz.size()
            && _wcsnicmp(ruta.c_str(), raiz.c_str(), raiz.size()) == 0
            && ruta[raiz.size()] == L'\\';
    }

    bool EsHexadecimal(const std::wstring& valor, std::size_t longitud)
    {
        return valor.size() == longitud
            && std::all_of(valor.begin(), valor.end(), [](wchar_t caracter)
            {
                return (caracter >= L'0' && caracter <= L'9')
                    || (caracter >= L'A' && caracter <= L'F')
                    || (caracter >= L'a' && caracter <= L'f');
            });
    }

    bool EsVersionValida(const std::wstring& version)
    {
        if (version.empty() || version.size() > 32)
        {
            return false;
        }

        int separadores = 0;
        int digitos = 0;
        for (const wchar_t caracter : version)
        {
            if (caracter == L'.')
            {
                if (digitos == 0 || ++separadores > 2)
                {
                    return false;
                }

                digitos = 0;
                continue;
            }

            if (!iswdigit(caracter) || ++digitos > 5)
            {
                return false;
            }
        }

        return separadores == 2 && digitos > 0;
    }

    bool ValidarNombreMsi(const std::wstring& nombre, const std::wstring& version)
    {
        if (!EsVersionValida(version)
            || nombre.find(L'\\') != std::wstring::npos
            || nombre.find(L'/') != std::wstring::npos
            || nombre.find(L"..") != std::wstring::npos)
        {
            return false;
        }

        return _wcsicmp(
            nombre.c_str(),
            (L"LanzadorScripts-" + version + L"-x64.msi").c_str()) == 0;
    }

    bool EsNombreSesion(const std::wstring& nombre)
    {
        constexpr wchar_t Prefijo[] = L"Sesion-";
        return nombre.size() == 7 + 32
            && _wcsnicmp(nombre.c_str(), Prefijo, 7) == 0
            && EsHexadecimal(nombre.substr(7), 32);
    }

    bool RutaSinPuntosReanalisis(const std::wstring& ruta)
    {
        const std::wstring completa = NormalizarRuta(ruta);
        if (completa.empty())
        {
            return false;
        }

        std::size_t posicion = completa.size() >= 3 && completa[1] == L':' ? 3 : 0;
        while (posicion <= completa.size())
        {
            const std::size_t separador = completa.find(L'\\', posicion);
            const std::wstring parcial = separador == std::wstring::npos
                ? completa
                : completa.substr(0, separador);
            const DWORD atributos = GetFileAttributesW(parcial.c_str());
            if (atributos == INVALID_FILE_ATTRIBUTES
                || (atributos & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
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

    bool ValidarUbicacion(
        const std::wstring& rutaPropia,
        const std::wstring& nombreMsi,
        std::wstring& rutaMsi)
    {
        const std::wstring programData = ObtenerCarpetaConocida(FOLDERID_ProgramData);
        const std::wstring raiz = NormalizarRuta(UnirRuta(
            programData,
            L"LanzadorScripts\\Actualizaciones\\Staging"));
        const std::size_t separador = rutaPropia.find_last_of(L'\\');
        if (raiz.empty() || separador == std::wstring::npos)
        {
            return false;
        }

        const std::wstring carpeta = rutaPropia.substr(0, separador);
        const std::wstring nombrePropio = rutaPropia.substr(separador + 1);
        const std::size_t separadorSesion = carpeta.find_last_of(L'\\');
        const std::wstring nombreSesion = separadorSesion == std::wstring::npos
            ? std::wstring()
            : carpeta.substr(separadorSesion + 1);
        if (_wcsicmp(nombrePropio.c_str(), NombreActualizador) != 0
            || !EmpiezaPorRuta(carpeta, raiz)
            || !EsNombreSesion(nombreSesion)
            || !RutaSinPuntosReanalisis(carpeta))
        {
            return false;
        }

        rutaMsi = NormalizarRuta(UnirRuta(carpeta, nombreMsi));
        return !rutaMsi.empty()
            && EmpiezaPorRuta(rutaMsi, carpeta)
            && RutaSinPuntosReanalisis(rutaMsi);
    }

    bool CalcularSha256(
        const std::wstring& ruta,
        std::wstring& hash,
        HANDLE& archivoBloqueado)
    {
        archivoBloqueado = INVALID_HANDLE_VALUE;
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
            return false;
        }

        LARGE_INTEGER longitud{};
        if (!GetFileSizeEx(archivo, &longitud)
            || longitud.QuadPart <= 0
            || longitud.QuadPart > LongitudMaximaMsi)
        {
            CloseHandle(archivo);
            return false;
        }

        BCRYPT_ALG_HANDLE algoritmo = nullptr;
        BCRYPT_HASH_HANDLE calculo = nullptr;
        DWORD tamanoObjeto = 0;
        DWORD bytes = 0;
        bool correcto = BCryptOpenAlgorithmProvider(
            &algoritmo,
            BCRYPT_SHA256_ALGORITHM,
            nullptr,
            0) == 0
            && BCryptGetProperty(
                algoritmo,
                BCRYPT_OBJECT_LENGTH,
                reinterpret_cast<PUCHAR>(&tamanoObjeto),
                sizeof(tamanoObjeto),
                &bytes,
                0) == 0;
        std::vector<UCHAR> objeto(tamanoObjeto);
        std::array<UCHAR, 32> resultado{};
        if (correcto)
        {
            correcto = BCryptCreateHash(
                algoritmo,
                &calculo,
                objeto.data(),
                static_cast<ULONG>(objeto.size()),
                nullptr,
                0,
                0) == 0;
        }

        std::vector<UCHAR> buffer(1024 * 1024);
        while (correcto)
        {
            DWORD leidos = 0;
            if (!ReadFile(
                    archivo,
                    buffer.data(),
                    static_cast<DWORD>(buffer.size()),
                    &leidos,
                    nullptr))
            {
                correcto = false;
                break;
            }

            if (leidos == 0)
            {
                break;
            }

            correcto = BCryptHashData(calculo, buffer.data(), leidos, 0) == 0;
        }

        if (correcto)
        {
            correcto = BCryptFinishHash(
                calculo,
                resultado.data(),
                static_cast<ULONG>(resultado.size()),
                0) == 0;
        }

        if (calculo != nullptr)
        {
            BCryptDestroyHash(calculo);
        }
        if (algoritmo != nullptr)
        {
            BCryptCloseAlgorithmProvider(algoritmo, 0);
        }
        if (!correcto)
        {
            CloseHandle(archivo);
            return false;
        }

        constexpr wchar_t Hex[] = L"0123456789ABCDEF";
        hash.clear();
        hash.reserve(64);
        for (const UCHAR valor : resultado)
        {
            hash.push_back(Hex[valor >> 4]);
            hash.push_back(Hex[valor & 0x0F]);
        }
        archivoBloqueado = archivo;
        return true;
    }

    bool VerificarFirmaWindows(const std::wstring& ruta)
    {
        WINTRUST_FILE_INFO archivo{};
        archivo.cbStruct = sizeof(archivo);
        archivo.pcwszFilePath = ruta.c_str();
        WINTRUST_DATA datos{};
        datos.cbStruct = sizeof(datos);
        datos.dwUIChoice = WTD_UI_NONE;
        datos.fdwRevocationChecks = WTD_REVOKE_NONE;
        datos.dwUnionChoice = WTD_CHOICE_FILE;
        datos.pFile = &archivo;
        datos.dwStateAction = WTD_STATEACTION_IGNORE;
        datos.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL;
        GUID accion = WINTRUST_ACTION_GENERIC_VERIFY_V2;
        const LONG resultado = WinVerifyTrust(nullptr, &accion, &datos);
        return resultado == ERROR_SUCCESS
            || static_cast<ULONG>(resultado) == CERT_E_UNTRUSTEDROOT;
    }

    bool ObtenerHuellaFirmante(const std::wstring& ruta, std::wstring& huella)
    {
        HCERTSTORE almacen = nullptr;
        HCRYPTMSG mensaje = nullptr;
        DWORD codificacion = 0;
        DWORD tipoContenido = 0;
        DWORD tipoFormato = 0;
        const BOOL consultado = CryptQueryObject(
            CERT_QUERY_OBJECT_FILE,
            ruta.c_str(),
            CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED_EMBED,
            CERT_QUERY_FORMAT_FLAG_BINARY,
            0,
            &codificacion,
            &tipoContenido,
            &tipoFormato,
            &almacen,
            &mensaje,
            nullptr);
        if (!consultado || almacen == nullptr || mensaje == nullptr)
        {
            return false;
        }

        DWORD longitudFirmante = 0;
        bool correcto = CryptMsgGetParam(
            mensaje,
            CMSG_SIGNER_INFO_PARAM,
            0,
            nullptr,
            &longitudFirmante) != FALSE;
        std::vector<BYTE> datosFirmante(longitudFirmante);
        if (correcto)
        {
            correcto = CryptMsgGetParam(
                mensaje,
                CMSG_SIGNER_INFO_PARAM,
                0,
                datosFirmante.data(),
                &longitudFirmante) != FALSE;
        }

        PCCERT_CONTEXT certificado = nullptr;
        if (correcto)
        {
            const auto firmante = reinterpret_cast<PCMSG_SIGNER_INFO>(datosFirmante.data());
            CERT_INFO informacion{};
            informacion.Issuer = firmante->Issuer;
            informacion.SerialNumber = firmante->SerialNumber;
            certificado = CertFindCertificateInStore(
                almacen,
                X509_ASN_ENCODING | PKCS_7_ASN_ENCODING,
                0,
                CERT_FIND_SUBJECT_CERT,
                &informacion,
                nullptr);
            correcto = certificado != nullptr;
        }

        std::array<BYTE, 20> hash{};
        DWORD longitudHash = static_cast<DWORD>(hash.size());
        if (correcto)
        {
            correcto = CertGetCertificateContextProperty(
                certificado,
                CERT_SHA1_HASH_PROP_ID,
                hash.data(),
                &longitudHash) != FALSE
                && longitudHash == hash.size();
        }

        if (certificado != nullptr)
        {
            CertFreeCertificateContext(certificado);
        }
        CryptMsgClose(mensaje);
        CertCloseStore(almacen, 0);
        if (!correcto)
        {
            return false;
        }

        constexpr wchar_t Hex[] = L"0123456789ABCDEF";
        huella.clear();
        for (const BYTE valor : hash)
        {
            huella.push_back(Hex[valor >> 4]);
            huella.push_back(Hex[valor & 0x0F]);
        }
        return true;
    }

    std::wstring LeerPropiedadMsi(MSIHANDLE baseDatos, const wchar_t* propiedad)
    {
        PMSIHANDLE vista;
        if (MsiDatabaseOpenViewW(
                baseDatos,
                L"SELECT `Value` FROM `Property` WHERE `Property` = ?",
                &vista) != ERROR_SUCCESS)
        {
            return {};
        }

        PMSIHANDLE parametro = MsiCreateRecord(1);
        if (parametro == 0
            || MsiRecordSetStringW(parametro, 1, propiedad) != ERROR_SUCCESS
            || MsiViewExecute(vista, parametro) != ERROR_SUCCESS)
        {
            return {};
        }

        PMSIHANDLE registro;
        if (MsiViewFetch(vista, &registro) != ERROR_SUCCESS)
        {
            return {};
        }

        DWORD longitud = 0;
        wchar_t consultaTamano[1]{};
        UINT resultado = MsiRecordGetStringW(registro, 1, consultaTamano, &longitud);
        if (resultado != ERROR_MORE_DATA && resultado != ERROR_SUCCESS)
        {
            return {};
        }

        std::vector<wchar_t> buffer(static_cast<std::size_t>(longitud) + 1);
        DWORD capacidad = static_cast<DWORD>(buffer.size());
        resultado = MsiRecordGetStringW(registro, 1, buffer.data(), &capacidad);
        return resultado == ERROR_SUCCESS
            ? std::wstring(buffer.data(), capacidad)
            : std::wstring();
    }

    bool EsMsiCompatible(const std::wstring& ruta, const std::wstring& version)
    {
        PMSIHANDLE baseDatos;
        if (MsiOpenDatabaseW(ruta.c_str(), MSIDBOPEN_READONLY, &baseDatos) != ERROR_SUCCESS)
        {
            return false;
        }

        const std::wstring producto = LeerPropiedadMsi(baseDatos, L"ProductName");
        const std::wstring versionMsi = LeerPropiedadMsi(baseDatos, L"ProductVersion");
        const std::wstring upgrade = LeerPropiedadMsi(baseDatos, L"UpgradeCode");
        PMSIHANDLE resumen;
        if (MsiGetSummaryInformationW(baseDatos, nullptr, 0, &resumen) != ERROR_SUCCESS)
        {
            return false;
        }

        UINT tipo = 0;
        INT entero = 0;
        FILETIME fecha{};
        DWORD longitud = 0;
        UINT resultado = MsiSummaryInfoGetPropertyW(
            resumen,
            PropiedadPlantilla,
            &tipo,
            &entero,
            &fecha,
            nullptr,
            &longitud);
        if (resultado != ERROR_MORE_DATA && resultado != ERROR_SUCCESS)
        {
            return false;
        }

        std::vector<wchar_t> buffer(static_cast<std::size_t>(longitud) + 1);
        DWORD capacidad = static_cast<DWORD>(buffer.size());
        resultado = MsiSummaryInfoGetPropertyW(
            resumen,
            PropiedadPlantilla,
            &tipo,
            &entero,
            &fecha,
            buffer.data(),
            &capacidad);
        const std::wstring plantilla = resultado == ERROR_SUCCESS
            ? std::wstring(buffer.data(), capacidad)
            : std::wstring();
        const std::size_t separador = plantilla.find(L';');
        const std::wstring arquitectura = plantilla.substr(0, separador);
        return producto == NombreProducto
            && versionMsi == version
            && _wcsicmp(upgrade.c_str(), UpgradeCode) == 0
            && _wcsicmp(arquitectura.c_str(), L"x64") == 0;
    }

    bool EsperarProcesoPadre(DWORD identificador)
    {
        HANDLE proceso = OpenProcess(SYNCHRONIZE, FALSE, identificador);
        if (proceso == nullptr)
        {
            return GetLastError() == ERROR_INVALID_PARAMETER;
        }

        const DWORD resultado = WaitForSingleObject(proceso, TiempoCierreMs);
        CloseHandle(proceso);
        return resultado == WAIT_OBJECT_0;
    }

    DWORD EjecutarMsi(const std::wstring& rutaMsi)
    {
        std::vector<wchar_t> sistema(32768);
        const UINT escritos = GetSystemDirectoryW(
            sistema.data(),
            static_cast<UINT>(sistema.size()));
        if (escritos == 0 || escritos >= sistema.size())
        {
            return ERROR_PATH_NOT_FOUND;
        }

        const std::wstring ejecutable = UnirRuta(sistema.data(), L"msiexec.exe");
        std::wstring comando = L"\"" + ejecutable + L"\" /i \"" + rutaMsi
            + L"\" /passive /norestart REBOOT=ReallySuppress";
        std::vector<wchar_t> mutableComando(comando.begin(), comando.end());
        mutableComando.push_back(L'\0');
        STARTUPINFOW inicio{};
        inicio.cb = sizeof(inicio);
        PROCESS_INFORMATION proceso{};
        if (!CreateProcessW(
                ejecutable.c_str(),
                mutableComando.data(),
                nullptr,
                nullptr,
                FALSE,
                CREATE_UNICODE_ENVIRONMENT,
                nullptr,
                nullptr,
                &inicio,
                &proceso))
        {
            return GetLastError();
        }

        const DWORD espera = WaitForSingleObject(proceso.hProcess, TiempoInstalacionMs);
        DWORD codigo = ERROR_TIMEOUT;
        if (espera == WAIT_OBJECT_0)
        {
            GetExitCodeProcess(proceso.hProcess, &codigo);
        }
        CloseHandle(proceso.hThread);
        CloseHandle(proceso.hProcess);
        return codigo;
    }

    bool RelanzarAplicacion()
    {
        const std::wstring programas = ObtenerCarpetaConocida(FOLDERID_ProgramFiles);
        const std::wstring ejecutable = NormalizarRuta(UnirRuta(
            programas,
            L"LanzadorScripts\\LanzadorScripts.exe"));
        if (ejecutable.empty() || !RutaSinPuntosReanalisis(ejecutable))
        {
            return false;
        }

        std::wstring comando = L"\"" + ejecutable + L"\"";
        std::vector<wchar_t> mutableComando(comando.begin(), comando.end());
        mutableComando.push_back(L'\0');
        STARTUPINFOW inicio{};
        inicio.cb = sizeof(inicio);
        PROCESS_INFORMATION proceso{};
        const BOOL correcto = CreateProcessW(
            ejecutable.c_str(),
            mutableComando.data(),
            nullptr,
            nullptr,
            FALSE,
            CREATE_UNICODE_ENVIRONMENT,
            nullptr,
            programas.c_str(),
            &inicio,
            &proceso);
        if (correcto)
        {
            CloseHandle(proceso.hThread);
            CloseHandle(proceso.hProcess);
        }
        return correcto != FALSE;
    }

    void MostrarError(const std::wstring& mensaje)
    {
        MessageBoxW(
            nullptr,
            mensaje.c_str(),
            L"LanzadorScripts - actualización",
            MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
    }
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    int total = 0;
    LPWSTR* argumentos = CommandLineToArgvW(GetCommandLineW(), &total);
    if (argumentos == nullptr || total != 6
        || wcscmp(argumentos[1], L"--instalar") != 0)
    {
        if (argumentos != nullptr)
        {
            LocalFree(argumentos);
        }
        return ERROR_INVALID_PARAMETER;
    }

    const std::wstring nombreMsi = argumentos[2];
    const std::wstring hashEsperado = argumentos[3];
    const std::wstring version = argumentos[4];
    wchar_t* finPid = nullptr;
    const unsigned long pidValor = wcstoul(argumentos[5], &finPid, 10);
    const bool pidValido = finPid != nullptr && *finPid == L'\0';
    const DWORD pid = pidValor <= std::numeric_limits<DWORD>::max()
        ? static_cast<DWORD>(pidValor)
        : 0;
    LocalFree(argumentos);

    std::wstring rutaMsi;
    const std::wstring rutaPropia = ObtenerRutaPropia();
    if (!ValidarNombreMsi(nombreMsi, version)
        || !EsHexadecimal(hashEsperado, 64)
        || pid == 0
        || !pidValido
        || !ValidarUbicacion(rutaPropia, nombreMsi, rutaMsi))
    {
        MostrarError(L"Los datos recibidos por el actualizador no son válidos.");
        return ERROR_INVALID_DATA;
    }

    if (!EsperarProcesoPadre(pid))
    {
        MostrarError(L"LanzadorScripts no terminó dentro del tiempo permitido.");
        return ERROR_TIMEOUT;
    }

    std::wstring hashReal;
    std::wstring huella;
    HANDLE bloqueoMsi = INVALID_HANDLE_VALUE;
    if (!CalcularSha256(rutaMsi, hashReal, bloqueoMsi)
        || _wcsicmp(hashReal.c_str(), hashEsperado.c_str()) != 0
        || !VerificarFirmaWindows(rutaMsi)
        || !ObtenerHuellaFirmante(rutaMsi, huella)
        || _wcsicmp(huella.c_str(), HuellaFirma) != 0
        || !EsMsiCompatible(rutaMsi, version))
    {
        if (bloqueoMsi != INVALID_HANDLE_VALUE)
        {
            CloseHandle(bloqueoMsi);
        }
        MostrarError(L"El paquete MSI no superó la verificación final.");
        RelanzarAplicacion();
        return ERROR_INVALID_DATA;
    }

    const DWORD codigo = EjecutarMsi(rutaMsi);
    CloseHandle(bloqueoMsi);
    if (codigo == ERROR_SUCCESS)
    {
        return RelanzarAplicacion() ? ERROR_SUCCESS : ERROR_FILE_NOT_FOUND;
    }

    if (codigo == CodigoReinicioIniciado || codigo == CodigoReinicioNecesario)
    {
        MessageBoxW(
            nullptr,
            L"Windows necesita reiniciar el equipo para completar la actualización.",
            L"LanzadorScripts - actualización",
            MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND);
        return static_cast<int>(codigo);
    }

    if (codigo == ERROR_TIMEOUT)
    {
        MostrarError(
            L"Windows Installer no terminó dentro del tiempo permitido. "
            L"Compruebe su estado antes de volver a abrir LanzadorScripts.");
        return ERROR_TIMEOUT;
    }

    const std::wstring detalle = codigo == CodigoCancelado
        ? L"La instalación fue cancelada."
        : L"Windows Installer terminó con el código " + std::to_wstring(codigo) + L".";
    MostrarError(detalle);
    RelanzarAplicacion();
    return static_cast<int>(codigo);
}
