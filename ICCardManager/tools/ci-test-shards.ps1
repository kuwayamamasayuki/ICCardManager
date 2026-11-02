# CI のテストのシャーディング（全件がちょうど 1 回ずつ実行されたことの照合など）の関数群。
# 関数定義だけで、ドットソースしても何も実行しない。

# StrictMode は各関数の先頭で設定する（ドットソースで呼び出し側のスコープへ漏らさないため）。

function Get-TestKey {
    # xUnit の理論は引数ごとに別の表示名になる（"A.B.M(x: 1)"）。末尾の「(名前: 値...)」だけを除いて 1 つのキーにする。
    # 全角の括弧やコロンの無い括弧（"Name (note)"）は表示名の一部なので残す。
    param([Parameter(Mandatory)][string]$DisplayName)
    Set-StrictMode -Version Latest
    # 引数名は Unicode の文字（日本語の引数名を含む）。C# 側の TestDisplayNameUniquenessConventionTests も同じ正規表現で正規化する。
    # 引数の値に改行を含む理論（例外メッセージ。"Value cannot be null.`nParameter name: param"）があるので (?s) で '.' を改行にも一致させる。
    # 一覧（--list-tests）が複数行の表示名を出す場合は 2 行目以降が字下げされず読まれない。その名前はキーが一致せず「欠落」になり、黙って通らない。
    return ($DisplayName -replace '(?s)\([\p{L}_][\p{L}\p{Nd}_]*: .*\)$', '')
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

function Format-CiNameList {
    # 名前の一覧を最大 $Limit 件まで Markdown の箇条書きにする。超えた分は件数だけ示す。
    param([string[]]$Names, [int]$Limit = 30)
    Set-StrictMode -Version Latest
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($name in @($Names | Select-Object -First $Limit)) { $lines.Add("  - ``$name``") }
    if (@($Names).Count -gt $Limit) { $lines.Add("  - ほか $(@($Names).Count - $Limit) 件") }
    return , $lines.ToArray()
}

function Format-CiPercent {
    param([double]$Rate)
    Set-StrictMode -Version Latest
    return ($Rate * 100).ToString('F1', [System.Globalization.CultureInfo]::InvariantCulture) + '%'
}

function Invoke-CiTestSummary {
    # ダウンロードした artifact（<Artifacts>/test-results-<構成>-<シャード>/**）を読み、構成ごとに
    # 「全件がちょうど 1 回ずつ実行されたこと」を照合し、サマリー（Markdown）とカバレッジを報告する。
    # 戻り値は終了コード（0 = 照合がすべて有効、1 = 無効または結果の欠落）。カバレッジでは合否を決めない（#2117）。
    param(
        [Parameter(Mandatory)][string]$ArtifactsDirectory,
        [Parameter(Mandatory)][string[]]$Configurations,
        [string]$SummaryPath
    )
    Set-StrictMode -Version Latest
    $failed = $false
    $md = [System.Collections.Generic.List[string]]::new()
    $md.Add('## テストの照合（シャード）')
    $md.Add('')

    # 期待するシャードは、いずれかの構成にある artifact 名の和集合とする（ci.yml の matrix の値を二重に持たない）。
    $knownShards = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    if (Test-Path -LiteralPath $ArtifactsDirectory) {
        foreach ($dir in (Get-ChildItem -LiteralPath $ArtifactsDirectory -Directory)) {
            foreach ($config in $Configurations) {
                $p = "test-results-$config-"
                if ($dir.Name.StartsWith($p, [StringComparison]::Ordinal)) { [void]$knownShards.Add($dir.Name.Substring($p.Length)) }
            }
        }
    }

    foreach ($config in $Configurations) {
        $prefix = "test-results-$config-"
        $md.Add("### $config")
        $md.Add('')
        $shardDirs = @()
        if (Test-Path -LiteralPath $ArtifactsDirectory) {
            $shardDirs = @(Get-ChildItem -LiteralPath $ArtifactsDirectory -Directory | Where-Object { $_.Name.StartsWith($prefix, [StringComparison]::Ordinal) } | Sort-Object Name)
        }
        if ($shardDirs.Count -eq 0) {
            Write-Host "::error::$config のテスト結果が無い（$prefix* の artifact が見つからない）ため、テストの照合ができません。ビルドまたはテストのジョブが失敗していないか、ci.yml の matrix.shard と artifact 名が一致しているかを確かめてください。"
            $md.Add("- 結果が無い（$prefix* の artifact が無い）")
            $md.Add('')
            $failed = $true
            continue
        }

        $shards = @{}
        $missingResult = $false
        $presentShards = @($shardDirs | ForEach-Object { $_.Name.Substring($prefix.Length) })
        foreach ($shard in $knownShards) {
            if ($presentShards -notcontains $shard) {
                Write-Host "::error::構成 $config にシャード $shard の結果（$prefix$shard）がありません。ほかの構成にはあるため、このシャードのテストが開始前に失敗したか artifact のアップロードが漏れた可能性があります。ジョブのログとアップロードの手順を確かめてください。"
                $md.Add("- シャード ``$shard``: 結果が無い（$prefix$shard の artifact が無い）")
                $missingResult = $true
            }
        }
        foreach ($dir in $shardDirs) {
            $shard = $dir.Name.Substring($prefix.Length)
            $trx = @(Get-ChildItem -LiteralPath $dir.FullName -Recurse -File -Filter 'results.trx')
            if ($trx.Count -eq 0) {
                Write-Host "::error::構成 $config のシャード $shard の結果（$prefix$shard の results.trx）が無いため、テストの照合ができません。そのシャードのテストが開始前に失敗していないか、ジョブのログを確かめてください。"
                $md.Add("- シャード ``$shard``: 結果が無い（results.trx が無い）")
                $missingResult = $true
                continue
            }
            $names = [System.Collections.Generic.List[string]]::new()
            foreach ($file in $trx) { foreach ($n in (Read-TrxTestNames -Path $file.FullName)) { $names.Add($n) } }
            $shards[$shard] = $names.ToArray()
        }

        $listFiles = @(Get-ChildItem -LiteralPath ($shardDirs | ForEach-Object { $_.FullName }) -Recurse -File -Filter 'list-tests.txt')
        if ($listFiles.Count -ne 1) {
            $why = if ($listFiles.Count -eq 0) { 'テスト一覧（list-tests.txt）がどのシャードにも無い' } else { "テスト一覧（list-tests.txt）が $($listFiles.Count) 個あり、どれが期待の正か決められない" }
            Write-Host "::error::$config のテストの照合ができません。$why ためです。ci.yml で一覧を作るシャードが 1 つだけで、その artifact がアップロードされているかを確かめてください。"
            $md.Add("- 照合できない: $why")
            $md.Add('')
            $failed = $true
            continue
        }
        if ($missingResult) {
            $md.Add('')
            $failed = $true
            continue
        }

        $expected = Read-ListTestsOutput -Path $listFiles[0].FullName
        $part = Test-ShardPartition -Expected $expected -Shards $shards
        $md.Add("- 照合したテスト（名前の数）: $($part.ExpectedCount)")
        $md.Add("- 実行件数（理論の展開後）: $($part.ExecutedCount)")
        foreach ($shard in ($shards.Keys | Sort-Object)) { $md.Add("  - シャード ``$shard``: $(@($shards[$shard]).Count) 件") }

        if (-not $part.IsValid) {
            $failed = $true
            $detail = "欠落 $(@($part.Missing).Count) 件・期待外 $(@($part.Unexpected).Count) 件・重複 $(@($part.Duplicated).Count) 件・空のシャード $(@($part.EmptyShards).Count) 個"
            $sample = @(@($part.Missing) + @($part.Unexpected) + @($part.Duplicated) | Select-Object -First 3) -join ', '
            Write-Host "::error::$config のテストの照合が無効です（$detail。例: $sample）。シャード ($(($shards.Keys | Sort-Object) -join ', ')) の合算が全件を 1 回ずつ実行していません。ci.yml の TEST_SHARD_NAMESPACE と matrix.shard の対応、ビルドの失敗の有無を確かめてください。本体に Category=UI のテストが足された場合も、どのシャードの絞り込みにも入らないことがあります。"
            $md.Add('- **照合が無効**: ' + $detail)
            foreach ($section in @(@('欠落（一覧にあるが実行されていない）', $part.Missing), @('期待外（一覧に無いのに実行された）', $part.Unexpected), @('重複（複数のシャードで実行された）', $part.Duplicated), @('空のシャード', $part.EmptyShards))) {
                if (@($section[1]).Count -gt 0) {
                    $md.Add("- $($section[0])")
                    foreach ($line in (Format-CiNameList -Names @($section[1]))) { $md.Add($line) }
                }
            }
        }

        $coverageFiles = @(Get-ChildItem -LiteralPath ($shardDirs | ForEach-Object { $_.FullName }) -Recurse -File -Filter 'coverage.json')
        if ($coverageFiles.Count -eq 0) {
            if ($config -eq 'Release') {
                Write-Host "::warning::$config のカバレッジ（coverage.json）が無いため、カバレッジを報告できません。テストの照合は続けています。coverlet の収集が動いたか、テストのジョブのログを確かめてください。"
                $md.Add('- カバレッジ: 結果が無い（coverage.json が無い）')
            }
        }
        else {
            try {
                $cov = Merge-CoverletJson -Paths @($coverageFiles | ForEach-Object { $_.FullName })
                $covDirs = @($shardDirs | Where-Object { @(Get-ChildItem -LiteralPath $_.FullName -Recurse -File -Filter 'coverage.json').Count -gt 0 })
                $noCovShards = @($shardDirs | Where-Object { $covDirs.FullName -notcontains $_.FullName } | ForEach-Object { $_.Name.Substring($prefix.Length) })
                $partialNote = ''
                if ($noCovShards.Count -gt 0) {
                    $noCovText = $noCovShards -join ', '
                    Write-Host "::warning::$config のカバレッジ（coverage.json）がシャード ($noCovText) に無く、合算は一部のシャードだけの数字です。そのシャードの coverlet の収集が動いたか、テストのジョブのログを確かめてください。テストの照合は続けています。"
                    $partialNote = "（一部のシャードのみ。coverage.json が無いシャード: $noCovText）"
                }
                $md.Add("- カバレッジ（シャード合算）${partialNote}: 行 $(Format-CiPercent $cov.LineRate)（$($cov.LinesCovered)/$($cov.LinesTotal)）、分岐 $(Format-CiPercent $cov.BranchRate)（$($cov.BranchesCovered)/$($cov.BranchesTotal)）")
                if (-not $cov.KeysConsistent) {
                    Write-Host "::warning::$config のカバレッジはシャードごとの行・分岐の集合が食い違っています。シャードのビルドが別の成果物になっていないか確かめてください。数値は和集合で合算しています。"
                    $md.Add('- カバレッジの注意: シャード間で行・分岐の集合が食い違っている（和集合で合算）')
                }
            }
            catch {
                Write-Host "::warning::$config のカバレッジを読めませんでした（$($_.Exception.Message)）。coverage.json が壊れていないか確かめてください。テストの照合は続けています。"
                $md.Add('- カバレッジ: 読めなかった')
            }
        }
        $md.Add('')
    }

    $text = ($md -join "`n") + "`n"
    if (-not [string]::IsNullOrEmpty($SummaryPath)) {
        [System.IO.File]::AppendAllText($SummaryPath, $text, [System.Text.UTF8Encoding]::new($false))
    }
    return [int]$(if ($failed) { 1 } else { 0 })
}
