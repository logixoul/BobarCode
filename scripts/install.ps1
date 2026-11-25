<#
.SYNOPSIS
  Publishes bobar-cli.exe and bobar.exe and installs them for the current user.

.DESCRIPTION
  1. dotnet publish of both exes (self-contained, single-file, win-x64) into dist\
  2. copies dist\ to the install dir (default %LOCALAPPDATA%\Programs\BobarCode)
  3. adds the install dir to the user PATH (so Claude can call bobar-cli)
  4. adds a Start menu shortcut for bobar.exe
  5. copies the breview, bplan, bexplore-cleanup and bdiscuss skills to ~\.claude\skills\
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\BobarCode"),
    [switch]$SkipPublish,
    [switch]$NoSkill
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"

if (-not $SkipPublish) {
    if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
    $common = @("-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true",
                "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:DebugType=none", "-o", $dist, "-nologo", "-v", "quiet")
    foreach ($proj in "src\Bobar.Cli\Bobar.Cli.csproj", "src\Bobar.App\Bobar.App.csproj") {
        Write-Host "Publishing $proj..."
        dotnet publish (Join-Path $root $proj) @common
        if ($LASTEXITCODE -ne 0) { throw "publish of $proj failed" }
    }
}

# A running GUI would lock bobar.exe.
Get-Process bobar -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$InstallDir*" } | ForEach-Object {
    Write-Host "Closing the running BobarCode..."
    $_.CloseMainWindow() | Out-Null
    if (-not $_.WaitForExit(5000)) { $_.Kill() }
}

New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item -Force (Join-Path $dist "*") $InstallDir -Recurse
Write-Host "Installed to $InstallDir"

$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
$parts = @($userPath -split ";" | Where-Object { $_ })
if ($parts -notcontains $InstallDir) {
    [Environment]::SetEnvironmentVariable("Path", (($parts + $InstallDir) -join ";"), "User")
    Write-Host "Added $InstallDir to the user PATH (open a new terminal to pick it up)."
}

$lnk = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\BobarCode.lnk"
$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($lnk)
$sc.TargetPath = Join-Path $InstallDir "bobar.exe"
$sc.WorkingDirectory = $InstallDir
$sc.Description = "BobarCode: review runs and decisions"
$sc.Save()
Write-Host "Start menu shortcut: $lnk"

if (-not $NoSkill) {
    foreach ($name in "breview", "bplan", "bexplore-cleanup", "bdiscuss") {
        $skillDir = Join-Path $env:USERPROFILE ".claude\skills\$name"
        New-Item -ItemType Directory -Force $skillDir | Out-Null
        Copy-Item -Force (Join-Path $root "skill\$name\SKILL.md") $skillDir
        Write-Host "Skill installed to $skillDir"
    }
    # The breview skill used to be called lx-review.
    $oldSkill = Join-Path $env:USERPROFILE ".claude\skills\lx-review"
    if (Test-Path $oldSkill) { Remove-Item -Recurse -Force $oldSkill }
}
Write-Host "Done. Put your provider keys in $env:APPDATA\BobarCode\keys.env (NAME=value lines)."
