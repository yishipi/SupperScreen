param([switch]$SkipAndroid)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $workspace
try {
    dotnet test tests/MagicCompanion.Tests/MagicCompanion.Tests.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
    dotnet run --project tests/MagicCompanion.PlatformChecks -c Release
    if ($LASTEXITCODE -ne 0) { throw "PowerShell platform checks failed" }
    dotnet publish src/windows/MagicCompanion.Agent/MagicCompanion.Agent.csproj -c Release -r win-x64 --self-contained true -o artifacts/windows/SupperScreen-0.21.0 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Windows build failed' }
    if (-not $SkipAndroid) {
        Push-Location src/android
        try { & ./gradlew.bat :app:assembleDebug :app:testDebugUnitTest :app:lintDebug --console=plain; if ($LASTEXITCODE -ne 0) { throw 'Android checks failed' } }
        finally { Pop-Location }
        New-Item -ItemType Directory -Path artifacts/android -Force | Out-Null
        Copy-Item -LiteralPath src/android/app/build/outputs/apk/debug/app-debug.apk -Destination artifacts/android/SupperScreen-0.21.0-debug.apk -Force
    }
    Compress-Archive -Path artifacts/windows/SupperScreen-0.21.0 -DestinationPath artifacts/SupperScreen-0.21.0-win-x64.zip -Force
    Get-FileHash -Algorithm SHA256 -LiteralPath artifacts/SupperScreen-0.21.0-win-x64.zip
    if (-not $SkipAndroid) { Get-FileHash -Algorithm SHA256 -LiteralPath artifacts/android/SupperScreen-0.21.0-debug.apk }
}
finally { Pop-Location }
