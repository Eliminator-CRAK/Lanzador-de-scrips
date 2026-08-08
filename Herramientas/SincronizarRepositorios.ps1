# (Autor: Alex Roman)
# Descripcion: Publica ramas en GitLab y GitHub y replica el main fusionado sin force-push.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('PublicarRama', 'SincronizarMain')]
    [string]$Modo,

    [string]$Rama = ''
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot

function Invoke-GitChecked {
    param([Parameter(Mandatory)][string[]]$Argumentos)

    # Detiene el flujo cuando Git no completa la operacion.
    $salida = & git @Argumentos 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Argumentos -join ' ') fallo: $($salida -join [Environment]::NewLine)"
    }
    return @($salida)
}

function Get-GitValue {
    param([Parameter(Mandatory)][string[]]$Argumentos)

    # Devuelve una unica referencia resuelta por Git.
    return ((Invoke-GitChecked -Argumentos $Argumentos) -join "`n").Trim()
}

function Test-Ancestor {
    param(
        [Parameter(Mandatory)][string]$Ancestro,
        [Parameter(Mandatory)][string]$Descendiente
    )

    # Comprueba si el avance se puede hacer sin reescribir historia.
    & git merge-base --is-ancestor $Ancestro $Descendiente
    if ($LASTEXITCODE -eq 0) {
        return $true
    }
    if ($LASTEXITCODE -eq 1) {
        return $false
    }
    throw 'No se pudo comprobar la relacion entre commits.'
}

function Assert-RemoteUrls {
    # Evita publicar por error en repositorios distintos a los corporativos.
    $gitlab = Get-GitValue -Argumentos @('remote', 'get-url', 'origin')
    $github = Get-GitValue -Argumentos @('remote', 'get-url', 'github')
    if ($gitlab -notmatch 'gitlab\.com[:/]micro2822131/Lanzador-de-scrips(?:\.git)?$') {
        throw "El remoto origin no apunta al GitLab esperado: $gitlab"
    }
    if ($github -notmatch 'github\.com[:/]Eliminator-CRAK/Lanzador-de-scrips(?:\.git)?$') {
        throw "El remoto github no apunta al GitHub esperado: $github"
    }
}

function Assert-CleanWorktree {
    # Evita publicar un SHA que no represente los cambios locales visibles.
    $estado = Get-GitValue -Argumentos @('status', '--porcelain', '--untracked-files=all')
    if (-not [string]::IsNullOrWhiteSpace($estado)) {
        throw 'El arbol de trabajo debe estar limpio antes de sincronizar repositorios.'
    }
}

function Get-RemoteBranchSha {
    param(
        [Parameter(Mandatory)][string]$Remoto,
        [Parameter(Mandatory)][string]$NombreRama
    )

    # Consulta la rama sin interpretar nombres suministrados como opciones.
    $referencia = "refs/heads/$NombreRama"
    $salida = & git ls-remote --heads $Remoto $referencia 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo consultar $Remoto/$NombreRama."
    }
    if (@($salida).Count -eq 0) {
        return ''
    }
    return ([string]$salida[0]).Split("`t")[0].Trim()
}

function Assert-BranchName {
    param([Parameter(Mandatory)][string]$NombreRama)

    # Acepta solo nombres de rama que Git puede publicar de forma no ambigua.
    if ($NombreRama -notmatch '^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$' -or
        $NombreRama.Contains('..') -or
        $NombreRama.EndsWith('/') -or
        $NombreRama.EndsWith('.') -or
        $NombreRama.Contains('@{') -or
        $NombreRama.Contains('//')) {
        throw "El nombre de rama no es valido: $NombreRama"
    }
}

Push-Location $raiz
try {
    Assert-RemoteUrls
    Assert-CleanWorktree
    Invoke-GitChecked -Argumentos @('fetch', '--prune', 'origin') | Out-Null
    Invoke-GitChecked -Argumentos @('fetch', '--prune', 'github') | Out-Null

    if ($Modo -eq 'PublicarRama') {
        if ([string]::IsNullOrWhiteSpace($Rama)) {
            $Rama = Get-GitValue -Argumentos @('branch', '--show-current')
        }
        Assert-BranchName -NombreRama $Rama
        if ($Rama -eq 'main') {
            throw 'Use SincronizarMain para publicar la rama protegida main.'
        }

        $ramaActual = Get-GitValue -Argumentos @('branch', '--show-current')
        if ($ramaActual -ne $Rama) {
            throw "La rama activa es $ramaActual, no $Rama."
        }
        $shaLocal = Get-GitValue -Argumentos @('rev-parse', 'HEAD')
        foreach ($remoto in @('origin', 'github')) {
            $shaRemoto = Get-RemoteBranchSha -Remoto $remoto -NombreRama $Rama
            if (-not [string]::IsNullOrWhiteSpace($shaRemoto) -and
                -not (Test-Ancestor -Ancestro $shaRemoto -Descendiente $shaLocal)) {
                throw "$remoto/$Rama diverge del historial local. No se publicara con force-push."
            }
            Invoke-GitChecked -Argumentos @(
                'push',
                $remoto,
                "$shaLocal`:refs/heads/$Rama"
            ) | Out-Null
            $shaPublicado = Get-RemoteBranchSha -Remoto $remoto -NombreRama $Rama
            if ($shaPublicado -ne $shaLocal) {
                throw "$remoto/$Rama no apunta al SHA local esperado."
            }
        }
        Write-Host "Rama $Rama publicada en ambos remotos: $shaLocal"
        return
    }

    $shaGitLab = Get-GitValue -Argumentos @('rev-parse', 'refs/remotes/origin/main')
    $shaGitHub = Get-GitValue -Argumentos @('rev-parse', 'refs/remotes/github/main')
    if (-not (Test-Ancestor -Ancestro $shaGitHub -Descendiente $shaGitLab)) {
        throw 'GitHub main diverge de GitLab main. La sincronizacion exige intervencion manual.'
    }
    Invoke-GitChecked -Argumentos @(
        'push',
        'github',
        "$shaGitLab`:refs/heads/main"
    ) | Out-Null
    $shaPublicado = Get-RemoteBranchSha -Remoto github -NombreRama main
    if ($shaPublicado -ne $shaGitLab) {
        throw 'GitHub main no coincide con el SHA fusionado en GitLab.'
    }
    Write-Host "GitHub main sincronizado con GitLab: $shaGitLab"
} finally {
    Pop-Location
}
