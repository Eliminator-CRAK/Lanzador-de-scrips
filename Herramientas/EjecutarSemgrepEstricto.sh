#!/bin/sh
# (Autor: Alex Roman)
# Descripcion: Ejecuta Semgrep en modo estricto y valida sus resultados.

set -u

# Conserva el informe en una ruta fija dentro del repositorio.
informe="semgrep-results.json"

# Ejecuta todas las reglas sin exclusiones ni supresiones locales.
semgrep scan \
  --config auto \
  --config p/security-audit \
  --config p/secrets \
  --no-error \
  --strict \
  --disable-nosem \
  --no-git-ignore \
  --timeout 60 \
  --timeout-threshold 0 \
  --max-target-bytes 0 \
  --json-output "$informe" \
  --verbose \
  .
codigo_semgrep=$?

# Valida el informe incluso cuando --strict devuelve errores de parser.
python3 Herramientas/ValidarResultadosSemgrep.py "$informe" "$codigo_semgrep"
