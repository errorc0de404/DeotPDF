# Builds the two release zips (framework-dependent and portable) into artifacts/release.
# Used by .github/workflows/release.yml and runnable locally:
#   powershell -File tools/package-release.ps1                 # version from Directory.Build.props
#   powershell -File tools/package-release.ps1 -Version 1.1.0
# -Stage folders builds the release folders only, -Stage zip only zips folders already there
# (the CI signs the files in between); the default does both.
param(
    [string]$Version,
    [string]$OutDir = "artifacts/release",
    [ValidateSet("all", "folders", "zip")] [string]$Stage = "all"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

if (-not $Version) {
    $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$Version = $Version.Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "bad version '$Version'" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

function Build-Folder([string]$dir, [string]$selfContained) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    # The app and deot share one folder, so both must resolve the same package versions (PdfNotes.Pdf pins the shared ones).
    foreach ($project in @("src/PdfNotes.App/PdfNotes.App.csproj", "tools/PdfNotes.Cli/PdfNotes.Cli.csproj")) {
        dotnet publish $project -c Release -r win-x64 --self-contained $selfContained -p:RestoreLockedMode=true "-p:Version=$Version" -o $dir
        if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
    }
    Copy-Item LICENSE, THIRD-PARTY-NOTICES.md, tools/register-open-with.ps1 $dir
    $readme = [IO.File]::ReadAllText("$root/tools/release-readme.txt").Replace("{VERSION}", $Version)
    [IO.File]::WriteAllText((Join-Path $dir "README.txt"), $readme, (New-Object Text.UTF8Encoding $true))
    $licenses = Join-Path $dir "licenses"
    New-Item -ItemType Directory -Force "$licenses/PDFium", "$licenses/Clipper2", "$licenses/ModelContextProtocol" | Out-Null
    Copy-Item third_party/PDFium/LICENSE, third_party/PDFium/licenses/* "$licenses/PDFium"
    Copy-Item third_party/Clipper2/License.txt "$licenses/Clipper2"
    Copy-Item third_party/ModelContextProtocol/LICENSE "$licenses/ModelContextProtocol"
}

$sums = @()
foreach ($kind in @("fd", "portable")) {
    $name = if ($kind -eq "fd") { "DeotPDF-$Version-win-x64" } else { "DeotPDF-$Version-win-x64-portable" }
    $dir = Join-Path $OutDir $name
    if ($Stage -ne "zip") { Build-Folder $dir $(if ($kind -eq "fd") { "false" } else { "true" }) }
    if ($Stage -eq "folders") { continue }
    if (-not (Test-Path $dir)) { throw "missing $dir" }

    $zip = Join-Path $OutDir "$name.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    # Top-level folder kept in the zip, like the earlier releases. Entries are added one by one because
    # ZipFile.CreateFromDirectory on Windows PowerShell 5.1 writes backslashes into the entry names.
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $dir -Recurse -File | Sort-Object FullName) {
            $entry = "$name/" + $file.FullName.Substring($dir.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose() }
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums += "$hash *$name.zip"
}

if ($sums.Count -gt 0) {
    $sumFile = Join-Path $OutDir "SHA256SUMS-$Version.txt"
    $sums | Set-Content $sumFile -Encoding ascii
    Get-Content $sumFile
}
