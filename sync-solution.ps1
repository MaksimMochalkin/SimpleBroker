#Requires -Version 5.1
<#
.SYNOPSIS
    Syncs the physical folder structure with the logical .slnx Solution Folder structure.
    The .slnx is the source of truth; the disk follows it.
.DESCRIPTION
    Rules:
      project path = <SolutionFolder>/<ProjectName>/<ProjectName>.csproj
      file path    = <SolutionFolder>/<FileName>   (except the virtual "Solution Items" folder: files stay where they are)
.PARAMETER SlnxPath
    Path to .slnx file. Searches current directory by default.
.PARAMETER DryRun
    Show what would be done without making changes (exit code 0).
.PARAMETER Check
    Report drift only and exit with code 1 if anything is out of sync. For CI. Never changes files.
.PARAMETER UseGit
    Use "git mv" (falls back to a plain move for untracked paths).
#>
param(
    [string]$SlnxPath = "",
    [switch]$DryRun,
    [switch]$Check,
    [switch]$UseGit
)

$ErrorActionPreference = 'Stop'
$Sep = [IO.Path]::DirectorySeparatorChar
$SkipDirRegex = '(^|[\\/])(bin|obj|node_modules|\.git|\.vs|TestResults|\.sync-tmp-[0-9a-f]+)([\\/]|$)'

# --------------------------------------------------
# Helpers
# --------------------------------------------------
function Write-Header([string]$text) {
    Write-Host ""
    Write-Host "===========================================" -ForegroundColor DarkCyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host "===========================================" -ForegroundColor DarkCyan
}
function Write-Step([string]$text)   { Write-Host "  -> $text" -ForegroundColor Gray }
function Write-Action([string]$text) { Write-Host "  OK $text" -ForegroundColor Green }
function Write-Skip([string]$text)   { Write-Host "   . $text" -ForegroundColor DarkGray }
function Write-Warn([string]$text)   { Write-Host "  !! $text" -ForegroundColor Yellow }
function Write-Err([string]$text)    { Write-Host "  XX $text" -ForegroundColor Red }

function Convert-Sep([string]$p) { return ($p -replace '[\\/]', $Sep) }

function Get-RelativePath([string]$fromDir, [string]$toPath) {
    $fromUri = New-Object System.Uri($fromDir.TrimEnd('\', '/') + $Sep)
    $toUri   = New-Object System.Uri($toPath)
    $rel     = [Uri]::UnescapeDataString($fromUri.MakeRelativeUri($toUri).ToString())
    return (Convert-Sep $rel)
}

function Get-RootRelative([string]$abs) {
    return $abs.Substring($SolutionRoot.TrimEnd('\', '/').Length).TrimStart('\', '/')
}

# Read/write text preserving the BOM state of the original file (avoids noisy diffs)
function Read-Text([string]$path) {
    $bytes  = [IO.File]::ReadAllBytes($path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $start  = if ($hasBom) { 3 } else { 0 }
    $text   = [Text.Encoding]::UTF8.GetString($bytes, $start, $bytes.Length - $start)
    return @{ Text = $text; Bom = $hasBom }
}
function Write-Text([string]$path, [string]$text, [bool]$bom) {
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($bom)))
}

# --------------------------------------------------
# Locate .slnx
# --------------------------------------------------
Write-Header "Sync Solution Structure"
if (-not $SlnxPath) {
    $found = Get-ChildItem -Filter "*.slnx" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) {
        Write-Err ".slnx file not found in current directory. Use -SlnxPath to specify."
        exit 1
    }
    $SlnxPath = $found.FullName
}
if (-not (Test-Path $SlnxPath)) {
    Write-Err "File not found: $SlnxPath"
    exit 1
}
$SlnxPath     = (Resolve-Path $SlnxPath).Path
$SolutionRoot = Split-Path $SlnxPath -Parent
Write-Step "Solution root : $SolutionRoot"
Write-Step "Slnx file     : $(Split-Path $SlnxPath -Leaf)"
if ($Check)  { Write-Step "Check mode: report drift only, exit 1 if found" }
if ($DryRun) { Write-Warn "DryRun mode - no changes will be made" }
if ($UseGit) { Write-Step "Using git mv" }

# --------------------------------------------------
# Parse .slnx
# --------------------------------------------------
Write-Header "Parsing .slnx"
[xml]$slnx = (Read-Text $SlnxPath).Text
$folderMap = [ordered]@{}
foreach ($folder in $slnx.Solution.Folder) {
    $folderName = $folder.Name
    if (-not $folderMap.Contains($folderName)) {
        $folderMap[$folderName] = [System.Collections.Generic.List[hashtable]]::new()
    }
    foreach ($proj in $folder.Project) {
        $folderMap[$folderName].Add(@{ Type = "Project"; Path = $proj.Path })
    }
    foreach ($file in $folder.File) {
        $folderMap[$folderName].Add(@{ Type = "File"; Path = $file.Path })
    }
}
Write-Step "Found Solution Folders: $($folderMap.Count)"

# --------------------------------------------------
# Target paths: project -> <Folder>/<Name>/<Name>.csproj ; file -> <Folder>/<File>
# --------------------------------------------------
function Get-TargetPath([string]$folderName, [string]$currentPath, [string]$itemType) {
    $logicalDir = $folderName.Trim('/')
    $leaf = Split-Path $currentPath -Leaf
    # "Solution Items" is a virtual folder for repo-level files (README, .editorconfig, scripts): never moved, only checked for existence
    if ($itemType -eq "File" -and $logicalDir -eq "Solution Items") { return $currentPath }
    if ($itemType -eq "Project") {
        $projectDir = [IO.Path]::GetFileNameWithoutExtension($leaf)
        return "$logicalDir/$projectDir/$leaf"
    }
    return "$logicalDir/$leaf"
}

# --------------------------------------------------
# Build move plan + drift report
# --------------------------------------------------
Write-Header "Analyzing"
$moves       = [System.Collections.Generic.List[hashtable]]::new()
$pathUpdates = [ordered]@{}
$missing     = [System.Collections.Generic.List[string]]::new()
$listedAbs   = @{}

foreach ($folderName in $folderMap.Keys) {
    foreach ($item in $folderMap[$folderName]) {
        $currentPath = $item.Path
        $targetPath  = Get-TargetPath $folderName $currentPath $item.Type
        $fromAbs     = Join-Path $SolutionRoot (Convert-Sep $currentPath)
        $toAbs       = Join-Path $SolutionRoot (Convert-Sep $targetPath)

        if ($item.Type -eq "Project") { $listedAbs[$fromAbs.ToLower()] = $true }

        if ($currentPath -eq $targetPath) {
            if (Test-Path -LiteralPath $fromAbs) { Write-Skip "OK      $currentPath" }
            else { Write-Warn "MISSING $currentPath (listed in .slnx, absent on disk)"; $missing.Add($currentPath) }
            continue
        }

        $sourceExists = Test-Path -LiteralPath $fromAbs
        if (-not $sourceExists -and $item.Type -eq "File") {
            $rootFallback = Join-Path $SolutionRoot (Split-Path $fromAbs -Leaf)
            if (Test-Path -LiteralPath $rootFallback) { $fromAbs = $rootFallback; $sourceExists = $true }
        }
        if (-not $sourceExists) {
            Write-Warn "NOT FOUND  $currentPath"
            $missing.Add($currentPath)
            continue
        }

        Write-Action "PLAN MOVE"
        Write-Host "           FROM: $fromAbs" -ForegroundColor DarkYellow
        Write-Host "           TO  : $toAbs"   -ForegroundColor DarkGreen

        $isProject = ($item.Type -eq "Project")
        $moves.Add(@{
            Type    = $item.Type
            From    = $fromAbs                                              # csproj or file
            To      = $toAbs
            Src     = if ($isProject) { Split-Path $fromAbs -Parent } else { $fromAbs }   # what is physically moved
            Dst     = if ($isProject) { Split-Path $toAbs   -Parent } else { $toAbs }
            OldPath = $currentPath
            NewPath = $targetPath
        })
        $pathUpdates[$currentPath] = $targetPath
    }
}

# csproj on disk but not listed in .slnx
$unlisted = @(Get-ChildItem -Path $SolutionRoot -Filter *.csproj -Recurse -File |
    Where-Object { (Get-RootRelative $_.FullName) -notmatch $SkipDirRegex -and -not $listedAbs.ContainsKey($_.FullName.ToLower()) })
foreach ($u in $unlisted) { Write-Warn "NOT IN SLNX  $(Get-RootRelative $u.FullName)" }

Write-Host ""
Write-Host "  Moves planned: $($moves.Count) | missing: $($missing.Count) | not in .slnx: $($unlisted.Count)" -ForegroundColor Cyan

if ($Check) {
    if ($moves.Count -gt 0 -or $missing.Count -gt 0 -or $unlisted.Count -gt 0) {
        Write-Err "Structure is out of sync. Run sync-solution.ps1 -DryRun to see details, then fix."
        exit 1
    }
    Write-Host "  In sync." -ForegroundColor Green
    exit 0
}
if ($moves.Count -eq 0) {
    Write-Host "  Everything is already in place. Nothing to do." -ForegroundColor Green
    exit 0
}
if ($DryRun) { Write-Warn "DryRun: exiting without changes"; exit 0 }

if ($UseGit) {
    $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $dirty = git -C $SolutionRoot status --porcelain 2>$null
    $ErrorActionPreference = $prevEap
    if ($dirty) { Write-Warn "Working tree is not clean. Commit or stash first so the move is a separate, reviewable commit." }
}

# --------------------------------------------------
# Move (two-phase through a staging folder: safe for nesting, swaps, flattening)
# --------------------------------------------------
function Move-Smart([string]$src, [string]$dst) {
    $parent = Split-Path $dst -Parent
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }

    if ($UseGit) {
        $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'   # PS 5.1 turns native stderr into errors under 'Stop'
        git -C $SolutionRoot mv -- $src $dst 2>&1 | Out-Null
        $gitCode = $LASTEXITCODE
        $ErrorActionPreference = $prevEap
        if ($gitCode -eq 0) { return }
        Write-Warn "git mv failed for '$src' (untracked or locked); using filesystem move"
    }

    if (Test-Path -LiteralPath $src -PathType Container) {
        $srcTrim = $src.TrimEnd('\', '/')
        # move source files except bin/obj (they may be locked by Visual Studio)
        Get-ChildItem -LiteralPath $src -Recurse -Force -File | ForEach-Object {
            $rel = $_.FullName.Substring($srcTrim.Length + 1)
            if ($rel -match '^(bin|obj)([\\/]|$)' -or $rel -match '[\\/](bin|obj)[\\/]') { return }
            $target = Join-Path $dst $rel
            $td = Split-Path $target -Parent
            if (-not (Test-Path -LiteralPath $td)) { New-Item -ItemType Directory -Path $td -Force | Out-Null }
            Move-Item -LiteralPath $_.FullName -Destination $target -Force
        }
        Remove-Item -LiteralPath $src -Recurse -Force -ErrorAction SilentlyContinue
    }
    else {
        Move-Item -LiteralPath $src -Destination $dst -Force
    }
}

Write-Header "Moving"
$stageRoot = Join-Path $SolutionRoot (".sync-tmp-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$idx = 0
foreach ($m in $moves) {
    $idx++
    $m.Stage = Join-Path $stageRoot ("{0:D3}_{1}" -f $idx, (Split-Path $m.Src -Leaf))
}

$staged = [System.Collections.Generic.List[hashtable]]::new()
try {
    foreach ($m in $moves) {                       # phase 1: sources -> staging
        Move-Smart $m.Src $m.Stage
        $staged.Add($m)
    }
    foreach ($m in $moves) {                       # phase 2: staging -> targets
        if (Test-Path -LiteralPath $m.Dst) {
            $isEmptyDir = (Test-Path -LiteralPath $m.Dst -PathType Container) -and
                          -not (Get-ChildItem -LiteralPath $m.Dst -Force | Select-Object -First 1)
            if ($isEmptyDir) { Remove-Item -LiteralPath $m.Dst -Force }
            else { throw "Target already exists and is not empty: $($m.Dst)" }
        }
        Move-Smart $m.Stage $m.Dst
        $m.Done = $true
        Write-Action "Moved: $(Get-RootRelative $m.Src)  ->  $(Get-RootRelative $m.Dst)"
    }
}
catch {
    Write-Err "Move failed: $_"
    Write-Warn "Rolling back items that are still in staging..."
    foreach ($m in $staged) {
        if (-not $m.Done -and (Test-Path -LiteralPath $m.Stage)) {
            try { Move-Smart $m.Stage $m.Src } catch { Write-Err "Could not restore $($m.Src); it is in $($m.Stage)" }
        }
    }
    Write-Warn "Items already moved to targets were left in place. .slnx and .csproj were NOT updated."
    exit 1
}
if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue }

# remove now-empty parent folders left behind
foreach ($m in $moves) {
    $d = if (Test-Path -LiteralPath $m.Src -PathType Container) { $m.Src } else { Split-Path $m.Src -Parent }
    while ($d -and $d.Length -gt $SolutionRoot.TrimEnd('\', '/').Length -and (Test-Path -LiteralPath $d) -and
           -not (Get-ChildItem -LiteralPath $d -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $d -Force
        $d = Split-Path $d -Parent
    }
}
# stale obj/project.assets.json contain absolute old paths
foreach ($m in $moves | Where-Object { $_.Type -eq "Project" }) {
    Remove-Item -LiteralPath (Join-Path $m.Dst 'obj') -Recurse -Force -ErrorAction SilentlyContinue
}

# --------------------------------------------------
# Update .slnx (exact Path match only: no substring/chain replacement)
# --------------------------------------------------
Write-Header "Updating .slnx"
$r = Read-Text $SlnxPath
$newText = [regex]::Replace($r.Text, '(\bPath\s*=\s*")([^"]+)(")', [System.Text.RegularExpressions.MatchEvaluator]{
    param($m)
    $p = $m.Groups[2].Value
    if ($pathUpdates.Contains($p)) { return $m.Groups[1].Value + $pathUpdates[$p] + $m.Groups[3].Value }
    return $m.Value
})
Write-Text $SlnxPath $newText $r.Bom
foreach ($k in $pathUpdates.Keys) { Write-Action "$k  ->  $($pathUpdates[$k])" }

# --------------------------------------------------
# Update <ProjectReference> in all .csproj
# A reference is resolved against the referencing project's OLD folder, mapped to the
# referenced project's NEW location, then written relative to the referencing project's NEW folder.
# --------------------------------------------------
Write-Header "Updating ProjectReference"
$oldToNew = @{}        # old csproj abs (lower) -> new csproj abs
$newDirToOldDir = @{}  # new project dir (lower) -> old project dir
foreach ($m in $moves | Where-Object { $_.Type -eq "Project" }) {
    $oldToNew[[IO.Path]::GetFullPath($m.From).ToLower()] = [IO.Path]::GetFullPath($m.To)
    $newDirToOldDir[$m.Dst.ToLower()] = $m.Src
}

$csprojs = Get-ChildItem -Path $SolutionRoot -Filter *.csproj -Recurse -File |
    Where-Object { (Get-RootRelative $_.FullName) -notmatch $SkipDirRegex }

foreach ($cs in $csprojs) {
    $newDir = $cs.DirectoryName
    $oldDir = if ($newDirToOldDir.ContainsKey($newDir.ToLower())) { $newDirToOldDir[$newDir.ToLower()] } else { $newDir }
    $state  = @{ Changed = $false }
    $rt     = Read-Text $cs.FullName

    $updated = [regex]::Replace($rt.Text, '(<ProjectReference\b[^>]*?\bInclude\s*=\s*")([^"]+)(")', [System.Text.RegularExpressions.MatchEvaluator]{
        param($m)
        $rel = $m.Groups[2].Value
        if ($rel -match '\$\(') { return $m.Value }       # MSBuild property inside the path: leave as is
        $abs    = [IO.Path]::GetFullPath((Join-Path $oldDir (Convert-Sep $rel)))
        $target = if ($oldToNew.ContainsKey($abs.ToLower())) { $oldToNew[$abs.ToLower()] } else { $abs }
        $newRel = Get-RelativePath $newDir $target
        if ($newRel -ieq (Convert-Sep $rel)) { return $m.Value }
        $state.Changed = $true
        Write-Action "$($cs.Name): $rel  ->  $newRel"
        return $m.Groups[1].Value + $newRel + $m.Groups[3].Value
    })

    if ($state.Changed) { Write-Text $cs.FullName $updated $rt.Bom }

    if ($oldDir -ne $newDir) {   # moved project: non-ProjectReference relative paths are not rewritten automatically
        foreach ($line in ($rt.Text -split "`r?`n")) {
            if ($line -match '"\.\.[\\/]' -and $line -notmatch 'ProjectReference') {
                Write-Warn "$($cs.Name): check relative path manually: $($line.Trim())"
            }
        }
    }
}

# --------------------------------------------------
# Other files that may mention old paths (pipelines, scripts, docs): report only
# --------------------------------------------------
Write-Header "Scanning other files for old paths (report only)"
$patterns = @()
foreach ($m in $moves | Where-Object { $_.Type -eq "Project" }) {
    $old = (Get-RootRelative $m.Src)
    $patterns += ($old -replace '\\', '/')
    $patterns += ($old -replace '/', '\')
}
$patterns = $patterns | Select-Object -Unique
$textFiles = Get-ChildItem -Path $SolutionRoot -Recurse -File -Include *.yml, *.yaml, *.json, *.ps1, *.props, *.targets, *.md, *.config, Dockerfile* |
    Where-Object { (Get-RootRelative $_.FullName) -notmatch $SkipDirRegex -and $_.FullName -ne $SlnxPath }
$hits = 0
if ($patterns.Count -gt 0 -and $textFiles) {
    foreach ($h in (Select-String -Path $textFiles.FullName -SimpleMatch $patterns)) {
        Write-Warn "$(Get-RootRelative $h.Path):$($h.LineNumber): $($h.Line.Trim())"
        $hits++
    }
}
if ($hits -eq 0) { Write-Skip "no references to old paths found" }

# --------------------------------------------------
# Summary
# --------------------------------------------------
Write-Header "Done"
Write-Host "  Items moved   : $($moves.Count)" -ForegroundColor Green
Write-Host "  Paths updated : $($pathUpdates.Count)" -ForegroundColor Green
Write-Host ""
Write-Host "  Next steps:" -ForegroundColor Cyan
Write-Host "  1. Fix the warnings above (pipelines, non-ProjectReference paths)" -ForegroundColor Gray
Write-Host "  2. dotnet restore && dotnet build -v q --nologo" -ForegroundColor Gray
Write-Host "  3. Reload solution in Visual Studio, review 'git status', commit as a separate 'refactor: sync folder structure'" -ForegroundColor Gray
Write-Host ""