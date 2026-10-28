<#
    fetch_deps.ps1 - populate lib\, models\ and tools\sdk\ from the internet.

    Run this once on a networked PC (not the locked-down work PC), then copy the
    whole SnipOCR folder. build.cmd needs no network.

      powershell -ExecutionPolicy Bypass -File tools\fetch_deps.ps1

    - lib\        ONNX Runtime (managed + native) and its .NET Framework shims
    - models\     PP-OCRv6 small detector / recognizer, SHA256 verified
    - tools\sdk\  winmd metadata so build.cmd works without the Windows SDK
#>
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [switch]$SkipModels,
    [switch]$SkipSdk
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-Package {
    param([string]$Id, [string]$Version, [string]$OutFile)
    $url = "https://api.nuget.org/v3-flatcontainer/$($Id.ToLower())/$Version/$($Id.ToLower()).$Version.nupkg"
    if (Test-Path $OutFile) { return $OutFile }
    Write-Host "  downloading $Id $Version"
    Invoke-WebRequest -Uri $url -OutFile $OutFile -UseBasicParsing
    return $OutFile
}

function Expand-From {
    param([string]$Package, [string]$Pattern, [string]$Destination, [string]$NewName)
    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $hit = $zip.Entries | Where-Object { $_.FullName -match $Pattern } | Select-Object -First 1
        if (-not $hit) { throw "pattern not found in ${Package}: $Pattern" }
        $target = Join-Path $Destination ($(if ($NewName) { $NewName } else { Split-Path $hit.FullName -Leaf }))
        [IO.Compression.ZipFileExtensions]::ExtractToFile($hit, $target, $true)
    }
    finally { $zip.Dispose() }
}

$lib = Join-Path $Root 'lib'
$models = Join-Path $Root 'models'
$sdk = Join-Path $Root 'tools\sdk'
foreach ($d in @($lib, $models, $sdk)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Force $d | Out-Null }
}
$tmp = Join-Path ([IO.Path]::GetTempPath()) 'snipocr_deps'
if (-not (Test-Path $tmp)) { New-Item -ItemType Directory -Force $tmp | Out-Null }

Write-Host '== ONNX Runtime =='
$ort = Get-Package -Id 'Microsoft.ML.OnnxRuntime' -Version '1.31.0' -OutFile (Join-Path $tmp 'ort.nupkg')
$ortm = Get-Package -Id 'Microsoft.ML.OnnxRuntime.Managed' -Version '1.31.0' -OutFile (Join-Path $tmp 'ortm.nupkg')
Expand-From $ort 'runtimes/win-x64/native/onnxruntime\.dll$' $lib
Expand-From $ort 'runtimes/win-x64/native/onnxruntime_providers_shared\.dll$' $lib
Expand-From $ortm '^lib/netstandard2\.0/Microsoft\.ML\.OnnxRuntime\.dll$' $lib

Write-Host '== .NET Framework shims (netstandard2.0 -> net461) =='
$mem = Get-Package -Id 'System.Memory' -Version '4.5.5' -OutFile (Join-Path $tmp 'mem.nupkg')
$buf = Get-Package -Id 'System.Buffers' -Version '4.5.1' -OutFile (Join-Path $tmp 'buf.nupkg')
# 4.5.3 is deliberate: System.Memory 4.5.5 references Unsafe 4.0.4.1, and a newer
# 6.x build would need an app.config binding redirect.
$uns = Get-Package -Id 'System.Runtime.CompilerServices.Unsafe' -Version '4.5.3' -OutFile (Join-Path $tmp 'uns.nupkg')
Expand-From $mem '^lib/net461/System\.Memory\.dll$' $lib
Expand-From $buf '^lib/net461/System\.Buffers\.dll$' $lib
Expand-From $uns '^lib/net461/System\.Runtime\.CompilerServices\.Unsafe\.dll$' $lib

Write-Host '== Visual C++ runtime (app-local copy) =='
# onnxruntime.dll links MSVCP140 / VCRUNTIME140. Office and most PCs already have
# them, but a locked-down work PC may not - shipping them next to the exe is the
# difference between "works" and "no error, no start".
$vcDlls = @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
foreach ($name in $vcDlls) {
    $src = Join-Path $env:WINDIR "System32\$name"
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $lib $name) -Force
        Write-Host "  copied $name"
    }
    else {
        Write-Warning "$name not found in System32; install the VC++ 2015-2022 redistributable on the target PC."
    }
}

if (-not $SkipModels) {
    Write-Host '== PP-OCRv6 small models (ModelScope, RapidAI mirror) =='
    $base = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0'
    $wanted = @(
        @{ Url = "$base/onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx"; File = 'PP-OCRv6_det_small.onnx'
           Sha = '090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f' },
        @{ Url = "$base/onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx"; File = 'PP-OCRv6_rec_small.onnx'
           Sha = '6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884' }
    )
    foreach ($m in $wanted) {
        $out = Join-Path $models $m.File
        if (-not (Test-Path $out)) {
            Write-Host "  downloading $($m.File)"
            Invoke-WebRequest -Uri $m.Url -OutFile $out -UseBasicParsing
        }
        $hash = (Get-FileHash $out -Algorithm SHA256).Hash.ToLower()
        if ($hash -ne $m.Sha) {
            throw "SHA256 mismatch for $($m.File): $hash"
        }
        Write-Host "  verified $($m.File)"
    }
}

if (-not $SkipSdk) {
    Write-Host '== winmd metadata (only needed when the Windows 10 SDK is absent) =='
    $sdkPkg = Get-Package -Id 'Microsoft.Windows.SDK.Contracts' -Version '10.0.28000.2705' `
        -OutFile (Join-Path $tmp 'sdk.nupkg')
    Expand-From $sdkPkg 'ref/netstandard2\.0/Windows\.WinMD$' $sdk 'Windows.winmd'
    Expand-From $sdkPkg 'ref/netstandard2\.0/Windows\.Foundation\.UniversalApiContract\.winmd$' $sdk
    Expand-From $sdkPkg 'ref/netstandard2\.0/Windows\.Foundation\.FoundationContract\.winmd$' $sdk
}

Write-Host ''
Write-Host 'Done. Now run build.cmd.'
