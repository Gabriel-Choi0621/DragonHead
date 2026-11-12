# 용머리 알리미 빌드 스크립트
# 사용법: 이 폴더(DragonHead)에서 PowerShell 로   .\publish.ps1
#   - 실행 중인 DragonHead 를 끄고
#   - publish\DragonHead.exe 하나로 빌드한 뒤 (.NET 런타임 없는 PC 에서도 실행)
#   - 다시 실행합니다.
# "스크립트를 실행할 수 없습니다" 오류가 나면:  powershell -ExecutionPolicy Bypass -File .\publish.ps1

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

Write-Host '1/3  실행 중인 용머리 알리미 종료' -ForegroundColor Cyan
Get-Process DragonHead -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
dotnet build-server shutdown | Out-Null

Write-Host '2/3  빌드 (publish\DragonHead.exe)' -ForegroundColor Cyan
dotnet publish src/DragonHead.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
if ($LASTEXITCODE -ne 0) { Write-Host '빌드 실패. 위 오류 메시지를 확인하세요.' -ForegroundColor Red; exit 1 }

Write-Host '3/3  실행' -ForegroundColor Cyan
Start-Process -FilePath (Join-Path $PSScriptRoot 'publish\DragonHead.exe')
Write-Host '완료. 트레이에 용 아이콘이 생겼는지 확인하세요.' -ForegroundColor Green
