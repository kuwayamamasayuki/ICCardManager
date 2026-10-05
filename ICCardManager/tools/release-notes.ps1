# GitHub Release の本文（リリースノート）の組み立てと照合（Issue #2246）
# publish-release.ps1 から dot-source する。単体テストは Windows PowerShell 5.1 で読み込むため、
# このファイルは BOM 付き UTF-8 で保存し、5.1 で使えない構文（?? / 三項演算子など）を書かない。
#
# 本文の行はヒアストリングではなく配列の連結で組み立てる。ヒアストリングはファイルの改行コードを
# そのまま取り込むため、保存時の改行コードで本文（と長さの照合）が変わってしまう。

# GitHub のリリース本文の上限（文字数）。超えると gh release edit が失敗する。
$ReleaseBodyMaxLength = 125000

function Get-ChangelogSection {
    param([string]$Path, [string]$Ver)
    $content = Get-Content -LiteralPath $Path -Encoding UTF8
    $section = @()
    $capturing = $false

    foreach ($line in $content) {
        if ($line -match "^### v$([regex]::Escape($Ver))\b") {
            $capturing = $true
            continue
        }
        if ($capturing -and $line -match '^### v\d+\.\d+\.\d+') {
            break
        }
        if ($capturing) {
            $section += $line
        }
    }

    # 前後の空行を除去
    return ($section -join "`n").Trim()
}

# タグ時点の CHANGELOG.md への URL。本文を見出しに絞ったときの詳細の参照先。
function Get-ChangelogUrlAtTag {
    param([string]$RepoUrl, [string]$TagName)
    return "$($RepoUrl.TrimEnd('/'))/blob/${TagName}/ICCardManager/CHANGELOG.md"
}

# 文字数（コードポイント数）。GitHub の上限は文字数で数えるため、サロゲートペアは 1 文字と数える。
function Measure-ReleaseBodyLength {
    param([AllowNull()][AllowEmptyString()][string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return 0 }
    return $Text.Length - [regex]::Matches($Text, '[\uDC00-\uDFFF]').Count
}

# 各項目の見出し行（分類行 **…** と、行頭の「- 」で始まる項目行）だけを抜き出す。字下げした詳細行は落とす。
function Get-ReleaseNotesHeadlines {
    param([AllowEmptyString()][string]$Section)
    return @(($Section -split "\r?\n") | Where-Object { $_ -match '^\*\*.+\*\*\s*$' -or $_ -match '^- ' })
}

function Get-NoHeadlineMessage {
    param([string]$Version, [int]$MaxLength)
    return "リリースノートの本文が上限（${MaxLength} 文字）に収まりません。見出しを 1 件も載せられないため、CHANGELOG.md の v${Version} の項目を「- 」で始まる行に分けてください。"
}

function New-ReleaseNotesBody {
    param(
        [Parameter(Mandatory = $true)][string]$Version,
        [AllowEmptyString()][string]$Section,
        [Parameter(Mandatory = $true)][string]$ChangelogUrl,
        [int]$MaxLength = $ReleaseBodyMaxLength
    )

    if ([string]::IsNullOrWhiteSpace($Section)) {
        $Section = "v${Version} リリース"
    }
    $Section = ($Section -replace "\r\n", "`n").Trim()

    $header = @("## ICCardManager v${Version}", "")
    # 本アプリは .NET Framework 4.8 向けで、self-contained の publish は使えない（development-conventions.md「環境制約」）
    $footer = @(
        "",
        "### 動作環境",
        "- Windows 10/11",
        "- Sony PaSoRi（RC-S380 等）",
        "- .NET Framework 4.8（Windows 10/11 に標準搭載。別途インストール不要）",
        "",
        "### インストール方法",
        "インストーラー（``ICCardManager_Setup_${Version}.exe``）を実行してください。"
    )

    $full = (@($header) + @($Section) + @($footer)) -join "`n"
    if ((Measure-ReleaseBodyLength $full) -le $MaxLength) {
        return $full
    }

    # 上限を超えるときは、各項目の見出し行だけに絞り、タグ時点の CHANGELOG へのリンクを添える
    $intro = @(
        "変更内容が多いため、各項目の見出しだけを載せています。各項目の詳細は [CHANGELOG.md]($ChangelogUrl) を参照してください。",
        ""
    )
    $headlines = Get-ReleaseNotesHeadlines $Section
    $itemCount = @($headlines | Where-Object { $_ -match '^- ' }).Count
    if ($itemCount -eq 0) {
        throw (Get-NoHeadlineMessage -Version $Version -MaxLength $MaxLength)
    }

    $compact = (@($header) + @($intro) + @($headlines) + @($footer)) -join "`n"
    if ((Measure-ReleaseBodyLength $compact) -le $MaxLength) {
        return $compact
    }

    # 見出しだけでも収まらないときは、収まるところまで載せ、残りの件数を示す
    $fixedLength = Measure-ReleaseBodyLength ((@($header) + @($intro) + @($footer)) -join "`n")
    # 省略の注記は件数の桁数が最大のときの長さで枠を取っておく
    $omissionTemplate = "- ほか {0} 件の項目は CHANGELOG.md を参照してください。"
    $budget = $MaxLength - $fixedLength - (Measure-ReleaseBodyLength ($omissionTemplate -f $itemCount)) - 2

    $kept = New-Object System.Collections.Generic.List[string]
    $used = 0
    $keptItems = 0
    foreach ($line in $headlines) {
        $cost = (Measure-ReleaseBodyLength $line) + 1
        if ($used + $cost -gt $budget) { break }
        $kept.Add($line)
        $used += $cost
        if ($line -match '^- ') { $keptItems++ }
    }
    # 項目を 1 つも残せなかった分類行を末尾に残さない
    while ($kept.Count -gt 0 -and $kept[$kept.Count - 1] -notmatch '^- ') {
        $kept.RemoveAt($kept.Count - 1)
    }
    if ($keptItems -eq 0) {
        # 見出し行はあるが、先頭の項目の見出し行だけで上限を超える
        $firstItem = @($headlines | Where-Object { $_ -match '^- ' })[0]
        throw "リリースノートの本文が上限（${MaxLength} 文字）に収まりません。CHANGELOG.md の v${Version} の先頭の項目の見出し行が $(Measure-ReleaseBodyLength $firstItem) 文字あり、見出しだけでも載せられないため、その行を短くして詳細は字下げした行へ移してください。"
    }

    $omission = $omissionTemplate -f ($itemCount - $keptItems)
    return (@($header) + @($intro) + @($kept) + @("", $omission) + @($footer)) -join "`n"
}

# 書き込んだ本文を読み戻した結果が、送った本文と一致するか。
# gh は空の本文も正常に処理して終了コード 0 を返すため、終了コードは成功の根拠にならない。
# 長さだけでなく内容も比べる（文字数を保ったまま化ける置換を見逃さない）。改行コードの違いと前後の空白は照合の対象外にする。
function Test-ReleaseBodyMatches {
    param(
        [AllowNull()][AllowEmptyString()][string]$Expected,
        [AllowNull()][AllowEmptyString()][string]$Actual
    )
    $normalizedExpected = ($Expected -replace "\r\n", "`n").Trim()
    $normalizedActual = ($Actual -replace "\r\n", "`n").Trim()
    return ($normalizedActual.Length -gt 0) -and ($normalizedActual -ceq $normalizedExpected)
}

# gh へ渡す本文の一時ファイルを書く。BOM を付けると本文の先頭に紛れ込むため、BOM なしの UTF-8 で書く。
function Write-ReleaseNotesFile {
    param([string]$Path, [string]$Body)
    [System.IO.File]::WriteAllText($Path, $Body, (New-Object System.Text.UTF8Encoding($false)))
}
