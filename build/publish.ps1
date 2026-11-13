<#
.SYNOPSIS
    配布用Zipを作成する。

.DESCRIPTION
    1. 単体テストを実行する（失敗したら中止）
    2. 自己完結・単一ファイルの exe を発行する（.NET ランタイムを内蔵するので、利用者側のインストールは不要）
    3. exe・説明書・ライセンス類を1つのフォルダへ集める
    4. Zip に固め、SHA-256 を表示する

    ゲームのファイルを配布物に含めることはない。
    （環境変数 THECOMA2_GAME_DIR にインストール先が設定されている場合だけ、手順1のテストが
    実物のファイルを一時フォルダへコピーして確認に使い、終わったら消す。）

    途中のどこで失敗しても、dist フォルダに Zip は残らない（前回の Zip も最初に消す）。

.PARAMETER Configuration
    ビルド構成。既定は Release。

.PARAMETER SkipTests
    テストを省略する（動作確認を別に済ませている場合のみ）。
#>
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

# このスクリプトでは、パスを受け取るコマンドに必ず -LiteralPath（または .NET の API）を使う。
# -Path はパスをワイルドカードとして解釈するため、フォルダ名に「[」「]」があるだけで失敗したり、別のファイルを指したりする。
$root = [System.IO.Path]::GetDirectoryName($PSScriptRoot)
$solution = Join-Path $root 'TheComa2JpPatcher.sln'
$project = Join-Path $root 'src\TheComa2JpPatcher\TheComa2JpPatcher.csproj'
$runtime = 'win-x64'

# dotnet が失敗したら、その場で理由を付けて止める（失敗を見過ごして壊れたZipを作らないため）。
function Invoke-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') が失敗しました（終了コード $LASTEXITCODE）。"
    }
}

# 日本語の説明書を、どのエディタでも文字化けしないよう BOM 付き UTF-8 で書き出す。
# 改行は CRLF にそろえる。作業ツリーの改行は git の設定（core.autocrlf）やファイルの来歴によって LF にも CRLF にもなるため、
# そのまま写すと、同じコミットから作っても Zip の中身が変わったり、1つの Zip の中で改行が不ぞろいになったりする
# （Windows 10 の古い版のメモ帳は、LF だけの改行を改行として表示できないという事情もある）。
function Copy-AsTextFile {
    param([string]$Source, [string]$Destination)
    $text = [System.IO.File]::ReadAllText($Source, [System.Text.Encoding]::UTF8)
    $text = [System.Text.RegularExpressions.Regex]::Replace($text, '\r?\n', "`r`n")
    [System.IO.File]::WriteAllText($Destination, $text, (New-Object System.Text.UTF8Encoding($true)))
}

# バージョンは Directory.Build.props の <Version> だけを出どころにする。
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Encoding UTF8
$version = $props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
if (-not $version) {
    throw 'Directory.Build.props から Version を読み取れませんでした。'
}

$packageName = "TheComa2JpPatcher-v$version-$runtime"
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish'
$stageDir = Join-Path $artifacts $packageName
$distDir = Join-Path $root 'dist'
$zipPath = Join-Path $distDir "$packageName.zip"
$partialZipPath = "$zipPath.partial"

# 前回作った同名の Zip は最初に消しておく。途中で失敗したときに、古い Zip が今回の成果物のように残らないようにするため。
foreach ($stale in @($zipPath, $partialZipPath)) {
    if (Test-Path -LiteralPath $stale) {
        Remove-Item -LiteralPath $stale -Force
    }
}

# --- 1. テスト ---
if (-not $SkipTests) {
    Write-Host '== テストを実行します =='
    Invoke-DotNet @('test', $solution, '-c', $Configuration, '--nologo')
}

# --- 2. 発行 ---
# 前回の出力が混ざらないよう、このスクリプトが作るフォルダだけを作り直す。
foreach ($dir in @($publishDir, $stageDir)) {
    if (Test-Path -LiteralPath $dir) {
        Remove-Item -LiteralPath $dir -Recurse -Force
    }
}

Write-Host '== 自己完結・単一ファイルで発行します =='
Invoke-DotNet @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none',
    '-o', $publishDir,
    '--nologo'
)

# --- 3. 同梱物を集める ---
[void][System.IO.Directory]::CreateDirectory($stageDir)
[System.IO.File]::Copy((Join-Path $publishDir 'TheComa2JpPatcher.exe'), (Join-Path $stageDir 'TheComa2JpPatcher.exe'), $true)
Copy-AsTextFile (Join-Path $root 'README.md') (Join-Path $stageDir 'README.txt')
Copy-AsTextFile (Join-Path $root 'LICENSE') (Join-Path $stageDir 'LICENSE.txt')
Copy-AsTextFile (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $stageDir 'THIRD-PARTY-NOTICES.txt')

# exe に内蔵した .NET ランタイムのライセンス文と第三者通知を、実際に使われたパッケージから取り出して同梱する。
$packsJson = & dotnet msbuild $project -nologo -t:ResolveFrameworkReferences "-p:RuntimeIdentifier=$runtime" -p:SelfContained=true -getItem:ResolvedRuntimePack | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "ランタイムパックの場所を取得できませんでした（終了コード $LASTEXITCODE）。"
}

# このアプリが実際に内蔵するのは、基本ランタイムとデスクトップ用ランタイムの2つ。
$bundledPackIds = @('Microsoft.NETCore.App.Runtime.', 'Microsoft.WindowsDesktop.App.Runtime.')
$packs = @(($packsJson | ConvertFrom-Json).Items.ResolvedRuntimePack | Where-Object {
    $id = $_.NuGetPackageId
    @($bundledPackIds | Where-Object { $id.StartsWith($_) }).Count -gt 0
})
if ($packs.Count -ne $bundledPackIds.Count) {
    throw "内蔵するランタイムパックを特定できませんでした（見つかった数: $($packs.Count)）。"
}

foreach ($pack in $packs) {
    $licenseDir = Join-Path $stageDir (Join-Path 'licenses\dotnet' "$($pack.NuGetPackageId).$($pack.NuGetPackageVersion)")
    [void][System.IO.Directory]::CreateDirectory($licenseDir)
    $licenseFiles = @(Get-ChildItem -LiteralPath $pack.PackageDirectory -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' })
    if ($licenseFiles.Count -eq 0) {
        throw "$($pack.NuGetPackageId) にライセンスファイルが見つかりません。"
    }

    foreach ($file in $licenseFiles) {
        # 拡張子の無い LICENSE も開きやすいよう .txt を付ける。
        $name = if ($file.Extension) { $file.Name } else { "$($file.Name).txt" }
        [System.IO.File]::Copy($file.FullName, (Join-Path $licenseDir $name), $true)
    }
}

# --- 4. Zip 化 ---
[void][System.IO.Directory]::CreateDirectory($distDir)

# 展開したときにファイルが散らばらないよう、フォルダごと固める。
# Windows PowerShell 5.1 の Compress-Archive は、エントリ名の区切りに「\」を書き込む（Zip の規格では「/」）。
# そのままだと Windows 以外の展開ツールでフォルダ構成が崩れるため、エントリ名を自分で組み立てて書き込む。
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$stageRoot = (Get-Item -LiteralPath $stageDir).FullName.TrimEnd('\')
# 並び順を固定して、作るたびにエントリの順序が変わらないようにする。
$stageFiles = @(Get-ChildItem -LiteralPath $stageRoot -Recurse -File | Sort-Object { $_.FullName.ToLowerInvariant() })

# 作業用の名前で作り、検査に通ってから正式な名前へ変える。途中で失敗した場合は作業用のファイルを消す
# （書きかけで中身の欠けた Zip が、正式な名前で残らないようにするため）。
try {
    $archive = [System.IO.Compression.ZipFile]::Open($partialZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $stageFiles) {
            $relativePath = $file.FullName.Substring($stageRoot.Length + 1)
            $entryName = "$packageName/" + $relativePath.Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
    }

    # 作ったZipを開き直し、全ファイルが入っていることと、エントリ名が規格どおりであることを確かめる。
    $check = [System.IO.Compression.ZipFile]::OpenRead($partialZipPath)
    try {
        $entryNames = @($check.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $check.Dispose()
    }

    if ($entryNames.Count -ne $stageFiles.Count) {
        throw "Zip のエントリ数（$($entryNames.Count)）が、同梱するファイル数（$($stageFiles.Count)）と一致しません。"
    }

    $invalidNames = @($entryNames | Where-Object { $_.Contains('\') -or -not $_.StartsWith("$packageName/") })
    if ($invalidNames.Count -gt 0) {
        throw "Zip のエントリ名が想定と異なります: $($invalidNames -join ', ')"
    }

    # 配布物として欠かせないファイルが、確かに入っていることを確かめる
    # （件数の一致だけでは、集める段階で欠けたファイルに気付けないため）。
    $requiredEntries = @('TheComa2JpPatcher.exe', 'README.txt', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt') | ForEach-Object { "$packageName/$_" }
    $missingEntries = @($requiredEntries | Where-Object { $entryNames -notcontains $_ })
    if (@($entryNames | Where-Object { $_.StartsWith("$packageName/licenses/dotnet/") }).Count -eq 0) {
        $missingEntries += "$packageName/licenses/dotnet/（.NET ランタイムのライセンス）"
    }

    if ($missingEntries.Count -gt 0) {
        throw "Zip に必要なファイルが入っていません: $($missingEntries -join ', ')"
    }

    [System.IO.File]::Move($partialZipPath, $zipPath)
}
catch {
    if (Test-Path -LiteralPath $partialZipPath) {
        Remove-Item -LiteralPath $partialZipPath -Force
    }

    throw
}

$zip = Get-Item -LiteralPath $zipPath
Write-Host ''
Write-Host "作成しました: $($zip.FullName)"
Write-Host ('サイズ      : {0:N0} バイト' -f $zip.Length)
Write-Host "SHA-256     : $((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLower())"
