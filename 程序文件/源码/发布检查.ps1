param(
    [switch]$Compile,
    [string]$ProjectRoot = '',
    [string]$CandidateExe = '',
    [string]$OutputManifest = ''
)

$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false)

function Get-VersionSource([string]$SourceDir) {
    $buildInfo = Join-Path $SourceDir 'BuildInfo.cs'
    $text = [IO.File]::ReadAllText($buildInfo, [Text.Encoding]::UTF8)
    $version = [regex]::Match($text, 'ProductVersion\s*=\s*"([^"]+)"').Groups[1].Value
    $date = [regex]::Match($text, 'BuildDate\s*=\s*"([^"]+)"').Groups[1].Value
    if (-not $version -or -not $date) { throw 'BuildInfo.cs 缺少版本或构建日期' }
    return @($version, $date)
}

function New-BuildManifest {
    param([string]$Root, [string]$ExePath, [string]$ManifestPath)
    $sourceDir = Join-Path $Root '程序文件\源码'
    $listPath = Join-Path $sourceDir 'sources.list.txt'
    $names = @([IO.File]::ReadAllLines($listPath, [Text.Encoding]::UTF8) | Where-Object { $_.Trim() })
    $versionInfo = Get-VersionSource $sourceDir
    $items = New-Object System.Collections.Generic.List[object]
    $setParts = New-Object System.Collections.Generic.List[string]
    foreach ($name in $names) {
        $path = Join-Path $sourceDir $name
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $items.Add([pscustomobject]@{ name = $name; sha256 = $hash })
        $setParts.Add($name.ToUpperInvariant() + ':' + $hash)
    }
    $setText = ($setParts.ToArray() -join "`n")
    $setHash = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
    $sha = [Security.Cryptography.SHA256]::Create()
    $sourceSetHash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($setText)))).Replace('-', '')
    $sha.Dispose()
    $usage = Join-Path $Root '使用说明.txt'
    $usageText = [IO.File]::ReadAllText($usage, [Text.Encoding]::UTF8)
    if ($usageText -notmatch [regex]::Escape($versionInfo[0]) -or $usageText -notmatch [regex]::Escape($versionInfo[1])) { throw '使用说明与 BuildInfo.cs 的版本或构建日期不一致' }
    $manifest = [ordered]@{
        productVersion = $versionInfo[0]; buildDate = $versionInfo[1]
        exeSha256 = $setHash; sourceSetSha256 = $sourceSetHash
        usageSha256 = (Get-FileHash -LiteralPath $usage -Algorithm SHA256).Hash
        sources = @($items.ToArray())
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($ManifestPath)) | Out-Null
    [IO.File]::WriteAllText($ManifestPath, (ConvertTo-Json -InputObject $manifest -Depth 6), $utf8)
    return $manifest
}

function Test-BuildPackage {
    param([string]$Root, [string]$ExePath, [string]$ManifestPath)
    $errors = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $ExePath)) { $errors.Add('暂存 EXE 不存在') }
    if (-not (Test-Path -LiteralPath $ManifestPath)) { $errors.Add('build-manifest.json 不存在') }
    if ($errors.Count -gt 0) { return $errors }
    try {
        $manifest = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($ManifestPath, [Text.Encoding]::UTF8)) -ErrorAction Stop
        $sourceDir = Join-Path $Root '程序文件\源码'
        $versionInfo = Get-VersionSource $sourceDir
        if ($manifest.productVersion -ne $versionInfo[0] -or $manifest.buildDate -ne $versionInfo[1]) { $errors.Add('清单版本与 BuildInfo.cs 不一致') }
        $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($ExePath)
        if ($fileVersion.FileVersion -ne '2.1.0.0' -or $fileVersion.ProductVersion -ne $versionInfo[0]) { $errors.Add("EXE 元数据不符：File=$($fileVersion.FileVersion), Product=$($fileVersion.ProductVersion)") }
        $exeHash = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
        if ($exeHash -ne $manifest.exeSha256) { $errors.Add('EXE 哈希与清单不一致') }
        $setParts = New-Object System.Collections.Generic.List[string]
        foreach ($item in @($manifest.sources)) {
            $sourcePath = Join-Path $sourceDir ([string]$item.name)
            if (-not (Test-Path -LiteralPath $sourcePath)) { $errors.Add("清单源文件缺失：$($item.name)"); continue }
            $hash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
            if ($hash -ne $item.sha256) { $errors.Add("源文件哈希不符：$($item.name)") }
            $setParts.Add(([string]$item.name).ToUpperInvariant() + ':' + $hash)
        }
        $expectedNames = @([IO.File]::ReadAllLines((Join-Path $sourceDir 'sources.list.txt'), [Text.Encoding]::UTF8) | Where-Object { $_.Trim() })
        $actualNames = @(Get-ChildItem -LiteralPath $sourceDir -Filter '*.cs' -File | ForEach-Object { $_.Name })
        $missing = @($expectedNames | Where-Object { $actualNames -notcontains $_ })
        $extra = @($actualNames | Where-Object { $expectedNames -notcontains $_ })
        if ($missing.Count) { $errors.Add('sources.list.txt 对应文件缺失：' + ($missing -join ',')) }
        if ($extra.Count) { $errors.Add('发现未纳入清单的 .cs：' + ($extra -join ',')) }
        if (@($manifest.sources).Count -ne $expectedNames.Count) { $errors.Add('交付清单源文件数量不符') }
        $sha = [Security.Cryptography.SHA256]::Create()
        $setHash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($setParts.ToArray() -join "`n"))))).Replace('-', '')
        $sha.Dispose()
        if ($setHash -ne $manifest.sourceSetSha256) { $errors.Add('源文件集合哈希不符') }
        $usageHash = (Get-FileHash -LiteralPath (Join-Path $Root '使用说明.txt') -Algorithm SHA256).Hash
        if ($usageHash -ne $manifest.usageSha256) { $errors.Add('使用说明哈希不符') }
    } catch { $errors.Add('清单核验异常：' + $_.Exception.Message) }
    return $errors
}

function Invoke-Compile([string]$Root) {
    $sourceDir = Join-Path $Root '程序文件\源码'
    $listPath = Join-Path $sourceDir 'sources.list.txt'
    if (-not (Test-Path -LiteralPath $listPath)) { throw 'sources.list.txt 缺失' }
    $names = @([IO.File]::ReadAllLines($listPath, [Text.Encoding]::UTF8) | Where-Object { $_.Trim() })
    $actual = @(Get-ChildItem -LiteralPath $sourceDir -Filter '*.cs' -File | ForEach-Object { $_.Name })
    $missing = @($names | Where-Object { $actual -notcontains $_ })
    $extra = @($actual | Where-Object { $names -notcontains $_ })
    if ($missing.Count -or $extra.Count) { throw "源文件清单不一致；缺失=$($missing -join ',')；额外=$($extra -join ',')" }
    $csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $csc)) { throw ".NET Framework 4 编译器不存在：$csc" }
    $candidate = Join-Path $Root 'UsbGuard.next.exe'
    $rsp = Join-Path $Root 'UsbGuard.build.rsp'
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($arg in @('/nologo', '/target:winexe', '/main:UsbGuard', '/platform:anycpu', '/optimize+', '/langversion:5', '/utf8output', '/warn:4', "/out:`"$candidate`"", '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Management.dll', '/r:System.Web.Extensions.dll')) { $lines.Add($arg) }
    foreach ($name in $names) { $lines.Add('"' + (Join-Path $sourceDir $name) + '"') }
    [IO.File]::WriteAllLines($rsp, $lines.ToArray(), $utf8)
    & $csc "@$rsp"
    if ($LASTEXITCODE -ne 0) { throw "C# 编译失败，exit=$LASTEXITCODE" }
    $programNext = Join-Path $Root '程序文件\UsbGuard.next.exe'
    try { [IO.File]::Copy($candidate, $programNext, $true); Write-Host "[OK] 已同步：$programNext" } catch { Write-Warning '程序文件目录只读；候选 EXE 保留在项目根目录，安装时会以管理员权限校验并替换。' }
    $manifestPath = Join-Path $Root 'build-manifest.json'
    New-BuildManifest -Root $Root -ExePath $candidate -ManifestPath $manifestPath | Out-Null
    try { [IO.File]::Copy($manifestPath, (Join-Path $Root '程序文件\build-manifest.json'), $true) } catch { }
    $errors = Test-BuildPackage -Root $Root -ExePath $candidate -ManifestPath $manifestPath
    if ($errors.Count) { throw ($errors -join "`n") }
    Write-Host "[OK] 构建与发布核验通过：$candidate"
    Write-Host "[OK] 清单：$manifestPath"
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $root = if ($ProjectRoot) { [IO.Path]::GetFullPath($ProjectRoot) } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')) }
        if ($Compile) { Invoke-Compile $root; exit 0 }
        $exe = if ($CandidateExe) { $CandidateExe } elseif (Test-Path -LiteralPath (Join-Path $root 'UsbGuard.next.exe')) { Join-Path $root 'UsbGuard.next.exe' } else { Join-Path $root '程序文件\UsbGuard.exe' }
        $manifest = if ($OutputManifest) { $OutputManifest } elseif (Test-Path -LiteralPath (Join-Path $root 'build-manifest.json')) { Join-Path $root 'build-manifest.json' } else { Join-Path $root '程序文件\build-manifest.json' }
        $errors = Test-BuildPackage -Root $root -ExePath $exe -ManifestPath $manifest
        if ($errors.Count) { Write-Error ($errors -join "`n"); exit 1 }
        Write-Host '[OK] 版本、EXE、源文件集合及使用说明哈希全部一致。'; exit 0
    } catch { Write-Error ($_.Exception.Message + "`n" + $_.InvocationInfo.PositionMessage + "`n" + $_.ScriptStackTrace); exit 1 }
}