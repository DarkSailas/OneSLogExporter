# PowerShell-скрипт удаления службы OneSLogExporter
$ErrorActionPreference = "Stop"
$serviceName = "OneSLogExporter"

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "[FATAL] Запустите скрипт от имени Администратора!" -ForegroundColor Red
    exit 1
}

$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $existingService) {
    Write-Host "Служба $serviceName не найдена в системе." -ForegroundColor Yellow
    exit 0
}

Write-Host "Остановка и удаление службы $serviceName..." -ForegroundColor Yellow
Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Stop-Process -Name mmc -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

sc.exe delete $serviceName | Out-Null
Start-Sleep -Seconds 2

Write-Host "Служба $serviceName успешно удалена!" -ForegroundColor Green