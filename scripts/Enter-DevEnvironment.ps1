# Dot-source this script from PowerShell before running the documented commands.
# Keep SDK/NuGet caches inside the project for sandboxed agents.
$pdfNotesRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $pdfNotesRoot '.local\dotnet'
$env:NUGET_PACKAGES = Join-Path $pdfNotesRoot '.local\nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES | Out-Null
