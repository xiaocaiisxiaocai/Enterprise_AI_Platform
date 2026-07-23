# 只读验证 Gate F Evidence Bundle；不依赖网络、GitHub Artifact 或未声明工作区文件。
[CmdletBinding()]
param(
    [string]$EvidencePath,
    [string]$ArtifactDirectory,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ExpectedSchemaVersion = '1.0'
$script:ExpectedEvidenceType = 'gate-f-local-contract'
$script:ExpectedStatus = 'PassedLocalContract'
$script:ExpectedRegressionCount = 92
$script:ExpectedEvaluationCases = 12
$script:Sha256Pattern = '^[0-9a-f]{64}$'
$script:CommitShaPattern = '^[0-9a-f]{7,64}$'
$script:RegressionIdPattern = '^REG-[A-Z]+-[0-9]{3}$'

function Test-IsSimpleFileName {
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
    if ($Name -match '[\\/]' -or $Name.Contains('..')) { return $false }
    if ([IO.Path]::IsPathRooted($Name)) { return $false }
    return $true
}

function Test-Sha256Hex {
    param([string]$Value)
    return -not [string]::IsNullOrWhiteSpace($Value) -and $Value -match $script:Sha256Pattern
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)][string]$Text)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
        return ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-TraceRecordRawJson {
    param([Parameter(Mandatory)][string]$Line)

    $recordMatches = [regex]::Matches($Line, '"record"\s*:')
    if ($recordMatches.Count -ne 1) {
        throw "Trace envelope 必须且只能包含一个 record 字段。"
    }

    $index = $recordMatches[0].Index + $recordMatches[0].Length
    while ($index -lt $Line.Length -and [char]::IsWhiteSpace($Line[$index])) {
        $index++
    }
    if ($index -ge $Line.Length -or $Line[$index] -ne '{') {
        throw "Trace record 必须是 JSON 对象。"
    }

    $start = $index
    $depth = 0
    $inString = $false
    $escaped = $false
    for (; $index -lt $Line.Length; $index++) {
        $character = $Line[$index]
        if ($inString) {
            if ($escaped) {
                $escaped = $false
            }
            elseif ($character -eq '\') {
                $escaped = $true
            }
            elseif ($character -eq '"') {
                $inString = $false
            }
            continue
        }

        if ($character -eq '"') {
            $inString = $true
        }
        elseif ($character -eq '{') {
            $depth++
        }
        elseif ($character -eq '}') {
            $depth--
            if ($depth -eq 0) {
                return $Line.Substring($start, $index - $start + 1)
            }
        }
    }

    throw "Trace record JSON 对象未闭合。"
}

function Get-TraceEntryHash {
    param(
        [Parameter(Mandatory)][long]$Sequence,
        [Parameter(Mandatory)][string]$PreviousHash,
        [Parameter(Mandatory)][string]$RecordJson
    )

    return Get-Sha256Hex -Text ("{0}`n{1}`n{2}" -f $Sequence, $PreviousHash, $RecordJson)
}

function Get-RequiredProperty {
    param(
        $Object,
        [string]$Name,
        $ErrorBag,
        [string]$Path
    )
    if ($null -eq $Object -or -not ($Object.PSObject.Properties.Name -contains $Name)) {
        [void]$ErrorBag.Add("FIELD: 缺少字段 $Path.$Name")
        return $null
    }
    return $Object.$Name
}

function Test-GateFEvidenceBundle {
    param(
        [Parameter(Mandatory)][string]$EvidenceFile,
        [string]$ArtifactsRoot
    )

    $errors = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $EvidenceFile -PathType Leaf)) {
        [void]$errors.Add("PATH: 证据包不存在：$EvidenceFile")
        return [pscustomobject]@{ Errors = @($errors.ToArray()); Ok = $false }
    }

    $resolvedEvidence = [IO.Path]::GetFullPath($EvidenceFile)
    if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
        $ArtifactsRoot = Split-Path -Parent $resolvedEvidence
    }
    $resolvedArtifacts = [IO.Path]::GetFullPath($ArtifactsRoot)
    if (-not (Test-Path -LiteralPath $resolvedArtifacts -PathType Container)) {
        [void]$errors.Add("PATH: 制品目录不存在：$resolvedArtifacts")
        return [pscustomobject]@{ Errors = @($errors.ToArray()); Ok = $false }
    }

    try {
        $bundle = Get-Content -LiteralPath $resolvedEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        [void]$errors.Add("JSON: 证据包无法解析：$($_.Exception.Message)")
        return [pscustomobject]@{ Errors = @($errors.ToArray()); Ok = $false }
    }

    $schema = Get-RequiredProperty $bundle 'schema_version' $errors 'root'
    if ($null -ne $schema -and $schema -ne $script:ExpectedSchemaVersion) {
        [void]$errors.Add("SCHEMA: 未知 schema_version=$schema")
    }

    $evidenceType = Get-RequiredProperty $bundle 'evidence_type' $errors 'root'
    if ($null -ne $evidenceType -and $evidenceType -ne $script:ExpectedEvidenceType) {
        [void]$errors.Add("SCHEMA: 未知 evidence_type=$evidenceType")
    }

    $status = Get-RequiredProperty $bundle 'status' $errors 'root'
    if ($null -ne $status -and $status -ne $script:ExpectedStatus) {
        [void]$errors.Add("STATUS: 期望 $script:ExpectedStatus，实际 $status")
    }

    $null = Get-RequiredProperty $bundle 'generated_at_utc' $errors 'root'
    $commitSha = Get-RequiredProperty $bundle 'commit_sha' $errors 'root'
    if ($null -ne $commitSha -and $commitSha -notmatch $script:CommitShaPattern) {
        [void]$errors.Add("COMMIT: commit_sha 格式无效")
    }

    $worktreeClean = Get-RequiredProperty $bundle 'worktree_clean' $errors 'root'
    if ($null -ne $worktreeClean -and $worktreeClean -isnot [bool]) {
        [void]$errors.Add("COMMIT: worktree_clean 必须为布尔值")
    }

    $environment = Get-RequiredProperty $bundle 'environment' $errors 'root'
    if ($null -ne $environment) {
        $null = Get-RequiredProperty $environment 'os' $errors 'environment'
        $null = Get-RequiredProperty $environment 'dotnet_sdk' $errors 'environment'
    }

    $snapshot = Get-RequiredProperty $bundle 'snapshot' $errors 'root'
    if ($null -ne $snapshot) {
        $null = Get-RequiredProperty $snapshot 'source_id' $errors 'snapshot'
        $null = Get-RequiredProperty $snapshot 'tenant_id' $errors 'snapshot'
        $manifestHash = Get-RequiredProperty $snapshot 'manifest_sha256' $errors 'snapshot'
        if ($null -ne $manifestHash -and -not (Test-Sha256Hex $manifestHash)) {
            [void]$errors.Add("HASH: snapshot.manifest_sha256 格式无效")
        }
        $documents = Get-RequiredProperty $snapshot 'documents' $errors 'snapshot'
        if ($null -ne $documents) {
            $docArray = @($documents)
            if ($docArray.Count -lt 1) {
                [void]$errors.Add("SNAPSHOT: documents 不能为空")
            }
            foreach ($doc in $docArray) {
                $docId = Get-RequiredProperty $doc 'document_id' $errors 'snapshot.documents[]'
                $rel = Get-RequiredProperty $doc 'relative_path' $errors 'snapshot.documents[]'
                $docHash = Get-RequiredProperty $doc 'sha256' $errors 'snapshot.documents[]'
                if ($null -ne $rel) {
                    if ($rel -match '(^|[\\/])\.\.([\\/]|$)' -or [IO.Path]::IsPathRooted($rel)) {
                        [void]$errors.Add("PATH: 文档相对路径越界：$rel")
                    }
                }
                if ($null -ne $docHash -and -not (Test-Sha256Hex $docHash)) {
                    [void]$errors.Add("HASH: 文档 $docId sha256 格式无效")
                }
            }
        }
    }

    $traceContract = Get-RequiredProperty $bundle 'trace_contract' $errors 'root'
    if ($null -ne $traceContract) {
        $rawQuestion = Get-RequiredProperty $traceContract 'raw_question_persisted' $errors 'trace_contract'
        if ($null -ne $rawQuestion -and $rawQuestion -ne $false) {
            [void]$errors.Add("TRACE: raw_question_persisted 必须为 false")
        }
    }

    $evaluation = Get-RequiredProperty $bundle 'evaluation' $errors 'root'
    $reportArtifact = $null
    $traceArtifact = $null
    if ($null -ne $evaluation) {
        $evalStatus = Get-RequiredProperty $evaluation 'status' $errors 'evaluation'
        if ($null -ne $evalStatus -and $evalStatus -ne 'PassedLocalDeterministicEvaluation') {
            [void]$errors.Add("EVAL: evaluation.status 无效：$evalStatus")
        }
        $datasetHash = Get-RequiredProperty $evaluation 'dataset_sha256' $errors 'evaluation'
        if ($null -ne $datasetHash -and -not (Test-Sha256Hex $datasetHash)) {
            [void]$errors.Add("HASH: evaluation.dataset_sha256 格式无效")
        }
        $negative = Get-RequiredProperty $evaluation 'negative_self_test_passed' $errors 'evaluation'
        if ($null -ne $negative -and $negative -ne $true) {
            [void]$errors.Add("EVAL: negative_self_test_passed 必须为 true")
        }

        $totalCases = Get-RequiredProperty $evaluation 'total_cases' $errors 'evaluation'
        $passedCases = Get-RequiredProperty $evaluation 'passed_cases' $errors 'evaluation'
        $unauthorized = Get-RequiredProperty $evaluation 'unauthorized_citation_count' $errors 'evaluation'
        $casePassRate = Get-RequiredProperty $evaluation 'case_pass_rate' $errors 'evaluation'
        $citationRate = Get-RequiredProperty $evaluation 'citation_exact_match_rate' $errors 'evaluation'
        $refusalRate = Get-RequiredProperty $evaluation 'refusal_consistency_rate' $errors 'evaluation'
        $traceEntryCount = Get-RequiredProperty $evaluation 'trace_entry_count' $errors 'evaluation'
        $traceFinalHash = Get-RequiredProperty $evaluation 'trace_final_hash' $errors 'evaluation'
        $reportArtifact = Get-RequiredProperty $evaluation 'report_artifact' $errors 'evaluation'
        $traceArtifact = Get-RequiredProperty $evaluation 'trace_artifact' $errors 'evaluation'

        if ($null -ne $totalCases -and [int]$totalCases -ne $script:ExpectedEvaluationCases) {
            [void]$errors.Add("COUNT: evaluation.total_cases 期望 $script:ExpectedEvaluationCases，实际 $totalCases")
        }
        if ($null -ne $passedCases -and [int]$passedCases -ne $script:ExpectedEvaluationCases) {
            [void]$errors.Add("COUNT: evaluation.passed_cases 期望 $script:ExpectedEvaluationCases，实际 $passedCases")
        }
        if ($null -ne $unauthorized -and [int]$unauthorized -ne 0) {
            [void]$errors.Add("COUNT: unauthorized_citation_count 必须为 0")
        }
        if ($null -ne $casePassRate -and [decimal]$casePassRate -ne 1) {
            [void]$errors.Add("METRIC: case_pass_rate 必须为 1")
        }
        if ($null -ne $citationRate -and [decimal]$citationRate -ne 1) {
            [void]$errors.Add("METRIC: citation_exact_match_rate 必须为 1")
        }
        if ($null -ne $refusalRate -and [decimal]$refusalRate -ne 1) {
            [void]$errors.Add("METRIC: refusal_consistency_rate 必须为 1")
        }
        if ($null -ne $traceEntryCount -and [int]$traceEntryCount -ne $script:ExpectedEvaluationCases) {
            [void]$errors.Add("COUNT: trace_entry_count 期望 $script:ExpectedEvaluationCases，实际 $traceEntryCount")
        }
        if ($null -ne $traceFinalHash -and -not (Test-Sha256Hex $traceFinalHash)) {
            [void]$errors.Add("HASH: evaluation.trace_final_hash 格式无效")
        }
        if ($null -ne $reportArtifact -and -not (Test-IsSimpleFileName $reportArtifact)) {
            [void]$errors.Add("PATH: report_artifact 必须为同目录简单文件名，禁止路径越界")
        }
        if ($null -ne $traceArtifact -and -not (Test-IsSimpleFileName $traceArtifact)) {
            [void]$errors.Add("PATH: trace_artifact 必须为同目录简单文件名，禁止路径越界")
        }
    }

    $verification = Get-RequiredProperty $bundle 'verification' $errors 'root'
    if ($null -ne $verification) {
        foreach ($field in @('restore', 'release_build', 'regression', 'docs_validation', 'deterministic_evaluation')) {
            $value = Get-RequiredProperty $verification $field $errors 'verification'
            if ($null -ne $value -and $value -ne 'Passed') {
                [void]$errors.Add("VERIFICATION: $field 必须为 Passed")
            }
        }
        $regressionCount = Get-RequiredProperty $verification 'regression_count' $errors 'verification'
        $regressionIds = Get-RequiredProperty $verification 'regression_ids' $errors 'verification'
        if ($null -ne $regressionCount -and [int]$regressionCount -ne $script:ExpectedRegressionCount) {
            [void]$errors.Add("COUNT: regression_count 期望 $script:ExpectedRegressionCount，实际 $regressionCount")
        }
        if ($null -ne $regressionIds) {
            $ids = @($regressionIds)
            if ($null -ne $regressionCount -and $ids.Count -ne [int]$regressionCount) {
                [void]$errors.Add("COUNT: regression_ids 数量 $($ids.Count) 与 regression_count $regressionCount 矛盾")
            }
            if ($ids.Count -ne $script:ExpectedRegressionCount) {
                [void]$errors.Add("COUNT: regression_ids 期望 $script:ExpectedRegressionCount 条，实际 $($ids.Count)")
            }
            $unique = @($ids | Select-Object -Unique)
            if ($unique.Count -ne $ids.Count) {
                [void]$errors.Add("COUNT: regression_ids 存在重复")
            }
            foreach ($id in $ids) {
                if ($id -notmatch $script:RegressionIdPattern) {
                    [void]$errors.Add("ID: 非法回归标识 $id")
                }
            }
            # 完整 REG 标识必须全部出现，禁止子集“通过”。
            $requiredIds = @(
                'REG-AUTH-001', 'REG-ACL-001', 'REG-ACL-002', 'REG-ACL-003', 'REG-TEN-001',
                'REG-RAG-001', 'REG-CITE-001', 'REG-SRC-001', 'REG-SRC-002', 'REG-SRC-003',
                'REG-SRC-004', 'REG-AUTH-002', 'REG-TRACE-001', 'REG-TRACE-002', 'REG-TRACE-003',
                'REG-SRC-005', 'REG-TRACE-004', 'REG-TRACE-005',
                'REG-TRACE-006', 'REG-TRACE-007', 'REG-TRACE-008', 'REG-TRACE-009', 'REG-TRACE-010', 'REG-TRACE-011',
                'REG-SRC-006', 'REG-SRC-007', 'REG-SRC-008', 'REG-SRC-009', 'REG-SRC-010',
                'REG-SRC-011', 'REG-SRC-012', 'REG-SRC-013', 'REG-SRC-014', 'REG-SRC-015',
                'REG-API-001', 'REG-API-002', 'REG-API-003', 'REG-API-004', 'REG-API-005',
                'REG-API-006', 'REG-API-007', 'REG-API-008', 'REG-API-009', 'REG-API-010',
                'REG-EVAL-001', 'REG-EVAL-002', 'REG-EVAL-003', 'REG-EVAL-004', 'REG-EVAL-005',
                'REG-EVAL-006', 'REG-EVAL-007', 'REG-EVAL-008', 'REG-EVAL-009', 'REG-EVAL-010',
                'REG-EVAL-011', 'REG-EVAL-012', 'REG-EVAL-013', 'REG-EVAL-014', 'REG-EVAL-015',
                'REG-TRACE-012', 'REG-TRACE-013', 'REG-SRC-016', 'REG-SRC-017',
                'REG-AUTH-003', 'REG-AUTH-004',
                'REG-LIFE-001', 'REG-LIFE-002', 'REG-LIFE-003', 'REG-LIFE-004', 'REG-LIFE-005',
                'REG-ING-001', 'REG-ING-002', 'REG-ING-003', 'REG-ING-004', 'REG-ING-005',
                'REG-ING-006', 'REG-ING-007', 'REG-ING-008', 'REG-ING-009', 'REG-ING-010',
                'REG-STATE-001', 'REG-STATE-002', 'REG-STATE-003', 'REG-STATE-004', 'REG-STATE-005',
                'REG-SEC-001', 'REG-SEC-002', 'REG-SEC-003', 'REG-SEC-004', 'REG-SEC-005',
                'REG-SEC-006', 'REG-WORK-001'
            )
            if ($requiredIds.Count -ne $script:ExpectedRegressionCount) {
                [void]$errors.Add("ID: 必要回归清单长度与期望计数不一致")
            }
            foreach ($required in $requiredIds) {
                if ($required -notin $ids) {
                    [void]$errors.Add("ID: 缺少必要回归 $required")
                }
            }
        }
    }

    $limitations = Get-RequiredProperty $bundle 'limitations' $errors 'root'
    if ($null -ne $limitations -and @($limitations).Count -lt 1) {
        [void]$errors.Add("LIMITATIONS: 必须声明限制")
    }

    # 同目录报告 / Trace 一致性
    if ($null -ne $reportArtifact -and (Test-IsSimpleFileName $reportArtifact)) {
        $reportPath = Join-Path $resolvedArtifacts $reportArtifact
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            [void]$errors.Add("ARTIFACT: 缺少评测报告 $reportArtifact")
        }
        else {
            try {
                $report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
                if ($report.status -ne 'PassedLocalDeterministicEvaluation') {
                    [void]$errors.Add("ARTIFACT: 报告 status 与证据包不一致")
                }
                if ($null -ne $evaluation -and $null -ne $evaluation.dataset_sha256 -and $report.dataset_sha256 -ne $evaluation.dataset_sha256) {
                    [void]$errors.Add("ARTIFACT: 报告 dataset_sha256 与证据包不匹配")
                }
                if ($null -ne $evaluation -and $null -ne $evaluation.trace_final_hash -and $report.trace_final_hash -ne $evaluation.trace_final_hash) {
                    [void]$errors.Add("ARTIFACT: 报告 trace_final_hash 与证据包不匹配")
                }
                if ([int]$report.metrics.total_cases -ne $script:ExpectedEvaluationCases -or
                    [int]$report.metrics.passed_cases -ne $script:ExpectedEvaluationCases -or
                    [int]$report.metrics.unauthorized_citation_count -ne 0) {
                    [void]$errors.Add("ARTIFACT: 报告指标未满足 12/12 且越权引用为 0")
                }
                if ($report.trace_artifact_file -ne $traceArtifact) {
                    [void]$errors.Add("ARTIFACT: 报告 trace_artifact_file 与证据包 trace_artifact 不匹配")
                }
            }
            catch {
                [void]$errors.Add("ARTIFACT: 评测报告无法解析")
            }
        }
    }

    if ($null -ne $traceArtifact -and (Test-IsSimpleFileName $traceArtifact)) {
        $tracePath = Join-Path $resolvedArtifacts $traceArtifact
        if (-not (Test-Path -LiteralPath $tracePath -PathType Leaf)) {
            [void]$errors.Add("ARTIFACT: 缺少评测 Trace $traceArtifact")
        }
        else {
            $lines = @(Get-Content -LiteralPath $tracePath -Encoding UTF8 | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if ($null -ne $evaluation -and $null -ne $evaluation.trace_entry_count -and $lines.Count -ne [int]$evaluation.trace_entry_count) {
                [void]$errors.Add("ARTIFACT: Trace 行数 $($lines.Count) 与 trace_entry_count $($evaluation.trace_entry_count) 矛盾")
            }
            if ($lines.Count -gt 0) {
                try {
                    $previousHash = 'GENESIS'
                    for ($i = 0; $i -lt $lines.Count; $i++) {
                        $envelope = $lines[$i] | ConvertFrom-Json -ErrorAction Stop
                        $expectedSequence = $i + 1
                        if ([long]$envelope.sequence -ne $expectedSequence) {
                            [void]$errors.Add("ARTIFACT: Trace sequence 在第 $expectedSequence 条不连续")
                            break
                        }
                        if ($envelope.previousHash -ne $previousHash) {
                            [void]$errors.Add("ARTIFACT: Trace previousHash 在第 $expectedSequence 条不匹配")
                            break
                        }
                        if ([string]::IsNullOrWhiteSpace($envelope.entryHash) -or -not (Test-Sha256Hex $envelope.entryHash)) {
                            [void]$errors.Add("ARTIFACT: Trace entryHash 在第 $expectedSequence 条无效")
                            break
                        }
                        $recordJson = Get-TraceRecordRawJson -Line $lines[$i]
                        $computedEntryHash = Get-TraceEntryHash `
                            -Sequence $expectedSequence `
                            -PreviousHash $previousHash `
                            -RecordJson $recordJson
                        if ($envelope.entryHash -ne $computedEntryHash) {
                            [void]$errors.Add("ARTIFACT: Trace entryHash 在第 $expectedSequence 条与记录内容不匹配")
                            break
                        }
                        $previousHash = $envelope.entryHash
                    }
                    if ($null -ne $evaluation -and $null -ne $evaluation.trace_final_hash -and $previousHash -ne $evaluation.trace_final_hash) {
                        [void]$errors.Add("ARTIFACT: Trace 最终哈希与证据包 trace_final_hash 不匹配")
                    }
                }
                catch {
                    [void]$errors.Add("ARTIFACT: Trace JSONL 无法解析")
                }
            }
        }
    }

    return [pscustomobject]@{
        Errors = @($errors.ToArray())
        Ok = ($errors.Count -eq 0)
        EvidencePath = $resolvedEvidence
        ArtifactDirectory = $resolvedArtifacts
    }
}

function New-ValidEvidenceFixture {
    param([string]$Root)

    New-Item -ItemType Directory -Path $Root -Force | Out-Null
    $datasetHash = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
    $manifestHash = 'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc'
    $docHash = 'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd'

    $regressionIds = @(
        'REG-AUTH-001', 'REG-ACL-001', 'REG-ACL-002', 'REG-ACL-003', 'REG-TEN-001',
        'REG-RAG-001', 'REG-CITE-001', 'REG-SRC-001', 'REG-SRC-002', 'REG-SRC-003',
        'REG-SRC-004', 'REG-AUTH-002', 'REG-TRACE-001', 'REG-TRACE-002', 'REG-TRACE-003',
        'REG-SRC-005', 'REG-TRACE-004', 'REG-TRACE-005',
        'REG-TRACE-006', 'REG-TRACE-007', 'REG-TRACE-008', 'REG-TRACE-009', 'REG-TRACE-010', 'REG-TRACE-011',
        'REG-SRC-006', 'REG-SRC-007', 'REG-SRC-008', 'REG-SRC-009', 'REG-SRC-010',
        'REG-SRC-011', 'REG-SRC-012', 'REG-SRC-013', 'REG-SRC-014', 'REG-SRC-015',
        'REG-API-001', 'REG-API-002', 'REG-API-003', 'REG-API-004', 'REG-API-005',
        'REG-API-006', 'REG-API-007', 'REG-API-008', 'REG-API-009', 'REG-API-010',
        'REG-EVAL-001', 'REG-EVAL-002', 'REG-EVAL-003', 'REG-EVAL-004', 'REG-EVAL-005',
        'REG-EVAL-006', 'REG-EVAL-007', 'REG-EVAL-008', 'REG-EVAL-009', 'REG-EVAL-010',
        'REG-EVAL-011', 'REG-EVAL-012', 'REG-EVAL-013', 'REG-EVAL-014', 'REG-EVAL-015',
        'REG-TRACE-012', 'REG-TRACE-013', 'REG-SRC-016', 'REG-SRC-017',
        'REG-AUTH-003', 'REG-AUTH-004',
        'REG-LIFE-001', 'REG-LIFE-002', 'REG-LIFE-003', 'REG-LIFE-004', 'REG-LIFE-005',
        'REG-ING-001', 'REG-ING-002', 'REG-ING-003', 'REG-ING-004', 'REG-ING-005',
        'REG-ING-006', 'REG-ING-007', 'REG-ING-008', 'REG-ING-009', 'REG-ING-010',
        'REG-STATE-001', 'REG-STATE-002', 'REG-STATE-003', 'REG-STATE-004', 'REG-STATE-005',
        'REG-SEC-001', 'REG-SEC-002', 'REG-SEC-003', 'REG-SEC-004', 'REG-SEC-005',
        'REG-SEC-006', 'REG-WORK-001'
    )
    if ($regressionIds.Count -ne 92) {
        throw "证据夹具回归 ID 数量必须为 92，实际为 $($regressionIds.Count)"
    }

    $traceLines = New-Object System.Collections.Generic.List[string]
    $previous = 'GENESIS'
    for ($i = 1; $i -le 12; $i++) {
        $record = [ordered]@{
            schemaVersion = '1.0'
            traceId = ('{0:x32}' -f $i)
            occurredAtUtc = '2026-01-01T00:00:00+00:00'
            principalId = 'fixture-principal'
            tenantId = 'enterprise-internal'
            groups = @('employees')
            questionSha256 = ('e' * 64)
            snapshotSourceId = 'fixture-snapshot'
            snapshotManifestSha256 = $manifestHash
            policyVersion = 'gate-f-acl-v1'
            decision = 'answered'
            reasonCode = 'fixture'
            selectedDocumentId = 'doc-finance-001'
            citationCount = 1
        }
        $recordJson = $record | ConvertTo-Json -Compress -Depth 5
        $entryHash = Get-TraceEntryHash -Sequence $i -PreviousHash $previous -RecordJson $recordJson
        $line = [ordered]@{
            sequence = $i
            previousHash = $previous
            entryHash = $entryHash
            record = $record
        } | ConvertTo-Json -Compress -Depth 5
        $traceLines.Add($line)
        $previous = $entryHash
    }
    $traceFinal = $previous
    $traceFileName = 'gate-f-evaluation-traces-fixture.jsonl'
    $reportFileName = 'gate-f-evaluation.json'
    [IO.File]::WriteAllLines((Join-Path $Root $traceFileName), $traceLines, [Text.UTF8Encoding]::new($false))

    $report = [ordered]@{
        schema_version = '1.0'
        evaluation_type = 'gate-f-local-deterministic'
        status = 'PassedLocalDeterministicEvaluation'
        dataset_id = 'gate-f-golden'
        dataset_version = '1'
        dataset_sha256 = $datasetHash
        negative_self_test_passed = $true
        metrics = [ordered]@{
            total_cases = 12
            passed_cases = 12
            unauthorized_citation_count = 0
            case_pass_rate = 1
            citation_exact_match_rate = 1
            refusal_consistency_rate = 1
        }
        trace_entry_count = 12
        trace_final_hash = $traceFinal
        trace_artifact_file = $traceFileName
    }
    $reportJson = $report | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText((Join-Path $Root $reportFileName), $reportJson, [Text.UTF8Encoding]::new($false))

    $bundle = [ordered]@{
        schema_version = '1.0'
        evidence_type = 'gate-f-local-contract'
        status = 'PassedLocalContract'
        generated_at_utc = [DateTimeOffset]::UtcNow.ToString('O')
        commit_sha = 'abcdef1234567890abcdef1234567890abcdef12'
        worktree_clean = $true
        environment = [ordered]@{
            os = 'fixture-os'
            dotnet_sdk = '8.0.0'
        }
        snapshot = [ordered]@{
            source_id = 'fixture-snapshot'
            tenant_id = 'enterprise-internal'
            manifest_sha256 = $manifestHash
            documents = @(
                [ordered]@{
                    document_id = 'doc-finance-001'
                    relative_path = 'fixtures/finance/budget-policy.txt'
                    version = '3'
                    sha256 = $docHash
                    acl_group_count = 1
                }
            )
        }
        trace_contract = [ordered]@{
            schema_version = '1.0'
            policy_version = 'gate-f-acl-v1'
            raw_question_persisted = $false
            hash_chain_regression = 'REG-TRACE-003'
            tamper_regression = 'REG-TRACE-004'
        }
        evaluation = [ordered]@{
            type = 'gate-f-local-deterministic'
            status = 'PassedLocalDeterministicEvaluation'
            dataset_id = 'gate-f-golden'
            dataset_version = '1'
            dataset_sha256 = $datasetHash
            negative_self_test_passed = $true
            total_cases = 12
            passed_cases = 12
            unauthorized_citation_count = 0
            case_pass_rate = 1
            citation_exact_match_rate = 1
            refusal_consistency_rate = 1
            trace_entry_count = 12
            trace_final_hash = $traceFinal
            report_artifact = $reportFileName
            trace_artifact = $traceFileName
        }
        verification = [ordered]@{
            restore = 'Passed'
            release_build = 'Passed'
            regression = 'Passed'
            regression_count = 92
            regression_ids = $regressionIds
            docs_validation = 'Passed'
            deterministic_evaluation = 'Passed'
        }
        limitations = @(
            'Fixture only; not production evidence'
        )
    }
    $evidencePath = Join-Path $Root 'gate-f-evidence.json'
    [IO.File]::WriteAllText(
        $evidencePath,
        ($bundle | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))
    return $evidencePath
}

function Invoke-EvidenceValidatorSelfTest {
    $temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $fixtureRoot = Join-Path $temporaryBase ("enterprise-ai-evidence-validator-" + [guid]::NewGuid().ToString('N'))
    try {
        $validRoot = Join-Path $fixtureRoot 'valid'
        $evidencePath = New-ValidEvidenceFixture -Root $validRoot
        $validResult = Test-GateFEvidenceBundle -EvidenceFile $evidencePath -ArtifactsRoot $validRoot
        if (-not $validResult.Ok) {
            throw "正样例被拒绝：$($validResult.Errors -join '; ')"
        }

        $cases = @(
            @{
                Name = 'missing-field'
                Mutate = {
                    param($dir)
                    $path = Join-Path $dir 'gate-f-evidence.json'
                    $obj = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                    $obj.PSObject.Properties.Remove('commit_sha')
                    [IO.File]::WriteAllText($path, ($obj | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
                }
                Expect = 'FIELD:*'
            }
            @{
                Name = 'unknown-schema'
                Mutate = {
                    param($dir)
                    $path = Join-Path $dir 'gate-f-evidence.json'
                    $obj = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                    $obj.schema_version = '9.9'
                    [IO.File]::WriteAllText($path, ($obj | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
                }
                Expect = 'SCHEMA:*'
            }
            @{
                Name = 'count-mismatch'
                Mutate = {
                    param($dir)
                    $path = Join-Path $dir 'gate-f-evidence.json'
                    $obj = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                    $obj.verification.regression_count = 1
                    [IO.File]::WriteAllText($path, ($obj | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
                }
                Expect = 'COUNT:*'
            }
            @{
                Name = 'hash-tamper'
                Mutate = {
                    param($dir)
                    $path = Join-Path $dir 'gate-f-evidence.json'
                    $obj = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                    $obj.evaluation.dataset_sha256 = 'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff'
                    [IO.File]::WriteAllText($path, ($obj | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
                }
                Expect = 'ARTIFACT:*'
            }
            @{
                Name = 'path-escape'
                Mutate = {
                    param($dir)
                    $path = Join-Path $dir 'gate-f-evidence.json'
                    $obj = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                    $obj.evaluation.report_artifact = '..\secret\report.json'
                    [IO.File]::WriteAllText($path, ($obj | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
                }
                Expect = 'PATH:*'
            }
            @{
                Name = 'trace-mismatch'
                Mutate = {
                    param($dir)
                    $path = Join-Path $dir 'gate-f-evidence.json'
                    $obj = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                    $obj.evaluation.trace_final_hash = 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'
                    [IO.File]::WriteAllText($path, ($obj | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
                }
                Expect = 'ARTIFACT:*'
            }
            @{
                Name = 'trace-record-tamper'
                Mutate = {
                    param($dir)
                    $evidence = Get-Content -LiteralPath (Join-Path $dir 'gate-f-evidence.json') -Raw | ConvertFrom-Json
                    $tracePath = Join-Path $dir $evidence.evaluation.trace_artifact
                    $lines = [System.Collections.Generic.List[string]]::new()
                    Get-Content -LiteralPath $tracePath -Encoding UTF8 | ForEach-Object { [void]$lines.Add($_) }
                    $lines[0] = $lines[0].Replace('fixture-principal', 'forged-principal')
                    [IO.File]::WriteAllLines($tracePath, $lines, [Text.UTF8Encoding]::new($false))
                }
                Expect = 'ARTIFACT:*entryHash*'
            }
        )

        foreach ($case in $cases) {
            $caseRoot = Join-Path $fixtureRoot $case.Name
            New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
            Get-ChildItem -LiteralPath $validRoot -Force | Copy-Item -Destination $caseRoot -Recurse -Force
            & $case.Mutate $caseRoot
            $result = Test-GateFEvidenceBundle -EvidenceFile (Join-Path $caseRoot 'gate-f-evidence.json') -ArtifactsRoot $caseRoot
            if ($result.Ok) {
                throw "负向样例 $($case.Name) 未被拒绝"
            }
            $matched = @($result.Errors | Where-Object { $_ -like $case.Expect })
            if ($matched.Count -eq 0) {
                throw "负向样例 $($case.Name) 未产生期望错误 $($case.Expect)：$($result.Errors -join '; ')"
            }
            Write-Host "SELF_TEST_CASE=PASS name=$($case.Name)"
        }

        Write-Host 'SELF_TEST=PASS (valid evidence accepted; field/schema/count/hash/path/trace content failures rejected)'
    }
    finally {
        $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
        if ($resolvedFixture.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $resolvedFixture)) {
            Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
        }
    }
}

if ($SelfTest) {
    Invoke-EvidenceValidatorSelfTest
    if (-not $EvidencePath) {
        Write-Host 'GATE_F_EVIDENCE_TEST=PASS mode=self-test'
        exit 0
    }
}

if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    Write-Error '用法：Test-GateFEvidence.ps1 -EvidencePath <path> [-ArtifactDirectory <dir>] 或 -SelfTest'
    exit 2
}

$result = Test-GateFEvidenceBundle -EvidenceFile $EvidencePath -ArtifactsRoot $ArtifactDirectory
if (-not $result.Ok) {
    foreach ($validationError in $result.Errors) {
        Write-Error $validationError
    }
    Write-Host "GATE_F_EVIDENCE_TEST=FAIL errors=$($result.Errors.Count)"
    exit 1
}

Write-Host "GATE_F_EVIDENCE_TEST=PASS path=$($result.EvidencePath)"
try {
    $bundle = Get-Content -LiteralPath $result.EvidencePath -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-Host (
        "GATE_F_SUMMARY " +
        "commit=$($bundle.commit_sha) " +
        "regression_count=$($bundle.verification.regression_count) " +
        "golden_cases=$($bundle.evaluation.passed_cases)/$($bundle.evaluation.total_cases) " +
        "unauthorized_citations=$($bundle.evaluation.unauthorized_citation_count) " +
        "dataset_sha256=$($bundle.evaluation.dataset_sha256) " +
        "trace_final_hash=$($bundle.evaluation.trace_final_hash) " +
        "limitations=offline-verify-only;local-deterministic-contract"
    )
}
catch {
    # 摘要失败不掩盖已通过的字段校验。
}
exit 0
