<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Plantilla de revision para merge requests de LanzadorScripts. -->

## Resumen

Describa el comportamiento cambiado y el motivo.

## Riesgo

- [ ] Autorizacion o identidad Windows
- [ ] Criptografia o certificados
- [ ] Rutas UNC o sistema de archivos
- [ ] Concurrencia o ejecucion de procesos
- [ ] WebView2 o interfaz
- [ ] Publicacion, firma o CI
- [ ] Sin impacto en las areas anteriores

## Verificacion

- [ ] Build Release correcto
- [ ] Todas las pruebas correctas
- [ ] Auditoria NuGet sin vulnerabilidades conocidas
- [ ] Semgrep estricto correcto
- [ ] Gitleaks de historial correcto
- [ ] Prueba manual indicada en la descripcion

## Revision

- [ ] CodeRabbit reviso la MR no borrador
- [ ] Comentarios accionables resueltos o justificados
- [ ] Discusiones resueltas
- [ ] No contiene secretos ni artefactos generados
- [ ] La rama publicada en GitHub apunta al mismo SHA

## Despliegue Y Rollback

Indique pasos de despliegue, compatibilidad y restauracion cuando proceda.
