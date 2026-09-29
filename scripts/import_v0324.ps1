param(
    [string]$Source = 'C:\ASTUE_MPS_BUILD\v0_3_2_4',
    [string]$Repo = 'C:\ASTUE_MPS_BUILD\astue-mps-replacement'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Copy-RequiredFile([string]$Name) {
    $src = Join-Path $Source $Name
    if (-not (Test-Path -LiteralPath $src -PathType Leaf)) {
        throw "Required source file not found: $src"
    }
    Copy-Item -LiteralPath $src -Destination (Join-Path $Repo $Name) -Force
}

function Copy-RequiredTree([string]$Name) {
    $src = Join-Path $Source $Name
    $dst = Join-Path $Repo $Name
    if (-not (Test-Path -LiteralPath $src -PathType Container)) {
        throw "Required source directory not found: $src"
    }
    if (Test-Path -LiteralPath $dst) {
        Remove-Item -LiteralPath $dst -Recurse -Force
    }
    Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force
}

Write-Host '===== ASTUE MPS v0.3.2.4 CLEAN IMPORT ====='
Write-Host "Source: $Source"
Write-Host "Repo:   $Repo"

if (-not (Test-Path -LiteralPath (Join-Path $Repo '.git') -PathType Container)) {
    throw "Not a Git checkout: $Repo"
}
if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
    throw "Source directory not found: $Source"
}

$rootFiles = @(
    'ASTUE_MPS_Replacement.sln',
    'AstueMpsReplacement.csproj',
    'build_and_package_x86.cmd',
    'build_release_x86.cmd',
    'check_env.cmd',
    'check_win7_native_toolset.cmd',
    'clean.cmd',
    'install_v143_win7_toolset.cmd',
    'start_config_mode.cmd',
    'start_work_mode.cmd'
)

foreach ($name in $rootFiles) { Copy-RequiredFile $name }
Copy-RequiredTree 'src'
Copy-RequiredTree 'opcda_native'

# Preserve the useful generic template verification tool, but exclude local
# field-test scripts tied to a specific COM line/site.
$toolsDir = Join-Path $Repo 'tools'
New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
$verifyTool = Join-Path $Source 'tools\verify_templates_against_mpp.py'
if (Test-Path -LiteralPath $verifyTool -PathType Leaf) {
    Copy-Item -LiteralPath $verifyTool -Destination (Join-Path $toolsDir 'verify_templates_against_mpp.py') -Force
}

# Sanitize template metadata while preserving the complete device trees/tags.
$templateSrc = Join-Path $Source 'templates\device_templates.json'
$templateDir = Join-Path $Repo 'templates'
$templateDst = Join-Path $templateDir 'device_templates.json'
if (-not (Test-Path -LiteralPath $templateSrc -PathType Leaf)) {
    throw "Template catalog not found: $templateSrc"
}
New-Item -ItemType Directory -Path $templateDir -Force | Out-Null

$catalog = Get-Content -LiteralPath $templateSrc -Raw -Encoding UTF8 | ConvertFrom-Json
$catalog.SourceMppSha256 = ''
$catalog.SourceFile = 'source_project.mpp'
$catalog.Policy = 'Generic embedded templates. Site-specific source paths and device names removed before repository import.'

foreach ($model in $catalog.Models) {
    $model.SourcePath = 'TEMPLATE.' + [string]$model.ModelId
    if ($null -ne $model.Template) {
        $model.Template.Name = [string]$model.ModelId
        if ($null -ne $model.Template.Properties) {
            $model.Template.Properties.NameInTree = [string]$model.ModelId
            if ($null -ne $model.Template.Properties.PSObject.Properties['DeviceAddress']) {
                $model.Template.Properties.DeviceAddress = '1'
            }
        }
    }
}

$catalog | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $templateDst -Encoding UTF8

$docsDir = Join-Path $Repo 'docs'
New-Item -ItemType Directory -Path $docsDir -Force | Out-Null
@'
# Windows 7 build notes

The managed application targets .NET Framework 4.0 Full, x86.
The native OPC DA LocalServer targets Windows 7 SP1 and is built with MSVC v143 + ATL.

Build on a Windows 10/11 x64 host:

```cmd
check_win7_native_toolset.cmd
build_and_package_x86.cmd
```

Expected audits include:

```text
PASS: AstueMpsReplacement targets .NET Framework 4.0 Full.
PASS: GetSystemTimePreciseAsFileTime is NOT statically imported.
6.01 subsystem version
```

Do not commit production `.mpp`/`.astue` projects, live inventories, logs, dumps, credentials or site addressing.
'@ | Set-Content -LiteralPath (Join-Path $docsDir 'BUILD_WIN7.md') -Encoding UTF8

# Audit for identifiers from the real field installation. The scripts folder is
# excluded because this script necessarily contains the audit strings itself.
$blockedPatterns = @(
    '192\.168\.',
    'ASTUE_48',
    'MERCURY_4624',
    'MERCURY_Vodozabor',
    'MERCURY_Ochistnie',
    'MERCURY_PSU',
    'COM33',
    'COM37',
    'COM100',
    '48\.34',
    '48\.153'
)

$auditFiles = Get-ChildItem -LiteralPath $Repo -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/]\.git[\\/]' -and
    $_.FullName -notmatch '[\\/]scripts[\\/]'
}

$hits = @()
foreach ($pattern in $blockedPatterns) {
    $m = $auditFiles | Select-String -Pattern $pattern -ErrorAction SilentlyContinue
    if ($m) { $hits += $m }
}

if ($hits.Count -gt 0) {
    Write-Host ''
    Write-Host 'BLOCKED: possible site-specific data found:' -ForegroundColor Red
    $hits | Select-Object Path, LineNumber, Line | Format-Table -AutoSize
    throw 'Repository import stopped by site-data audit.'
}

$forbiddenNames = @('*.astue','*.mpp','*.mbp','*.dmp','opc_snapshot.tsv','current_inventory.csv')
foreach ($mask in $forbiddenNames) {
    $found = Get-ChildItem -LiteralPath $Repo -Recurse -File -Filter $mask -ErrorAction SilentlyContinue | Where-Object {
        $_.FullName -notmatch '[\\/]\.git[\\/]'
    }
    if ($found) {
        $found | ForEach-Object { Write-Host "Forbidden file: $($_.FullName)" -ForegroundColor Red }
        throw "Repository import stopped: forbidden file mask $mask"
    }
}

Push-Location $Repo
try {
    if (-not (git config user.name)) {
        git config user.name 'ivan12000user'
    }
    if (-not (git config user.email)) {
        git config user.email '192781827+ivan12000user@users.noreply.github.com'
    }

    Write-Host ''
    Write-Host '===== GIT STATUS BEFORE COMMIT ====='
    git status --short

    git add -A
    $pending = git diff --cached --name-only
    if (-not $pending) {
        Write-Host 'No source changes to commit.'
    } else {
        git commit -m 'Import cleaned v0.3.2.4 field-tested source'
        git push origin main
    }

    $head = (git rev-parse HEAD).Trim()
    Write-Host "HEAD=$head"

    $tagExists = git tag -l 'v0.3.2.4'
    if (-not $tagExists) {
        git tag -a v0.3.2.4 -m 'First field-tested Windows 7 build'
        git push origin v0.3.2.4
    }

    $runtimeZip = Join-Path $Source 'dist\astue_mps_replacement_v0_3_2_4_win7_win11_x86.zip'
    if (Test-Path -LiteralPath $runtimeZip -PathType Leaf) {
        $sha = (Get-FileHash -LiteralPath $runtimeZip -Algorithm SHA256).Hash.ToLowerInvariant()
        $expected = '0cf0d42287ad10683f592418400f222cbeae268689c9cbcf3d2a5e80c642c7e6'
        Write-Host "Runtime SHA256=$sha"
        if ($sha -ne $expected) {
            throw "Runtime ZIP hash mismatch. Expected $expected"
        }

        $gh = 'C:\Program Files\GitHub CLI\gh.exe'
        if (-not (Test-Path -LiteralPath $gh -PathType Leaf)) {
            $ghCmd = Get-Command gh.exe -ErrorAction SilentlyContinue
            if ($ghCmd) { $gh = $ghCmd.Source }
        }
        if (-not (Test-Path -LiteralPath $gh -PathType Leaf)) {
            throw 'GitHub CLI not found; source/tag are pushed, but release was not created.'
        }

        & $gh release view v0.3.2.4 --repo ivan12000user/astue-mps-replacement *> $null
        if ($LASTEXITCODE -ne 0) {
            $notes = @"
First field-tested Windows 7 build.

Verified baseline:
- Windows 7 SP1 x64 / .NET Framework 4.0 Full, x86
- native OPC DA 2.05a LocalServer
- remote COM/DCOM activation with -Embedding
- OPC demand-start of the main polling application
- Mercury polling
- SET4 wizard support

Runtime ZIP SHA-256: $sha
"@
            & $gh release create v0.3.2.4 $runtimeZip --repo ivan12000user/astue-mps-replacement --title 'v0.3.2.4' --notes $notes
            if ($LASTEXITCODE -ne 0) { throw 'GitHub release creation failed.' }
        } else {
            Write-Host 'Release v0.3.2.4 already exists; leaving it unchanged.'
        }
    } else {
        Write-Host "Runtime ZIP not found: $runtimeZip" -ForegroundColor Yellow
        Write-Host 'Source and tag were pushed; release asset was skipped.' -ForegroundColor Yellow
    }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host '===== DONE =====' -ForegroundColor Green
Write-Host 'Repository: https://github.com/ivan12000user/astue-mps-replacement'
