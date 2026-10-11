$ErrorActionPreference = 'Stop'

$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) {
    throw '未找到 .NET Framework 编译器，请启用或安装 .NET Framework 4.x。'
}

$outputDir = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$exePath = Join-Path $outputDir 'YY麦克风灯.exe'
$iconPath = Join-Path $PSScriptRoot 'assets\yy-lamp.ico'
$manifestPath = Join-Path $PSScriptRoot 'src\YYMicLamp.manifest'
$sourcePath = Join-Path $PSScriptRoot 'src\YYMicLamp.cs'
$audioPath = Join-Path $PSScriptRoot 'src\AudioMonitor.cs'

& $compilerPath /nologo /target:winexe /platform:anycpu /optimize+ "/out:$exePath" "/win32manifest:$manifestPath" "/win32icon:$iconPath" "/resource:$iconPath,YYMicLamp.Icon" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Accessibility.dll $sourcePath $audioPath
if ($LASTEXITCODE -ne 0) {
    throw '编译失败。'
}
Write-Host "已生成：$exePath"
