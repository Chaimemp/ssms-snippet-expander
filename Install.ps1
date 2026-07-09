<#
.SYNOPSIS
    Installs SSMS Snippet Expander: copies the snippet files into SSMS's snippet
    folder, builds the app, and (optionally) registers it to run at login.

.DESCRIPTION
    Run from the repo root in PowerShell:

        .\Install.ps1                 # copy snippets + build
        .\Install.ps1 -Startup        # also run the app at login
        .\Install.ps1 -NoBuild        # copy snippets only (skip build)

    No administrator rights are required — everything installs per-user.

.PARAMETER Startup
    Add a shortcut to the Windows Startup folder so the app launches at login.

.PARAMETER NoBuild
    Skip the dotnet build step (just install the snippet files).
#>
[CmdletBinding()]
param(
    [switch]$Startup,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot

# ── 1. Copy snippet files into every installed SSMS "My Shortcuts" folder ──────
$docs = Join-Path $env:USERPROFILE 'Documents'   # physical path SSMS actually uses
$ssmsDirs = @(Get-ChildItem $docs -Directory -Filter 'SQL Server Management Studio*' -ErrorAction SilentlyContinue)

if ($ssmsDirs.Count -eq 0) {
    # No SSMS profile folder yet — create one for SSMS 22.
    $ssmsDirs = @(New-Item -ItemType Directory -Force -Path (Join-Path $docs 'SQL Server Management Studio 22'))
}

foreach ($dir in $ssmsDirs) {
    $target = Join-Path $dir.FullName 'Snippets\My Shortcuts'
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item (Join-Path $repo 'snippets\*.snippet') $target -Force
    Write-Host "Copied snippets -> $target" -ForegroundColor Green
}

# ── 2. Build the app ───────────────────────────────────────────────────────────
$exe = Join-Path $repo 'bin\Release\net8.0-windows\SsmsSnippetExpander.exe'
if (-not $NoBuild) {
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        # A running instance locks the exe and fails the build.
        Stop-Process -Name 'SsmsSnippetExpander' -Force -ErrorAction SilentlyContinue
        Write-Host 'Building (dotnet build -c Release)...' -ForegroundColor Cyan
        dotnet build (Join-Path $repo 'SsmsSnippetExpander.csproj') -c Release | Out-Null
        Write-Host "Built -> $exe" -ForegroundColor Green
    } else {
        Write-Warning 'dotnet SDK not found. Install .NET 8 SDK, or run with -NoBuild and build manually.'
    }
}

# ── 3. Optional: run at login ──────────────────────────────────────────────────
if ($Startup) {
    $startupDir = [Environment]::GetFolderPath('Startup')
    $lnk = Join-Path $startupDir 'SsmsSnippetExpander.lnk'
    $wsh = New-Object -ComObject WScript.Shell
    $sc = $wsh.CreateShortcut($lnk)
    $sc.TargetPath = $exe
    $sc.WorkingDirectory = Split-Path $exe
    $sc.Save()
    Write-Host "Added to startup -> $lnk" -ForegroundColor Green
}

Write-Host ''
Write-Host 'Done. Launch the app:' -ForegroundColor Yellow
Write-Host "  $exe"
Write-Host 'Then in SSMS type a shortcut (e.g. ssf) and press Tab.'
