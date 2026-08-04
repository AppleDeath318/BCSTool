# ============================================================
# Publish-SingleExe.ps1
# ============================================================
#
# This script asks the .NET SDK to create a Release build for Windows x64.
#
# --self-contained true
#     Bundles the .NET runtime so the destination machine does not need to
#     install .NET separately.
#
# PublishSingleFile=true
#     Packs the managed application into a single primary BCS Tool.exe.
#
# IncludeNativeLibrariesForSelfExtract=true
#     Allows native WPF/runtime components to be included in single-file
#     publishing.
#
# Run from PowerShell:
#
#   powershell.exe -ExecutionPolicy Bypass -File .\Publish-SingleExe.ps1
#
# ============================================================

$ErrorActionPreference = "Stop"

$Project = Join-Path $PSScriptRoot "BCSTool.csproj"
$PublishDirectory = Join-Path $PSScriptRoot "publish"

if (Test-Path $PublishDirectory) {
    Remove-Item $PublishDirectory -Recurse -Force
}

dotnet publish $Project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $PublishDirectory `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:DebugType=None `
    /p:DebugSymbols=false

Write-Host ""
Write-Host "Publish complete:"
Write-Host "  $PublishDirectory"
Write-Host "  $(Join-Path $PublishDirectory 'BCS Tool.exe')"
Write-Host ""
Write-Host "BCS Tool settings are stored in:"
Write-Host "  HKEY_CURRENT_USER\Software\BCSServerTool"
Write-Host ""
Write-Host "No settings.json file is required."
