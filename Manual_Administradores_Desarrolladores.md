<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Define la administracion, seguridad, despliegue y desarrollo de LanzadorScripts. -->

# Manual De Administradores Y Desarrolladores

## Arquitectura

`LanzadorScripts.exe` inicia una ventana WPF, un backend HTTP limitado a `127.0.0.1` y un cliente WebView2 embebido. El backend resuelve la identidad Windows, carga permisos, valida el catalogo y ejecuta una copia controlada del script.

Los dos artefactos operativos se ubican en:

```text
\\MAD002MICROPRU.mad.ae.aena.es\R$\PERMISOS\permisos.json
\\MAD002MICROPRU.mad.ae.aena.es\R$\PERMISOS\catalogo-scripts.json
```

No existe un tercer paquete de clave en `1.6.0`.

## Contrato Firmado V3

Cada JSON contiene exactamente `Autor`, `Descripcion`, `Version`, `Tipo`, `Algoritmo`, `ConjuntoId`, `Contenido` y `Firma`.

- `Version`: `3`.
- `Algoritmo`: `RSA-PSS-SHA256`.
- `Tipo`: `permissions` o `script-catalog`.
- `ConjuntoId`: 32 caracteres hexadecimales en mayusculas.
- `Contenido`: objeto JSON legible.
- `Firma`: Base64 de la firma RSA.

La firma cubre un encuadre binario con longitudes, metadatos y los bytes UTF-8 exactos de `Contenido`. No depende de canonicalizar JSON. Se rechazan propiedades desconocidas o duplicadas, UTF-8 incorrecto, Base64 invalido, limites excedidos, tipos intercambiados, enlaces del sistema y cualquier modificacion.

La clave publica esta embebida. La clave privada con huella `500266A64E574889370D92E5CE0D65D55CC963B7` solo debe existir en equipos administradores autorizados.

Los clientes no necesitan certificados privados ni acceso a secretos. `artefactos.key` y `clave-artefactos.dpng.json` son obsoletos e inactivos.

## Administradores Iniciales

La generacion predeterminada autoriza exclusivamente como administradores a:

```text
MAD00\aroperez_micro
PCERA\alero
```

La autorizacion usa el nombre Windows que devuelve el equipo cliente. No depende de SID ni de una consulta al controlador de dominio durante la generacion.

## Generacion

Ejecute desde la raiz del repositorio:

```powershell
pwsh -NoProfile -File .\PrepararArtefactosFirmados.ps1 `
  -RutaScripts "C:\Ruta\ACTUALES" `
  -TotalScriptsEsperado 37
```

La herramienta:

1. Valida dos cuentas en formato `DOMINIO\usuario`.
2. Rechaza enlaces y rutas que salgan de la carpeta de scripts.
3. Copia y compara SHA-256 de los 37 scripts.
4. Comprueba el certificado privado de artefactos.
5. Genera exactamente dos JSON con un `ConjuntoId` comun.
6. Vuelve a validar tipos, metadatos, administradores, recuento y hashes.

La salida queda ignorada por Git bajo `ArtefactosGenerados`.

## Escrituras Administrativas

La interfaz puede modificar permisos o volver a publicar el catalogo solo si la pareja actual es valida. Cada escritura:

- conserva el `ConjuntoId` existente;
- verifica el archivo compañero;
- adquiere un bloqueo exclusivo en la carpeta compartida;
- escribe mediante archivo temporal y reemplazo atomico;
- conserva una copia `.bak`;
- valida la pareja resultante y restaura el respaldo si falla.

No edite manualmente el JSON porque cualquier cambio invalida la firma.

## Corte A 1.6.0

El formato v3 es un corte inmediato. Prepare primero los EXE y los JSON sin modificar el servidor.

Durante la ventana de mantenimiento:

1. Impida nuevos inicios y cierre todos los clientes.
2. Cree un respaldo fechado de ambos EXE anteriores.
3. Respalde juntos `permisos.json`, `catalogo-scripts.json` y `clave-artefactos.dpng.json` anteriores.
4. Compruebe SHA-256 y firma Authenticode de los EXE `1.6.0`.
5. Sustituya conjuntamente los dos JSON centrales.
6. Distribuya los dos EXE `1.6.0`.
7. Pruebe con `MAD00\aroperez_micro` y con una cuenta nominal.
8. Compruebe lectura de permisos, catalogo, ejecucion y auditoria.

No mezcle componentes de ambas versiones.

## Rollback

Restaure como una unidad los EXE anteriores y los tres artefactos AES respaldados. No intente convertir un JSON v3 en el cliente.

El archivo local inactivo `%ProgramData%\LanzadorScripts\Seguridad\artefactos.key` puede conservarse siete dias. `1.6.0` no crea, lee, rota ni elimina ese archivo. Tras cerrar la ventana de rollback, una limpieza administrativa opcional puede retirarlo.

## Compilacion Y Pruebas

```powershell
dotnet restore LanzadorScripts.slnx
dotnet build LanzadorScripts.slnx -c Release --no-restore
dotnet test LanzadorScripts.slnx -c Release --no-build
dotnet list LanzadorScripts.slnx package --vulnerable --include-transitive
```

Las pruebas cubren firma, manipulacion de metadatos y contenido, clave publica incorrecta, tipos, JSON duplicado, UTF-8, `.bak`, bloqueos y `ConjuntoId` distinto.

En un entorno con las herramientas instaladas, ejecute tambien:

```text
Semgrep estricto
Gitleaks sobre todo el historial
```

Aikido no se utiliza en este proyecto.

## Publicacion

```powershell
pwsh -NoProfile -File .\Herramientas\PublicarPortable.ps1 `
  -CertThumbprint "HUELLA_AUTHENTICODE"
```

Compruebe para ambos EXE:

- producto y archivo `1.6.0`;
- arquitectura x64;
- firma Authenticode valida;
- timestamp valido;
- SHA-256 registrado en `SHA256SUMS.txt`;
- arranque y lectura real de la pareja v3.

La Release `v1.6.0` de ambos proveedores debe contener bytes identicos. Nunca publique PFX, claves privadas, perfiles WebView2, runtimes descargados o artefactos operativos sin control de acceso.

## Frontend

`ClienteWeb` contiene el bundle compilado que se incrusta en el ejecutable. No esta disponible el proyecto frontend original. Los cambios de servidor y WPF son reproducibles; un cambio estructural del bundle requiere recuperar primero sus fuentes y su cadena de build.

## Repositorios Y Revision

GitLab es el origen principal. Todo cambio se desarrolla en una rama, se publica en ambos proveedores y abre una unica merge request en GitLab. La MR debe tener pipeline correcto, discusiones resueltas, revision humana y comentarios accionables de CodeRabbit resueltos o justificados.

CodeRabbit no sustituye xUnit, Semgrep, Gitleaks ni la revision humana. GitHub recibe el SHA exacto fusionado en GitLab sin `force-push`.

Consulte `CONTRIBUTING.md` y la plantilla de merge request para el proceso completo.
