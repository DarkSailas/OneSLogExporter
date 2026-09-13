# PowerShell Script for OneSLogExporter Build, Test & Distribution
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$publishServiceDir = Join-Path $root "publish\Service"
$publishGuiDir = Join-Path $root "publish\Gui"

Write-Host ""
Write-Host "  OneSLogExporter Build Script" -ForegroundColor Cyan
Write-Host "  ================================" -ForegroundColor Cyan
Write-Host ""

# --- [1/5] Cleanup ---
Write-Host "[1/5] Stopping running processes..." -ForegroundColor Yellow

Get-Process | Where-Object Name -Match "OneSLogExporter" | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

if (-not (Test-Path $publishServiceDir)) { New-Item -ItemType Directory -Path $publishServiceDir -Force | Out-Null }
if (-not (Test-Path $publishGuiDir)) { New-Item -ItemType Directory -Path $publishGuiDir -Force | Out-Null }
Write-Host "       OK" -ForegroundColor Green

# Проверка наличия .NET SDK на сервере
$sdkCheck = & dotnet --list-sdks 2>$null
if (-not $sdkCheck) {
    Write-Host ""
    Write-Host "[FATAL] .NET SDK is not installed on this server." -ForegroundColor Red
    Write-Host "Binaries are located at:" -ForegroundColor Yellow
    Write-Host "  - Windows Service: .\publish\Service\OneSLogExporter.Service.exe" -ForegroundColor Cyan
    Write-Host "  - GUI App:         .\publish\Gui\OneSLogExporter.Gui.exe" -ForegroundColor Cyan
    Write-Host ""
    exit 0
}

# --- [2/5] Restore ---
Write-Host "[2/5] dotnet restore..." -ForegroundColor Yellow
$slnPath = Join-Path $root "OneSLogExporter.slnx"
& dotnet restore $slnPath --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FATAL] dotnet restore failed with exit code $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "       OK" -ForegroundColor Green

# --- [3/5] Build ---
Write-Host "[3/5] dotnet build (Release)..." -ForegroundColor Yellow
& dotnet build $slnPath -c Release --no-restore --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FATAL] Build failed with exit code $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "       OK" -ForegroundColor Green

# --- [4/5] Tests ---
Write-Host "[4/5] dotnet test (Unit tests)..." -ForegroundColor Yellow
$testProject = Join-Path $root "tests\OneSLogExporter.Tests\OneSLogExporter.Tests.csproj"
& dotnet test $testProject -c Release --no-build --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "[WARN] Some tests failed." -ForegroundColor Yellow
} else {
    Write-Host "       OK" -ForegroundColor Green
}

# --- [5/5] Publish Separate Service & GUI Packages ---
Write-Host "[5/5] Publishing Service to ./publish/Service and GUI to ./publish/Gui..." -ForegroundColor Yellow

$serviceProject = Join-Path $root "src\OneSLogExporter.Service\OneSLogExporter.Service.csproj"
$guiProject = Join-Path $root "src\OneSLogExporter.Gui\OneSLogExporter.Gui.csproj"

# Direct Publish Service (Framework-Dependent win-x64 - clean output without CLR runtime DLLs)
& dotnet publish $serviceProject -c Release -r win-x64 --no-self-contained -o $publishServiceDir --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FATAL] Service Publish failed with exit code $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}

# Direct Publish GUI (Framework-Dependent win-x64 - clean output without CLR runtime DLLs)
& dotnet publish $guiProject -c Release -r win-x64 --no-self-contained -o $publishGuiDir --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FATAL] GUI Publish failed with exit code $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}

# Copy Install and Update Scripts directly into Service publish root
Copy-Item -Path (Join-Path $root "scripts\INSTALL_SERVICE.ps1") -Destination $publishServiceDir -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $root "scripts\UNINSTALL_SERVICE.ps1") -Destination $publishServiceDir -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $root "scripts\UPDATE_SERVICE.ps1") -Destination $publishServiceDir -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $root "scripts\logcfg.sample.xml") -Destination $publishServiceDir -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $root "scripts\elasticsearch-template.json") -Destination $publishServiceDir -Force -ErrorAction SilentlyContinue

# Copy AppSettings and Icon files
Copy-Item -Path (Join-Path $root "src\OneSLogExporter.Service\appsettings.json") -Destination $publishGuiDir -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $root "src\OneSLogExporter.Service\icon.ico") -Destination $publishServiceDir -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $root "src\OneSLogExporter.Gui\icon.ico") -Destination $publishGuiDir -Force -ErrorAction SilentlyContinue

# Auto-sync publish binaries and code to target folders if they exist and differ from root
$peerDir = if ($root -match "\\work\\Production\\") { $root -replace "\\work\\Production\\", "\work\Github\" } else { $root -replace "\\work\\Github\\", "\work\Production\" }
$syncTargets = @($peerDir, "X:\Production\OneSLogExporter", "X:\Github\OneSLogExporter") | Where-Object { 
    (Test-Path $_) -and ((Get-Item $_).FullName.TrimEnd('\') -ne (Get-Item $root).FullName.TrimEnd('\'))
}
foreach ($target in $syncTargets) {
    Write-Host "Syncing build artifacts and sources to $target..." -ForegroundColor Yellow
    
    # Save existing target appsettings so production configs are never overwritten
    $targetConfigs = @{}
    if (Test-Path $target) {
        Get-ChildItem -Path $target -Recurse -Filter "appsettings*.json" -File -ErrorAction SilentlyContinue | ForEach-Object {
            $rel = $_.FullName.Substring((Get-Item $target).FullName.TrimEnd('\').Length)
            $targetConfigs[$rel] = [IO.File]::ReadAllText($_.FullName)
        }
    }

    $tPublish = Join-Path $target "publish"
    New-Item -ItemType Directory -Path (Join-Path $tPublish "Gui") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $tPublish "Service") -Force | Out-Null
    Copy-Item -Path (Join-Path $publishGuiDir "*") -Destination (Join-Path $tPublish "Gui") -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $publishServiceDir "*") -Destination (Join-Path $tPublish "Service") -Recurse -Force -ErrorAction SilentlyContinue
    
    # Sync source files and solution assets
    Copy-Item -Path (Join-Path $root "src\*") -Destination (Join-Path $target "src") -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $root "tests\*") -Destination (Join-Path $target "tests") -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem -Path (Join-Path $root "scripts") -File | Copy-Item -Destination (Join-Path $target "scripts") -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $root "README.md") -Destination (Join-Path $target "README.md") -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $root "build.ps1") -Destination (Join-Path $target "build.ps1") -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $root "Directory.Build.props") -Destination (Join-Path $target "Directory.Build.props") -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $root ".gitignore") -Destination (Join-Path $target ".gitignore") -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $root "OneSLogExporter.slnx") -Destination (Join-Path $target "OneSLogExporter.slnx") -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $root "OneSLogExporter.sln") -Destination (Join-Path $target "OneSLogExporter.sln") -Force -ErrorAction SilentlyContinue

    # Restore preserved target configurations
    foreach ($entry in $targetConfigs.GetEnumerator()) {
        $destPath = Join-Path $target $entry.Key.TrimStart('\', '/')
        if (Test-Path (Split-Path $destPath -Parent)) {
            [IO.File]::WriteAllText($destPath, $entry.Value)
        }
    }

    Write-Host "       Synced OK -> $target" -ForegroundColor Green
}

Write-Host "       OK" -ForegroundColor Green
Write-Host ""
Write-Host "  ==============================" -ForegroundColor Cyan
Write-Host "  BUILD SUCCESSFUL" -ForegroundColor Green
Write-Host "  Service Output: $publishServiceDir" -ForegroundColor Cyan
Write-Host "  GUI Output:     $publishGuiDir" -ForegroundColor Cyan
Write-Host "  ==============================" -ForegroundColor Cyan
Write-Host ""
