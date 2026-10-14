# 配布物の SHA-256 チェックサム（SHA256SUMS.txt）の作成と読み書き（Issue #2268）
# release.yml（ZIP）・publish-release.ps1（インストーラーを加えて Release 本文にも載せる）・
# build-installer.ps1（インストーラーの隣に書き出す）から dot-source する。
# 単体テストは Windows PowerShell 5.1 で読み込むため、このファイルは BOM 付き UTF-8 で保存し、
# 5.1 で使えない構文（?? / 三項演算子など）を書かない。
#
# 形式は GNU coreutils の sha256sum と同じ「<小文字の 16 進 64 桁><空白 2 つ><ファイル名>」を 1 行 1 ファイルで並べる。
# Linux では `sha256sum -c SHA256SUMS.txt` でそのまま照合でき、Windows では Get-FileHash の結果と目で照合できる
# （Get-FileHash は大文字で出すが、PowerShell の -eq は大文字・小文字を区別しない）。

# Release に添付するチェックサムのファイル名
$Sha256SumsFileName = "SHA256SUMS.txt"

# sha256sum 互換の 1 行。ハッシュは小文字の 16 進 64 桁、区切りは空白 2 つ（テキストモードの印）
$Sha256SumLinePattern = '^(?<hash>[0-9a-f]{64})  (?<name>[^\r\n]+)$'

# 相対パスを PowerShell の現在の場所から解決する。[System.IO.File] はプロセスの作業ディレクトリで解決するため、
# Set-Location の後では別の場所を読み書きしてしまう
function Resolve-ChecksumPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

# 1 ファイルの SHA-256 を sha256sum 互換の行にする。ファイルが無ければ例外（空のハッシュを公開しない）。
function Get-Sha256SumLine {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "SHA-256 を求めるファイルが見つかりません: $Path"
    }
    $item = Get-Item -LiteralPath $Path
    $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    return "$hash  $($item.Name)"
}

# 行の形を確かめ、ファイル名を返す。形が崩れた行は例外（照合できない行を公開しない）。
function Get-Sha256SumLineName {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Line)
    if ($Line -cnotmatch $Sha256SumLinePattern) {
        throw "SHA-256 の行の形が正しくありません（「小文字の 16 進 64 桁、空白 2 つ、ファイル名」の形にしてください）: $Line"
    }
    return $Matches['name']
}

# 既存の行に新しい行を加える。同じファイル名の既存の行は新しい行で置き換える（作り直した配布物の古いハッシュを残さない）。
# 既存の行の並びは保ち、新しい行は末尾に加える。
function Merge-Sha256SumLines {
    param(
        [AllowEmptyCollection()][AllowNull()][string[]]$Existing,
        [Parameter(Mandatory = $true)][string[]]$New
    )
    $newNames = @{}
    foreach ($line in $New) {
        $newNames[(Get-Sha256SumLineName $line)] = $true
    }
    $merged = New-Object System.Collections.Generic.List[string]
    foreach ($line in @($Existing)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if (-not $newNames.ContainsKey((Get-Sha256SumLineName $line))) {
            $merged.Add($line)
        }
    }
    foreach ($line in $New) {
        $merged.Add($line)
    }
    return , $merged.ToArray()
}

# SHA256SUMS.txt を読む。空行は読み飛ばし、形が崩れた行は例外にする。
function Read-Sha256SumsFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    $text = [System.IO.File]::ReadAllText((Resolve-ChecksumPath $Path), (New-Object System.Text.UTF8Encoding($false)))
    $lines = @(($text.TrimStart([char]0xFEFF) -split "\r?\n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    foreach ($line in $lines) {
        $null = Get-Sha256SumLineName $line
    }
    return , [string[]]$lines
}

# SHA256SUMS.txt を書く。sha256sum -c が読めるよう、BOM なしの UTF-8・LF 区切り・末尾に改行を付ける
# （BOM や CR はファイル名の一部として読まれ、照合が「ファイルが無い」で失敗する）。
function Write-Sha256SumsFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$Lines
    )
    foreach ($line in $Lines) {
        $null = Get-Sha256SumLineName $line
    }
    $text = ($Lines -join "`n") + "`n"
    [System.IO.File]::WriteAllText((Resolve-ChecksumPath $Path), $text, (New-Object System.Text.UTF8Encoding($false)))
}

# Release 本文に載せるチェックサムの節（行の配列）。行が無ければ空の配列を返す。
# 照合の手順は IT担当者ガイド「配布物のハッシュ（SHA-256）を照合する」と同じ内容にする。
function New-ChecksumsMarkdown {
    param([AllowEmptyCollection()][AllowNull()][string[]]$Lines)
    $valid = @(@($Lines) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($valid.Count -eq 0) {
        return , @()
    }
    foreach ($line in $valid) {
        $null = Get-Sha256SumLineName $line
    }
    $section = @(
        "### SHA-256（改ざんの確認）",
        "ダウンロードしたファイルが途中で差し替えられていないことを、PowerShell の ``Get-FileHash -Algorithm SHA256 <ファイル>`` で求めた値と次の値の照合で確かめてください（大文字・小文字は問いません）。同じ値を ``${Sha256SumsFileName}`` にも載せています。",
        "",
        '```text'
    ) + $valid + @('```')
    return , [string[]]$section
}

# `gh release view --json assets` の出力に、添付が済んだ（state が uploaded の）指定名のアセットがあるか。
# Release の作成とアセットの添付は別の操作なので、Release が見えてもアセットはまだ無いことがある。
# 解析できない出力は「まだ無い」と扱う（待機の上限で止まる）。
function Test-ReleaseAssetUploaded {
    param(
        [AllowNull()][AllowEmptyString()][string]$ViewJson,
        [Parameter(Mandatory = $true)][string]$Name
    )
    if ([string]::IsNullOrWhiteSpace($ViewJson)) { return $false }
    try {
        $view = $ViewJson | ConvertFrom-Json
    } catch {
        return $false
    }
    foreach ($asset in @($view.assets)) {
        if ($null -eq $asset) { continue }
        if ($asset.name -ceq $Name -and ([string]::IsNullOrEmpty($asset.state) -or $asset.state -eq 'uploaded')) {
            return $true
        }
    }
    return $false
}
