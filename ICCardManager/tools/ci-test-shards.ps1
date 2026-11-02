# CI のテストのシャーディング（全件がちょうど 1 回ずつ実行されたことの照合など）の関数群。
# 関数定義だけで、ドットソースしても何も実行しない。

Set-StrictMode -Version Latest

function Get-TestKey {
    # xUnit の理論は引数ごとに別の表示名になる（"A.B.M(x: 1)"）。末尾の「(名前: 値...)」だけを除いて 1 つのキーにする。
    # 全角の括弧やコロンの無い括弧（"Name (note)"）は表示名の一部なので残す。
    param([Parameter(Mandatory)][string]$DisplayName)
    return ($DisplayName -replace '\([A-Za-z_][A-Za-z0-9_]*: .*\)$', '')
}

function Read-ListTestsOutput {
    # dotnet test --list-tests の出力から、テスト名の行（4 文字以上の空白で始まる行）だけを読む。
    # 見出しの言語（英語・日本語）に依存しない。
    param([Parameter(Mandatory)][string]$Path)
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
