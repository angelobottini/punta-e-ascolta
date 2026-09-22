<#
.SYNOPSIS
    Pubblica Punta e Ascolta in cartelle portatili self-contained: publish\PuntaEAscolta-win-x64 e publish\PuntaEAscolta-win-arm64.

.DESCRIPTION
    1. Compila tutta la soluzione (Release).
    2. Esegue i test unitari (salvo -SkipTests).
    3. Pubblica l'app per ogni architettura: self-contained, non ridotta (no trimming), non a file singolo.
    4. Copia accanto all'eseguibile le librerie Visual C++ richieste da ONNX Runtime (se trovate sul PC di compilazione,
       solo quelle della stessa architettura, controllata nell'intestazione PE),
       toglie i file .lib inutili, copia docs\LEGGIMI.txt.
    5. Stampa dimensioni e numero di file.
    Nella cartella di destinazione vengono conservati settings.json, cache e logs se esistono gia'.

.PARAMETER Configuration
    Release (predefinito) oppure Debug.

.PARAMETER Runtime
    all (predefinito), win-x64 oppure win-arm64.

.PARAMETER SkipTests
    Salta i test unitari.

.PARAMETER ArtifactsPath
    Cartella dei file intermedi di compilazione (predefinita: artifacts\publish nel repository, esclusa da git).

.PARAMETER Dotnet
    Percorso di dotnet.exe. Predefinito: %DOTNET_ROOT%, poi %LOCALAPPDATA%\Microsoft\dotnet, poi il PATH.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\publish.ps1
    powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Runtime win-arm64 -SkipTests
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [ValidateSet('all', 'win-x64', 'win-arm64')]
    [string] $Runtime = 'all',

    [switch] $SkipTests,

    [string] $ArtifactsPath,

    [string] $Dotnet
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repo 'PuntaEAscolta.slnx'
$appProject = Join-Path $repo 'src\PuntaEAscolta.App\PuntaEAscolta.App.csproj'
$readme = Join-Path $repo 'docs\LEGGIMI.txt'
$publishRoot = Join-Path $repo 'publish'
if (-not $ArtifactsPath) { $ArtifactsPath = Join-Path $repo 'artifacts\publish' }
$runtimes = if ($Runtime -eq 'all') { @('win-x64', 'win-arm64') } else { @($Runtime) }

# File dell'utente da non cancellare quando si ripubblica sopra una cartella gia' in uso.
$keepNames = @('settings.json', 'settings.json.bad', 'cache', 'logs')

function Find-Dotnet {
    if ($Dotnet) {
        if (Test-Path $Dotnet) { return (Resolve-Path $Dotnet).Path }
        throw "dotnet non trovato in $Dotnet"
    }
    $candidates = @()
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe') }
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    throw 'dotnet.exe non trovato: indicare -Dotnet <percorso> oppure impostare DOTNET_ROOT.'
}

function Invoke-Dotnet {
    param([string[]] $Arguments)
    Write-Host ("> dotnet " + ($Arguments -join ' ')) -ForegroundColor DarkGray
    & $script:dotnetExe @Arguments
    if ($LASTEXITCODE -ne 0) { throw ("dotnet " + $Arguments[0] + " non riuscito (codice $LASTEXITCODE)") }
}

function Find-VcRuntimeFolder {
    param([string] $Architecture)
    $roots = @()
    if ($env:ProgramFiles) { $roots += (Join-Path $env:ProgramFiles 'Microsoft Visual Studio') }
    $programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
    if ($programFilesX86) { $roots += (Join-Path $programFilesX86 'Microsoft Visual Studio') }
    $found = @()
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $pattern = Join-Path $root "*\*\VC\Redist\MSVC\*\$Architecture\Microsoft.VC14*.CRT"
        $found += @(Get-ChildItem -Path $pattern -Directory -ErrorAction SilentlyContinue)
    }
    if ($found.Count -eq 0) { return $null }
    return ($found | Sort-Object FullName -Descending | Select-Object -First 1).FullName
}

# Tipo di macchina dell'intestazione PE (0x8664 = x64, 0xAA64 = ARM64); $null se il file non e' un PE.
function Get-PeMachine {
    param([string] $Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = New-Object IO.BinaryReader($stream)
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { return $null }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -le 0 -or $peOffset -gt $stream.Length - 6) { return $null }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { return $null }
        return [int] $reader.ReadUInt16()
    }
    finally { $stream.Dispose() }
}

function Clear-OutputFolder {
    param([string] $Folder)
    if (-not (Test-Path $Folder)) {
        New-Item -ItemType Directory -Path $Folder | Out-Null
        return
    }
    Get-ChildItem -LiteralPath $Folder -Force | Where-Object { $keepNames -notcontains $_.Name } |
        Remove-Item -Recurse -Force
}

function Get-FolderSize {
    param([string] $Folder)
    $files = @(Get-ChildItem -LiteralPath $Folder -Recurse -File -Force |
        Where-Object { $_.FullName -notmatch '\\(cache|logs)\\' -and $keepNames -notcontains $_.Name })
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    if (-not $bytes) { $bytes = 0 }
    return [pscustomobject]@{ Files = $files.Count; MB = [math]::Round($bytes / 1MB, 1) }
}

$script:dotnetExe = Find-Dotnet
if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = Split-Path -Parent $script:dotnetExe }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Write-Host "Punta e Ascolta: pubblicazione $Configuration per $($runtimes -join ', ')" -ForegroundColor Cyan
Write-Host "dotnet: $script:dotnetExe"
Write-Host "File intermedi: $ArtifactsPath"
$stopwatch = [Diagnostics.Stopwatch]::StartNew()

# 1. Compilazione di tutta la soluzione
Invoke-Dotnet @('build', $solution, '-c', $Configuration, '--artifacts-path', $ArtifactsPath)

# 2. Test unitari
if ($SkipTests) {
    Write-Host 'Test saltati (-SkipTests).' -ForegroundColor Yellow
}
else {
    Invoke-Dotnet @('test', $solution, '-c', $Configuration, '--no-build', '--artifacts-path', $ArtifactsPath)
}

# 3-4. Pubblicazione per architettura
$summary = @()
foreach ($rid in $runtimes) {
    $out = Join-Path $publishRoot "PuntaEAscolta-$rid"
    Write-Host ""
    Write-Host "Pubblicazione $rid in $out" -ForegroundColor Cyan
    Clear-OutputFolder $out

    Invoke-Dotnet @('publish', $appProject, '-c', $Configuration, '-r', $rid, '--self-contained', 'true',
        '-p:PublishTrimmed=false', '-p:PublishSingleFile=false', '-p:PublishReadyToRun=false',
        '--artifacts-path', $ArtifactsPath, '-o', $out)

    # ONNX Runtime importa le librerie Visual C++ (MSVCP140, VCRUNTIME140...): il publish non le copia.
    # Si copia solo cio' che ha la stessa architettura dell'app: nel Redist ARM64 vcruntime140_1.dll e' ARM64EC
    # (intestazione x64, serve solo al codice x64/ARM64EC) e l'onnxruntime ARM64 non lo importa.
    $arch = if ($rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
    $machine = if ($rid -eq 'win-arm64') { 0xAA64 } else { 0x8664 }
    $vcFolder = Find-VcRuntimeFolder $arch
    $vcCopied = @()
    $vcSkipped = @()
    if ($vcFolder) {
        foreach ($dll in @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
            $source = Join-Path $vcFolder $dll
            if (-not (Test-Path $source)) { continue }
            if ((Get-PeMachine $source) -ne $machine) {
                $vcSkipped += $dll
                continue
            }
            Copy-Item -LiteralPath $source -Destination (Join-Path $out $dll) -Force
            $vcCopied += $dll
        }
        Write-Host ("Librerie Visual C++ copiate da " + $vcFolder + ": " + ($vcCopied -join ', '))
        if ($vcSkipped.Count -gt 0) {
            Write-Host ("Non copiate (architettura diversa da $arch): " + ($vcSkipped -join ', '))
        }
    }
    else {
        Write-Warning "Librerie Visual C++ ($arch) non trovate su questo PC: sui PC senza Visual C++ Redistributable l'OCR ONNX risultera' non disponibile (resta l'OCR di Windows)."
    }

    # File inutili per l'esecuzione.
    Get-ChildItem -LiteralPath $out -Filter '*.lib' -File | Remove-Item -Force

    Copy-Item -LiteralPath $readme -Destination (Join-Path $out 'LEGGIMI.txt') -Force

    $exe = Join-Path $out 'PuntaEAscolta.exe'
    if (-not (Test-Path $exe)) { throw "Eseguibile mancante: $exe" }
    if (-not (Test-Path (Join-Path $out 'models\v5'))) { Write-Warning "Cartella models\v5 mancante in ${out}: OCR ONNX non disponibile." }

    $size = Get-FolderSize $out
    $summary += [pscustomobject]@{ Architettura = $rid; File = $size.Files; MB = $size.MB; VCRuntime = ($vcCopied.Count -gt 0); Cartella = $out }
}

Write-Host ""
Write-Host ("Pubblicazione terminata in {0:N0} s" -f $stopwatch.Elapsed.TotalSeconds) -ForegroundColor Green
$summary | Format-Table -AutoSize | Out-String | Write-Host
