<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Presenta la arquitectura, compilacion y operacion de LanzadorScripts. -->

# LanzadorScripts

Aplicacion WPF para descubrir, autorizar y ejecutar scripts PowerShell, BAT y CMD desde una interfaz WebView2 local. La version actual es `1.6.0`.

## Estado Del Codigo

- Aplicacion: .NET 10, WPF, `win-x64` y ejecutable autocontenido.
- Interfaz: bundle compilado incluido en `ClienteWeb` como activo fuente de distribucion.
- Lanzador nativo: C++ en `LanzadorNativo`.
- Pruebas: xUnit en `Pruebas`.
- Repositorio principal: GitLab.
- Replica: GitHub con el mismo SHA de `main` y de las etiquetas publicadas.

El proyecto frontend original que genero el bundle de `ClienteWeb` no esta disponible. El repositorio puede compilar y publicar la aplicacion, pero no reconstruir ese bundle desde sus fuentes originales.

Los 37 scripts operativos no forman parte del repositorio. Se leen desde una carpeta externa durante la generacion del catalogo.

## Artefactos Firmados V3

La carpeta central predeterminada es:

```text
\\MAD002MICROPRU.mad.ae.aena.es\R$\PERMISOS
```

Contiene exactamente los artefactos activos:

```text
permisos.json
catalogo-scripts.json
```

Ambos archivos son JSON legible y estan firmados con RSA-PSS/SHA-256. No usan AES, DPAPI, SID ni `artefactos.key`.

El contenedor v3 tiene exactamente estas propiedades:

```text
Autor
Descripcion
Version
Tipo
Algoritmo
ConjuntoId
Contenido
Firma
```

`ConjuntoId` es un identificador publico aleatorio de 128 bits. Permisos y catalogo deben compartirlo. La aplicacion falla de forma cerrada si falta un archivo, cambia cualquier metadato o contenido, la firma no es valida, los tipos estan intercambiados o los identificadores no coinciden.

Los contenedores AES v1/v2 no se migran dentro de la aplicacion. Deben sustituirse conjuntamente por una pareja v3.

La clave privada RSA solo se instala en equipos autorizados para generar o modificar artefactos. Los clientes incorporan unicamente el certificado publico de verificacion.

`ServicioCifradoAplicacion` se conserva para `configuracion.dat` y paquetes `.lanzadorconfig`; no protege los dos JSON compartidos.

## Generar Los Dos JSON

Requisitos:

- Certificado privado de artefactos con huella `500266A64E574889370D92E5CE0D65D55CC963B7` en `CurrentUser\My` o `LocalMachine\My`.
- .NET SDK 10.x.
- Carpeta externa con los 37 scripts.

Ejemplo desde la raiz del repositorio:

```powershell
pwsh -NoProfile -File .\PrepararArtefactosFirmados.ps1 `
  -RutaScripts "C:\Ruta\ACTUALES" `
  -TotalScriptsEsperado 37
```

La salida se crea en `ArtefactosGenerados\conjunto-firmado-*` y contiene solo los dos JSON. Los administradores iniciales son:

```text
MAD00\aroperez_micro
PCERA\alero
```

La herramienta no se conecta a Active Directory ni genera material cifrado para un equipo concreto.

## Compilar Y Probar

```powershell
dotnet restore LanzadorScripts.slnx
dotnet build LanzadorScripts.slnx -c Release --no-restore
dotnet test LanzadorScripts.slnx -c Release --no-build
dotnet list LanzadorScripts.slnx package --vulnerable --include-transitive
```

Los analizadores adicionales son Semgrep estricto y Gitleaks sobre todo el historial. Aikido no forma parte de este flujo.

## Publicar Ejecutables

La publicacion requiere PowerShell 7.6, Visual Studio Build Tools para el lanzador C++ y un certificado Authenticode valido.

```powershell
pwsh -NoProfile -File .\Herramientas\PublicarPortable.ps1 `
  -CertThumbprint "HUELLA_DEL_CERTIFICADO_AUTHENTICODE"
```

La carpeta ignorada `publicacion` recibe:

```text
LanzadorScripts.exe
LanzadorScripts_Portable.exe
```

`-InicializarArtefactos` exige el certificado privado de artefactos, pero nunca busca `artefactos.key`.

La Release `v1.6.0` debe incluir los dos EXE firmados, `SHA256SUMS.txt`, el certificado publico Authenticode, notas de despliegue y un ZIP con los dos JSON firmados. GitLab y GitHub deben publicar exactamente los mismos bytes.

## Despliegue 1.6.0

El cambio es incompatible y debe ejecutarse durante una ventana de mantenimiento:

1. Cerrar todos los clientes.
2. Respaldar como una unidad los EXE anteriores, `permisos.json`, `catalogo-scripts.json` y `clave-artefactos.dpng.json`.
3. Copiar conjuntamente los dos JSON v3 a la carpeta central.
4. Distribuir ambos EXE `1.6.0`.
5. Validar arranque, permisos y ejecucion antes de reabrir el servicio.

No se deben mezclar EXE anteriores con JSON v3 ni EXE `1.6.0` con contenedores AES.

Para rollback, restaurar conjuntamente los EXE y los tres artefactos anteriores. El archivo local inactivo `%ProgramData%\LanzadorScripts\Seguridad\artefactos.key` puede conservarse siete dias; `1.6.0` no lo lee ni lo modifica.

## Datos Locales

- Configuracion por usuario: `%ProgramData%\LanzadorScripts\Usuarios\<perfil>\configuracion.dat`.
- Logs y auditoria: bajo la misma carpeta de usuario.
- Perfil WebView2: `%LocalAppData%\LanzadorScripts\WebView2-v4\Sesiones`.
- Runtime WebView2 extraido: `%ProgramFiles%\LanzadorScripts\Runtimes\WebView2`.

El boton de cerrar oculta la ventana. El menu de la bandeja permite restaurar, maximizar, minimizar y cerrar definitivamente. La confirmacion de cancelacion solo aparece si existen ejecuciones activas.

## Seguridad

- La API escucha solo en `127.0.0.1` y exige sesion local.
- Los scripts se validan por ruta, extension, longitud y SHA-256.
- El catalogo firmado autoriza el byte exacto que se puede ejecutar.
- Las actualizaciones de permisos y catalogo conservan el `ConjuntoId`, verifican el archivo compañero y usan escritura atomica con `.bak`.
- Las rutas con navegacion, enlaces de sistema o archivos fuera de las carpetas autorizadas se rechazan.
- Claves privadas, perfiles WebView2, runtimes descargados, `bin`, `obj`, EXE y artefactos operativos generados quedan fuera de Git.

## Flujo Git

Las contribuciones se desarrollan en ramas y se fusionan mediante una merge request de GitLab. `main` requiere pipeline correcto, discusiones resueltas y merge por Maintainers. CodeRabbit revisa la MR en GitLab. Tras la fusion, la herramienta de sincronizacion publica el mismo SHA en GitHub sin `force-push`.

Consulte `CONTRIBUTING.md`, `Manual_Usuarios.md` y `Manual_Administradores_Desarrolladores.md`.
