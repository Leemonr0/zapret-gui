$ErrorActionPreference = "Stop"
$srcDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $srcDir
$dist = Join-Path $root "dist\zapret-gui"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (!(Test-Path $csc)) {
    throw "csc.exe not found at $csc"
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

# Auto-fetch dependencies if missing
$depsZap = Join-Path $root "deps\zapret\zapret-discord-youtube-1.10.4"
$depsWv  = Join-Path $root "deps\webview2"

if (!(Test-Path (Join-Path $dist "bin\winws.exe"))) {
    if (!(Test-Path $depsZap)) {
        Write-Host "Downloading zapret-discord-youtube 1.10.4..."
        New-Item -ItemType Directory -Force -Path (Join-Path $root "deps") | Out-Null
        $zapZip = Join-Path $root "deps\zapret-1.10.4.zip"
        Invoke-WebRequest -Uri "https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.4/zapret-discord-youtube-1.10.4.zip" -OutFile $zapZip -Headers @{"User-Agent"="zapret-gui-builder"}
        Expand-Archive -Path $zapZip -DestinationPath (Join-Path $root "deps\zapret") -Force
    }
    New-Item -ItemType Directory -Force -Path "$dist\bin", "$dist\lists", "$dist\strategies" | Out-Null
    Copy-Item -Force -Recurse "$depsZap\bin\*" "$dist\bin\"
    Copy-Item -Force -Recurse "$depsZap\lists\*" "$dist\lists\"
    Get-ChildItem "$depsZap\*.bat" | Where-Object { $_.Name -notlike "service*" } | Copy-Item -Destination "$dist\strategies\" -Force
}

if (!(Test-Path (Join-Path $dist "Microsoft.Web.WebView2.Core.dll"))) {
    if (!(Test-Path $depsWv)) {
        Write-Host "Downloading Microsoft.Web.WebView2..."
        New-Item -ItemType Directory -Force -Path (Join-Path $root "deps") | Out-Null
        $wvVer = "1.0.4314.35"
        $wvZip = Join-Path $root "deps\webview2.zip"
        Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$wvVer/microsoft.web.webview2.$wvVer.nupkg" -OutFile $wvZip
        Expand-Archive -Path $wvZip -DestinationPath $depsWv -Force
    }
    Copy-Item -Force "$depsWv\lib\net462\Microsoft.Web.WebView2.Core.dll" "$dist\"
    Copy-Item -Force "$depsWv\lib\net462\Microsoft.Web.WebView2.WinForms.dll" "$dist\"
    Copy-Item -Force "$depsWv\runtimes\win-x64\native\WebView2Loader.dll" "$dist\"
}

# Generate crisp flat monochrome .ico
Add-Type -AssemblyName System.Drawing
$icoPath = Join-Path $root "deps\app.ico"
$bmp = New-Object System.Drawing.Bitmap(64, 64)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 14
$d = $r * 2
$rect = New-Object System.Drawing.Rectangle(4, 4, 56, 56)
$path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
$path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
$path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
$path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
$path.CloseFigure()

$bgBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(25, 25, 27))
$borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(72, 237, 237, 237), 2.5)
$g.FillPath($bgBrush, $path)
$g.DrawPath($borderPen, $path)

$zPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(237, 237, 237), 4.5)
$zPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$zPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$zPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
$pts = @(
    (New-Object System.Drawing.PointF(20, 22)),
    (New-Object System.Drawing.PointF(44, 22)),
    (New-Object System.Drawing.PointF(20, 42)),
    (New-Object System.Drawing.PointF(44, 42))
)
$g.DrawLines($zPen, $pts)
$g.Dispose()

$msPng = New-Object System.IO.MemoryStream
$bmp.Save($msPng, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $msPng.ToArray()
$msPng.Dispose()
$bmp.Dispose()

$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]1)
$bw.Write([Byte]64)
$bw.Write([Byte]64)
$bw.Write([Byte]0)
$bw.Write([Byte]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]32)
$bw.Write([UInt32]$pngBytes.Length)
$bw.Write([UInt32]22)
$bw.Write($pngBytes)
$bw.Close()
$fs.Close()

# Pack runtime payload into embedded payload.zip
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$payloadZip = Join-Path $root "deps\payload.zip"
if (Test-Path $payloadZip) { Remove-Item -Force $payloadZip }

$zipStream = [System.IO.File]::Create($payloadZip)
$archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)

foreach ($f in @("Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll")) {
    $full = Join-Path $dist $f
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $full, $f, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
}
foreach ($dirName in @("bin", "lists", "strategies")) {
    $dirFull = Join-Path $dist $dirName
    Get-ChildItem -Recurse -File $dirFull | ForEach-Object {
        $rel = $_.FullName.Substring($dist.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
$archive.Dispose()
$zipStream.Dispose()

# Compile standalone single-file ZapretGUI.exe directly to repo root
$outExe = Join-Path $root "ZapretGUI.exe"
$wvCore = Join-Path $dist "Microsoft.Web.WebView2.Core.dll"
$wvForms = Join-Path $dist "Microsoft.Web.WebView2.WinForms.dll"
$manifest = Join-Path $srcDir "app.manifest"
$html = Join-Path $srcDir "index.html"

$argsList = @(
    "/nologo",
    "/target:winexe",
    "/platform:x64",
    "/optimize+",
    "/out:$outExe",
    "/win32manifest:$manifest",
    "/win32icon:$icoPath",
    "/resource:$html,index.html",
    "/resource:$payloadZip,payload.zip",
    "/r:System.dll",
    "/r:System.Core.dll",
    "/r:System.Drawing.dll",
    "/r:System.Windows.Forms.dll",
    "/r:System.ServiceProcess.dll",
    "/r:System.Web.Extensions.dll",
    "/r:System.IO.Compression.dll",
    "/r:$wvCore",
    "/r:$wvForms",
    (Join-Path $srcDir "Config.cs"),
    (Join-Path $srcDir "NetworkTools.cs"),
    (Join-Path $srcDir "ZapretEngine.cs"),
    (Join-Path $srcDir "MainForm.cs"),
    (Join-Path $srcDir "Program.cs")
)

& $csc @argsList
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

$fi = Get-Item $outExe
Write-Host "Built standalone ZapretGUI.exe: $($fi.FullName) ($([Math]::Round($fi.Length / 1MB, 2)) MB)"
