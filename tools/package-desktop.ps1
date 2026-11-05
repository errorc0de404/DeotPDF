# Builds the release package of the cross-platform app (roadmap P4 A7) for one system, with the WPF-free CLI next to it.
#   pwsh tools/package-desktop.ps1 -Rid linux-x64            # DeotPDF-<version>-linux-x64.tar.gz
#   pwsh tools/package-desktop.ps1 -Rid osx-arm64            # DeotPDF-<version>-macos-arm64.zip (DeotPDF.app inside)
#   pwsh tools/package-desktop.ps1 -Rid osx-x64              # DeotPDF-<version>-macos-x64.zip
#   pwsh tools/package-desktop.ps1 -Rid win-x64              # DeotPDF-<version>-win-x64-next.zip (DeotPDF.Next.exe)
# Self-contained: no .NET install needed. The macOS app is not signed by a developer ID (decision 18): on a Mac it is
# signed ad hoc (codesign) so Apple silicon runs it, and the first start needs right-click > Open. Build the Linux
# package on Linux and the macOS ones on macOS (file modes, codesign); other systems can build them for a look.
# Restore is not in locked mode: the tracked lock files hold the Windows runtime only (the WPF release stays locked),
# so this restore writes its own lock file under each project's obj folder and leaves the tracked ones alone.
param(
    [Parameter(Mandatory)] [ValidateSet("win-x64", "linux-x64", "osx-x64", "osx-arm64")] [string]$Rid,
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

# An .icns from the PNG images of the Windows icon (16 to 256 pixels); Retina sizes reuse the larger images.
function Write-Icns([string]$ico, [string]$target) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $root $ico))
    $images = @{}
    $count = [BitConverter]::ToUInt16($bytes, 4)
    for ($i = 0; $i -lt $count; $i++) {
        $entry = 6 + 16 * $i
        $size = if ($bytes[$entry] -eq 0) { 256 } else { [int]$bytes[$entry] }
        $length = [BitConverter]::ToInt32($bytes, $entry + 8)
        $offset = [BitConverter]::ToInt32($bytes, $entry + 12)
        $images[$size] = $bytes[$offset..($offset + $length - 1)]
    }
    $types = [ordered]@{ "icp4" = 16; "icp5" = 32; "icp6" = 64; "ic07" = 128; "ic08" = 256; "ic11" = 32; "ic12" = 64; "ic13" = 256 }
    $body = New-Object IO.MemoryStream
    foreach ($type in $types.Keys) {
        if (-not $images.ContainsKey($types[$type])) { continue }
        [byte[]]$png = $images[$types[$type]]
        $body.Write([Text.Encoding]::ASCII.GetBytes($type), 0, 4)
        $body.Write((BigEndian (8 + $png.Length)), 0, 4)
        $body.Write($png, 0, $png.Length)
    }
    $out = New-Object IO.MemoryStream
    $out.Write([Text.Encoding]::ASCII.GetBytes("icns"), 0, 4)
    $out.Write((BigEndian (8 + $body.Length)), 0, 4)
    $body.WriteTo($out)
    [IO.File]::WriteAllBytes($target, $out.ToArray())
}

function BigEndian([long]$value) {
    $b = [BitConverter]::GetBytes([uint32]$value)
    [Array]::Reverse($b)
    return ,$b
}

$system = switch -Wildcard ($Rid) { "win-*" { "windows" } "linux-*" { "linux" } "osx-*" { "macos" } }
$arch = $Rid.Split("-")[1]
$name = switch ($system) { "windows" { "DeotPDF-$Version-win-x64-next" } "linux" { "DeotPDF-$Version-linux-$arch" } "macos" { "DeotPDF-$Version-macos-$arch" } }
$top = Join-Path $OutDir $name
if (Test-Path $top) { Remove-Item -Recurse -Force $top }
New-Item -ItemType Directory -Force $top | Out-Null

# The app files: next to the readme on Windows and Linux, inside DeotPDF.app/Contents/MacOS on macOS.
$bin = if ($system -eq "macos") { Join-Path $top "DeotPDF.app/Contents/MacOS" } else { $top }
foreach ($project in @("src/PdfNotes.Desktop/PdfNotes.Desktop.csproj", "tools/PdfNotes.Cli/PdfNotes.Cli.csproj")) {
    dotnet publish $project -c Release -r $Rid --self-contained true -p:DeotEngine=Skia "-p:NuGetLockFilePath=obj/packages.$Rid.lock.json" "-p:Version=$Version" -o $bin
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
}
Get-ChildItem $bin -Filter *.pdb | Remove-Item

# The app is DeotPDF on macOS and Linux (no WPF app beside it); the host finds DeotPDF.Next.dll by its own name.
$exe = "DeotPDF.Next"
if ($system -ne "windows") {
    Move-Item (Join-Path $bin "DeotPDF.Next") (Join-Path $bin "DeotPDF") -Force
    $exe = "DeotPDF"
}

# Licenses and the readme.
Copy-Item LICENSE, THIRD-PARTY-NOTICES.md $top
$licenses = Join-Path $top "licenses"
foreach ($part in @("PDFium", "Clipper2", "ModelContextProtocol", "Avalonia", "SkiaSharp", "FluentUISystemIcons")) {
    New-Item -ItemType Directory -Force "$licenses/$part" | Out-Null
}
Copy-Item third_party/PDFium/LICENSE, third_party/PDFium/licenses/* "$licenses/PDFium"
Copy-Item third_party/Clipper2/License.txt "$licenses/Clipper2"
Copy-Item third_party/ModelContextProtocol/LICENSE "$licenses/ModelContextProtocol"
Copy-Item third_party/Avalonia/* "$licenses/Avalonia"
Copy-Item third_party/SkiaSharp/* "$licenses/SkiaSharp"
Copy-Item third_party/FluentUISystemIcons/LICENSE "$licenses/FluentUISystemIcons"
$readme = [IO.File]::ReadAllText("$root/tools/release-readme-$system.txt").Replace("{VERSION}", $Version)
[IO.File]::WriteAllText((Join-Path $top "README.txt"), $readme.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))

switch ($system) {
    "windows" {
        Copy-Item tools/register-open-with.ps1 $top
    }
    "linux" {
        Copy-Item packaging/linux/deotpdf.desktop, packaging/linux/register-open-with.sh $top
        Copy-Item src/PdfNotes.App/Assets/DeotPDF.svg (Join-Path $top "deotpdf.svg")
        if ([Environment]::OSVersion.Platform -ne "Win32NT") { chmod +x (Join-Path $top "DeotPDF") (Join-Path $top "deot") (Join-Path $top "register-open-with.sh") }
    }
    "macos" {
        $contents = Join-Path $top "DeotPDF.app/Contents"
        $plist = [IO.File]::ReadAllText("$root/packaging/macos/Info.plist").Replace("{EXECUTABLE}", $exe).Replace("{VERSION}", $Version)
        [IO.File]::WriteAllText((Join-Path $contents "Info.plist"), $plist, (New-Object Text.UTF8Encoding $false))
        New-Item -ItemType Directory -Force (Join-Path $contents "Resources") | Out-Null
        Write-Icns "src/PdfNotes.App/Assets/DeotPDF.ico" (Join-Path $contents "Resources/DeotPDF.icns")
        if (Get-Command codesign -ErrorAction SilentlyContinue) {
            codesign --force --deep --sign - (Join-Path $top "DeotPDF.app")
            if ($LASTEXITCODE -ne 0) { throw "codesign failed" }
        }
    }
}

# The archive keeps the top folder, like the Windows zips.
$archive = if ($system -eq "linux") { Join-Path $OutDir "$name.tar.gz" } else { Join-Path $OutDir "$name.zip" }
if (Test-Path $archive) { Remove-Item -Force $archive }
if ($system -eq "linux") {
    tar -C $OutDir -czf $archive $name
} elseif ($system -eq "macos" -and (Get-Command ditto -ErrorAction SilentlyContinue)) {
    ditto -c -k --keepParent $top $archive # keeps file modes and the app's signature
} else {
    Compress-Archive -Path $top -DestinationPath $archive
}
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "archive failed" }
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash *$(Split-Path $archive -Leaf)" | Set-Content (Join-Path $OutDir "SHA256SUMS-$Version-$Rid.txt") -Encoding ascii
Get-Content (Join-Path $OutDir "SHA256SUMS-$Version-$Rid.txt")
