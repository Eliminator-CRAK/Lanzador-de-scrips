<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Define el flujo corporativo de contribucion y publicacion. -->

# Contribuir A LanzadorScripts

## Repositorios

GitLab es el repositorio principal. GitHub es una replica del mismo historial para CI Windows y distribucion. No cree dos lineas de desarrollo independientes.

## Ramas

1. Actualice `main` desde GitLab.
2. Cree una rama descriptiva, por ejemplo `codex/instalador-msi-auditoria-remota`.
3. Publique la rama en GitLab y GitHub con `Herramientas\SincronizarRepositorios.ps1 -Modo PublicarRama`.
4. Abra una unica merge request en GitLab.

No se permiten pushes directos ni `force-push` sobre `main`. No reescriba una rama despues de que haya comenzado su revision salvo acuerdo explicito.

## Validacion Local

```powershell
dotnet restore .\Pruebas\LanzadorScripts.Pruebas.csproj
dotnet build .\LanzadorScripts.csproj -c Release --no-restore
dotnet test .\Pruebas\LanzadorScripts.Pruebas.csproj -c Release --no-restore
dotnet list .\Pruebas\LanzadorScripts.Pruebas.csproj package --vulnerable --include-transitive --no-restore
```

Ejecute Semgrep estricto y Gitleaks antes de solicitar la fusion. No se utiliza Aikido.

## Merge Request

- Complete la plantilla de GitLab.
- Mantenga la MR fuera de borrador cuando este lista para CodeRabbit.
- Resuelva o justifique todos los comentarios accionables.
- Exija pipeline correcto y discusiones resueltas.
- Use un merge commit para conservar la trazabilidad.
- La fusion solo la realiza un Maintainer.

## Sincronizacion Posterior

Despues de fusionar en GitLab:

```powershell
pwsh -NoProfile -File .\Herramientas\SincronizarRepositorios.ps1 `
  -Modo SincronizarMain
```

La herramienta solo permite un avance rapido de GitHub al SHA exacto de `origin/main`. Nunca usa `force-push`.

## Publicacion

Los builds de ramas son de desarrollo y no se firman. La firma y publicacion definitiva se realizan unicamente desde `main` o una etiqueta `v*`. El empaquetado requiere el runner Windows corporativo `vs-professional-2026`.

No incluya claves privadas, PFX, perfiles WebView2, runtimes descargados, `bin`, `obj`, EXE, scripts operativos ni JSON firmados generados.
