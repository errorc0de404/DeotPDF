# Builds the Claude Desktop extension of deot (an MCP bundle, .mcpb) for one system into artifacts/release:
#   pwsh tools/package-mcpb.ps1 -Rid win-x64      # DeotPDF-<v>-win-x64.mcpb
#   pwsh tools/package-mcpb.ps1 -Rid osx-arm64    # DeotPDF-<v>-macos-arm64.mcpb (osx-x64 likewise)
# The bundle is a zip: manifest.json (packaging/mcpb, with the version and the system filled in), icon.png, and
# server/ with a self-contained deot, so the user needs no .NET runtime. Installing it in Claude Desktop asks for the
# PDF folders and starts `deot mcp --root <folders>`. Check a bundle with `npx @anthropic-ai/mcpb validate <manifest>`.
# Build the macOS bundles on a Mac (file modes). Linux has no Claude Desktop, so there is no Linux bundle.
param(
    [Parameter(Mandatory)] [ValidateSet("win-x64", "osx-x64", "osx-arm64")] [string]$Rid,
    [string]$Version,
    [string]$OutDir = "artifacts/release"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$Version = $Version.Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "bad version '$Version'" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$windows = $Rid -like "win-*"
$platform = if ($windows) { "win32" } else { "darwin" }
$exe = if ($windows) { "deot.exe" } else { "deot" }
$name = if ($windows) { "DeotPDF-$Version-$Rid" } else { "DeotPDF-$Version-macos-$($Rid.Split('-')[1])" }

$top = Join-Path $OutDir "$name-mcpb"
if (Test-Path $top) { Remove-Item -Recurse -Force $top }
$server = Join-Path $top "server"
New-Item -ItemType Directory -Force $server | Out-Null

dotnet publish src/PdfNotes.Cli/PdfNotes.Cli.csproj -c Release -r $Rid --self-contained true "-p:RestoreLockedMode=true" "-p:Version=$Version" -o $server
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Get-ChildItem $server -Filter *.pdb | Remove-Item
if (-not $windows -and [Environment]::OSVersion.Platform -ne "Win32NT") { chmod +x (Join-Path $server $exe) }

$manifest = [IO.File]::ReadAllText("$root/packaging/mcpb/manifest.json").Replace("{VERSION}", $Version).Replace("{EXE}", $exe).Replace("{PLATFORM}", $platform)
[IO.File]::WriteAllText((Join-Path $top "manifest.json"), $manifest, (New-Object Text.UTF8Encoding $false))
Copy-Item packaging/mcpb/icon.png, LICENSE, THIRD-PARTY-NOTICES.md $top
$licenses = Join-Path $top "licenses"
foreach ($part in @("PDFium", "Clipper2", "ModelContextProtocol", "SkiaSharp")) { New-Item -ItemType Directory -Force "$licenses/$part" | Out-Null }
Copy-Item third_party/PDFium/LICENSE, third_party/PDFium/licenses/* "$licenses/PDFium"
Copy-Item third_party/Clipper2/License.txt "$licenses/Clipper2"
Copy-Item third_party/ModelContextProtocol/LICENSE "$licenses/ModelContextProtocol"
Copy-Item third_party/SkiaSharp/* "$licenses/SkiaSharp"

# A zip with forward slashes and manifest.json at its top (entries one by one: Windows PowerShell 5.1 would write backslashes).
$archive = Join-Path $OutDir "$name.mcpb"
if (Test-Path $archive) { Remove-Item -Force $archive }
if (-not $windows -and (Get-Command ditto -ErrorAction SilentlyContinue)) {
    ditto -c -k $top $archive # keeps file modes
    if ($LASTEXITCODE -ne 0) { throw "ditto failed" }
} else {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $top -Recurse -File | Sort-Object FullName) {
            $entry = $file.FullName.Substring($top.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $zip.Dispose() }
}
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash *$(Split-Path $archive -Leaf)" | Set-Content (Join-Path $OutDir "SHA256SUMS-$Version-$Rid-mcpb.txt") -Encoding ascii
"$archive ($([math]::Round((Get-Item $archive).Length / 1MB, 1)) MB)"
