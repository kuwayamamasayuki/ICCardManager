# CI のテストのシャーディング（全件がちょうど 1 回ずつ実行されたことの照合など）の関数群。
# 関数定義だけで、ドットソースしても何も実行しない。

# StrictMode は各関数の先頭で設定する（ドットソースで呼び出し側のスコープへ漏らさないため）。

function Get-TestKey {
    # xUnit の理論は引数ごとに別の表示名になる（"A.B.M(x: 1)"）。末尾の「(名前: 値...)」だけを除いて 1 つのキーにする。
    # 全角の括弧やコロンの無い括弧（"Name (note)"）は表示名の一部なので残す。
    param([Parameter(Mandatory)][string]$DisplayName)
    Set-StrictMode -Version Latest
    return ($DisplayName -replace '\([A-Za-z_][A-Za-z0-9_]*: .*\)$', '')
}

function Read-ListTestsOutput {
    # dotnet test --list-tests の出力から、テスト名の行（4 文字以上の空白で始まる行）だけを読む。
    # 見出しの言語（英語・日本語）に依存しない。
    param([Parameter(Mandatory)][string]$Path)
    Set-StrictMode -Version Latest
    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if ($line -match '^\s{4,}\S') {
            $names.Add($line.Trim())
        }
    }
    return , $names.ToArray()
}

function Read-TrxTestNames {
    # trx の UnitTestResult の testName を、結果（成功・失敗・スキップ）を問わずすべて返す。
    param([Parameter(Mandatory)][string]$Path)
    Set-StrictMode -Version Latest
    $xml = [xml][System.IO.File]::ReadAllText($Path)
    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($result in @($xml.TestRun.Results.UnitTestResult)) {
        if ($null -ne $result) {
            $names.Add([string]$result.testName)
        }
    }
    return , $names.ToArray()
}

function Test-ShardPartition {
    # 期待する全テストが、いずれかのシャードでちょうど 1 回ずつ実行されたかを照合する。
    # 比較は Get-TestKey を通したキーの集合で行う（理論の展開は 1 つのキー）。
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Expected,
        [Parameter(Mandatory)][hashtable]$Shards
    )
    Set-StrictMode -Version Latest
    $comparer = [StringComparer]::Ordinal
    $expectedKeys = [System.Collections.Generic.HashSet[string]]::new($comparer)
    foreach ($name in $Expected) { [void]$expectedKeys.Add((Get-TestKey -DisplayName $name)) }

    $executedKeys = [System.Collections.Generic.HashSet[string]]::new($comparer)
    $owner = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.HashSet[string]]]::new($comparer)
    $executedCount = 0
    $emptyShards = [System.Collections.Generic.List[string]]::new()

    foreach ($shardName in ($Shards.Keys | Sort-Object)) {
        $names = @($Shards[$shardName])
        if ($names.Count -eq 0) { $emptyShards.Add([string]$shardName) }
        foreach ($name in $names) {
            $executedCount++
            $key = Get-TestKey -DisplayName ([string]$name)
            [void]$executedKeys.Add($key)
            if (-not $owner.ContainsKey($key)) {
                $owner[$key] = [System.Collections.Generic.HashSet[string]]::new($comparer)
            }
            [void]$owner[$key].Add([string]$shardName)
        }
    }

    $missing = @($expectedKeys | Where-Object { -not $executedKeys.Contains($_) } | Sort-Object -CaseSensitive)
    $unexpected = @($executedKeys | Where-Object { -not $expectedKeys.Contains($_) } | Sort-Object -CaseSensitive)
    $duplicated = @($owner.Keys | Where-Object { $owner[$_].Count -ge 2 } | Sort-Object -CaseSensitive)
    $empty = @($emptyShards)

    return [pscustomobject]@{
        ExpectedCount = $expectedKeys.Count
        ExecutedCount = $executedCount
        Missing       = $missing
        Unexpected    = $unexpected
        Duplicated    = $duplicated
        EmptyShards   = $empty
        IsValid       = ($missing.Count -eq 0 -and $unexpected.Count -eq 0 -and $duplicated.Count -eq 0 -and $empty.Count -eq 0)
    }
}

function Merge-CoverletJson {
    # シャードごとの coverlet の json（coverage.json）を合算し、coverlet の cobertura と同じ数え方で行・分岐のカバレッジを返す。
    # 行は「モジュール|ファイル|クラス|メソッド|行番号」、分岐は「…|Line|Offset|EndOffset|Path|Ordinal」をキーにする。
    # 同じ行番号でもメソッドが違えば別に数える（コンストラクタが複数あるクラスで、ファイルと行番号だけで数えると cobertura と食い違う）。
    # シャードごとのヒット数を足し、1 以上なら covered と数える。キーの集合は和集合で数え、食い違いは KeysConsistent で知らせる。
    param([Parameter(Mandatory)][string[]]$Paths)
    Set-StrictMode -Version Latest
    $lineHits = [System.Collections.Generic.Dictionary[string, long]]::new([System.StringComparer]::Ordinal)
    $branchHits = [System.Collections.Generic.Dictionary[string, long]]::new([System.StringComparer]::Ordinal)
    $lineCounts = [System.Collections.Generic.List[int]]::new()
    $branchCounts = [System.Collections.Generic.List[int]]::new()
    foreach ($path in $Paths) {
        $doc = [System.IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable
        $lineKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        $branchKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($module in $doc.Keys) {
            foreach ($file in $doc[$module].Keys) {
                foreach ($class in $doc[$module][$file].Keys) {
                    foreach ($method in $doc[$module][$file][$class].Keys) {
                        $prefix = "$module|$file|$class|$method|"
                        $entry = $doc[$module][$file][$class][$method]
                        if ($entry.ContainsKey('Lines') -and $null -ne $entry['Lines']) {
                            foreach ($line in $entry['Lines'].Keys) {
                                $key = $prefix + $line
                                $null = $lineKeys.Add($key)
                                $lineHits[$key] = $(if ($lineHits.ContainsKey($key)) { $lineHits[$key] } else { 0 }) + [long]$entry['Lines'][$line]
                            }
                        }
                        if ($entry.ContainsKey('Branches') -and $null -ne $entry['Branches']) {
                            foreach ($branch in @($entry['Branches'])) {
                                $key = $prefix + "$($branch['Line'])|$($branch['Offset'])|$($branch['EndOffset'])|$($branch['Path'])|$($branch['Ordinal'])"
                                $null = $branchKeys.Add($key)
                                $branchHits[$key] = $(if ($branchHits.ContainsKey($key)) { $branchHits[$key] } else { 0 }) + [long]$branch['Hits']
                            }
                        }
                    }
                }
            }
        }
        $lineCounts.Add($lineKeys.Count)
        $branchCounts.Add($branchKeys.Count)
    }
    $linesCovered = @($lineHits.Values | Where-Object { $_ -gt 0 }).Count
    $branchesCovered = @($branchHits.Values | Where-Object { $_ -gt 0 }).Count
    $consistent = $true
    foreach ($count in $lineCounts) { if ($count -ne $lineHits.Count) { $consistent = $false } }
    foreach ($count in $branchCounts) { if ($count -ne $branchHits.Count) { $consistent = $false } }
    return [pscustomobject]@{
        LinesCovered    = [int]$linesCovered
        LinesTotal      = [int]$lineHits.Count
        BranchesCovered = [int]$branchesCovered
        BranchesTotal   = [int]$branchHits.Count
        LineRate        = $(if ($lineHits.Count -eq 0) { [double]0 } else { [double]$linesCovered / $lineHits.Count })
        BranchRate      = $(if ($branchHits.Count -eq 0) { [double]0 } else { [double]$branchesCovered / $branchHits.Count })
        KeysConsistent  = $consistent
    }
}
