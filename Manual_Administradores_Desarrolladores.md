<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Define la administracion, seguridad, despliegue y desarrollo de LanzadorScripts. -->

# Manual De Administradores Y Desarrolladores

## Arquitectura 1.7.1

LanzadorScripts inicia una ventana WPF, un backend HTTP limitado a `127.0.0.1` y un cliente WebView2 embebido. El backend identifica al usuario, carga permisos, valida el catalogo y ejecuta una copia controlada del script.

Las distribuciones son:

```text
LanzadorScripts-1.7.1-x64.msi
LanzadorScripts_Portable-1.7.1-x64.exe
```

La instalada conserva binarios y runtimes en `Program Files`, y datos por usuario en `ProgramData`. La portable confina todo el estado local a una sesion aleatoria de `%TEMP%` y el lanzador nativo la elimina al finalizar.

## Contrato Firmado V3

Los artefactos operativos son:

```text
\\MAD002MICROPRU.mad.ae.aena.es\R$\PERMISOS\permisos.json
\\MAD002MICROPRU.mad.ae.aena.es\R$\PERMISOS\catalogo-scripts.json
```

Cada JSON contiene exactamente `Autor`, `Descripcion`, `Version`, `Tipo`, `Algoritmo`, `ConjuntoId`, `Contenido` y `Firma`.

- `Version`: `3`.
- `Algoritmo`: `RSA-PSS-SHA256`.
- `Tipo`: `permissions` o `script-catalog`.
- `ConjuntoId`: 32 caracteres hexadecimales en mayusculas.
- `Contenido`: objeto JSON legible.
- `Firma`: Base64 de la firma RSA.

No existe un paquete AES, DPAPI ni `artefactos.key`. La clave publica esta embebida. La clave privada con huella `500266A64E574889370D92E5CE0D65D55CC963B7` solo debe estar en equipos administradores.

Los administradores iniciales son:

```text
MAD00\aroperez_micro
PCERA\alero
```

## Generar Artefactos

```powershell
pwsh -NoProfile -File .\PrepararArtefactosFirmados.ps1 `
  -RutaScripts "C:\Ruta\ACTUALES" `
  -TotalScriptsEsperado 37
```

La herramienta valida cuentas, rutas, 37 scripts, SHA-256 y certificado privado; genera exactamente dos JSON con el mismo `ConjuntoId` y vuelve a validar la pareja.

Las escrituras desde la interfaz conservan `ConjuntoId`, verifican el archivo compañero, usan bloqueo entre procesos, escritura atomica y copia `.bak`.

## Preparar Auditoria

La auditoria se deriva de `RutaPermisos`:

```text
<RutaPermisos>\Auditoria\<dominio_usuario__sid-hash>
```

Ejecute una vez como administrador del servidor o recurso compartido:

```powershell
pwsh -NoProfile -File .\Herramientas\PrepararAuditoriaServidor.ps1 `
  -RutaPermisos "\\MAD002MICROPRU.mad.ae.aena.es\R$\PERMISOS"
```

La raiz permite a usuarios autenticados crear su carpeta, pero no modificar ni borrar eventos confirmados. Administradores y SYSTEM conservan control completo.

Cada evento usa `FileMode.CreateNew`, nombre impredecible y JSON de tamano limitado. El inicio debe confirmarse antes de crear el proceso. Un fallo final se conserva solo en memoria, bloquea nuevas ejecuciones y se reintenta hasta el cierre. No existe cola persistente local.

## MSI

El proyecto `Instalador\LanzadorScripts.Instalador.vdproj` usa `Publish Items` y el perfil `Properties\PublishProfiles\Instalada.pubxml`.

Contrato:

- Visual Studio Professional 2026 y Visual Studio Installer Projects 3.0.0 o posterior.
- x64 y todos los usuarios.
- Destino fijo `C:\Program Files\LanzadorScripts`.
- `UpgradeCode` estable `{24169C78-5164-45C8-AB1A-AFC281D86DE9}`.
- Menu Inicio y asociacion `.lanzadorconfig` obligatorios.
- Escritorio opcional con `CREATE_DESKTOP_SHORTCUT=1`.
- Apertura final desmarcada y solo disponible en UI interactiva.
- Sin inicio automatico con Windows.

Comandos:

```powershell
msiexec /i LanzadorScripts-1.7.1-x64.msi
msiexec /i LanzadorScripts-1.7.1-x64.msi CREATE_DESKTOP_SHORTCUT=1 /qn /norestart
msiexec /fa LanzadorScripts-1.7.1-x64.msi /qn /norestart
msiexec /x LanzadorScripts-1.7.1-x64.msi /qn /norestart
```

Instalacion, reparacion, actualizacion y desinstalacion se bloquean si hay una variante activa. Las actualizaciones y reparaciones conservan configuracion. La desinstalacion completa elimina solo `Program Files`, `ProgramData`, perfiles WebView2 locales y Registro conocidos; nunca toca auditoria ni artefactos remotos.

La primera instalacion retira runtimes antiguos administrados por las versiones portables anteriores sin eliminar configuracion.

## Portable

El lanzador nativo:

1. Crea `%TEMP%\LanzadorScripts\Portable\Sesion-<guid>` con ACL privada.
2. Extrae el payload firmado, .NET y WebView2 dentro de esa sesion.
3. Define `LANZADOR_VARIANTE=portable` y rutas confinadas.
4. Espera el cierre definitivo y comprueba procesos auxiliares.
5. Elimina el arbol sin seguir enlaces.
6. En el siguiente arranque, limpia sesiones abandonadas sin bloqueo o proceso activo.

La portable no consulta configuracion o tokens heredados, no registra `.lanzadorconfig` y no modifica el Registro. Los recursos remotos y archivos exportados expresamente quedan fuera de la limpieza.

## Compilacion

```powershell
pwsh -NoProfile -File .\Herramientas\PrepararVisualStudioInstalador.ps1
dotnet restore .\Pruebas\LanzadorScripts.Pruebas.csproj
dotnet build .\LanzadorScripts.csproj -c Release --no-restore
dotnet test .\Pruebas\LanzadorScripts.Pruebas.csproj -c Release --no-restore
dotnet list .\Pruebas\LanzadorScripts.Pruebas.csproj package --vulnerable --include-transitive --no-restore
```

Compilar solo el MSI:

```powershell
pwsh -NoProfile -File .\Herramientas\CompilarMsi.ps1 `
  -CertThumbprint "HUELLA_AUTHENTICODE"
```

Publicar las dos distribuciones:

```powershell
pwsh -NoProfile -File .\Herramientas\PublicarPortable.ps1 `
  -CertThumbprint "HUELLA_AUTHENTICODE"
```

La publicacion exige un arbol Git limpio y produce solo el MSI y la portable. Valida firma, sello de tiempo, version, revision Git, x64 y SHA-256.

## Pruebas Operativas

Antes de liberar:

1. Ejecute xUnit, auditoria NuGet, Semgrep estricto y Gitleaks sobre todo el historial.
2. Verifique firma Authenticode del MSI, portable, EXE instalado y helper.
3. Pruebe instalacion interactiva y silenciosa, reparacion, actualizacion y desinstalacion en un equipo limpio.
4. Compruebe menu Inicio, escritorio opcional, asociacion y funcionamiento sin Internet.
5. Fuerce un cierre portable y confirme la limpieza en el siguiente arranque.
6. Corte la red de auditoria y confirme HTTP `503` antes de crear el proceso.
7. Confirme que ninguna limpieza elimina `Auditoria` ni los JSON firmados.

Aikido no se utiliza. CodeRabbit no sustituye xUnit, Semgrep, Gitleaks ni revision humana.

## Repositorios Y Release

GitLab es el origen principal. Todo cambio se publica en la misma rama de GitLab y GitHub, con una unica merge request en GitLab. CodeRabbit revisa la MR.

La Release `v1.7.1` debe contener bytes identicos en ambos proveedores:

- `LanzadorScripts-1.7.1-x64.msi`.
- `LanzadorScripts_Portable-1.7.1-x64.exe`.
- `SHA256SUMS.txt`.
- Certificado publico Authenticode.
- ZIP con `permisos.json` y `catalogo-scripts.json`.
- Notas de despliegue.

`main`, etiqueta, hashes y releases deben apuntar al mismo commit en GitLab y GitHub.
