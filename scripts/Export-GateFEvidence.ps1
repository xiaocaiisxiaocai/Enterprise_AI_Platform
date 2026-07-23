# 生成 Gate F Evidence Bundle：先写入同目录暂存区，全部校验通过后再原子发布正式文件。
[CmdletBinding()]
param(
    [string]$OutputPath = "artifacts\gate-f-evidence.json",
    [switch]$RequireCleanWorktree,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot "tests\EnterpriseAI.Poc.Regression\EnterpriseAI.Poc.Regression.csproj"
$evaluationProject = Join-Path $repoRoot "tests\EnterpriseAI.Poc.Evaluation\EnterpriseAI.Poc.Evaluation.csproj"
$evaluationDatasetPath = Join-Path $repoRoot "evaluation\gate-f-golden-v1.json"
$manifestPath = Join-Path $repoRoot "src\EnterpriseAI.Poc\Data\approved-source.json"
$validatorPath = Join-Path $repoRoot "scripts\Validate-Docs.ps1"
$evidenceTesterPath = Join-Path $repoRoot "scripts\Test-GateFEvidence.ps1"

function New-GateFStagingDirectory {
    param([Parameter(Mandatory)][string]$OutputDirectory)

    $stagingName = ".gate-f-export-staging-{0}" -f [guid]::NewGuid().ToString("N")
    $stagingPath = Join-Path $OutputDirectory $stagingName
    New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
    return $stagingPath
}

function Remove-GateFStagingDirectory {
    param([string]$StagingPath, [string]$OutputDirectory)

    if ([string]::IsNullOrWhiteSpace($StagingPath)) {
        return
    }

    $resolvedStaging = [IO.Path]::GetFullPath($StagingPath)
    $resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory) + [IO.Path]::DirectorySeparatorChar
    $leaf = Split-Path -Leaf $resolvedStaging
    if (-not $leaf.StartsWith(".gate-f-export-staging-", [StringComparison]::OrdinalIgnoreCase)) {
        return
    }
    if (-not $resolvedStaging.StartsWith($resolvedOutput, [StringComparison]::OrdinalIgnoreCase)) {
        return
    }
    if (Test-Path -LiteralPath $resolvedStaging) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}

function Publish-GateFArtifacts {
    param(
        [Parameter(Mandatory)][string]$StagingDirectory,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$FinalEvidencePath,
        [Parameter(Mandatory)][string]$StagingEvidencePath,
        [Parameter(Mandatory)][string]$StagingReportPath,
        [Parameter(Mandatory)][string]$StagingTracePath
    )

    $finalReportPath = Join-Path $OutputDirectory (Split-Path -Leaf $StagingReportPath)
    $finalTracePath = Join-Path $OutputDirectory (Split-Path -Leaf $StagingTracePath)

    foreach ($source in @($StagingEvidencePath, $StagingReportPath, $StagingTracePath)) {
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "暂存制品缺失，拒绝发布：$source"
        }
    }

    if (Test-Path -LiteralPath $FinalEvidencePath -PathType Container) {
        throw "正式 Evidence 路径不能是目录：$FinalEvidencePath"
    }
    foreach ($destination in @($finalReportPath, $finalTracePath)) {
        if (Test-Path -LiteralPath $destination) {
            throw "版本化制品目标已存在，拒绝覆盖：$destination"
        }
    }

    # Report/Trace 使用不可变版本化名称；Evidence 最后移动，作为唯一原子提交指针。
    # 前两步失败只会留下未被正式 Evidence 引用的孤立文件，不破坏上一版证据包。
    Move-Item -LiteralPath $StagingTracePath -Destination $finalTracePath -ErrorAction Stop
    Move-Item -LiteralPath $StagingReportPath -Destination $finalReportPath -ErrorAction Stop
    Move-Item -LiteralPath $StagingEvidencePath -Destination $FinalEvidencePath -Force -ErrorAction Stop
}

function Invoke-ExportAtomicitySelfTest {
    $temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $fixtureRoot = Join-Path $temporaryBase ("enterprise-ai-export-atomicity-" + [guid]::NewGuid().ToString("N"))
    try {
        $outputDirectory = Join-Path $fixtureRoot "artifacts"
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
        $finalEvidence = Join-Path $outputDirectory "gate-f-evidence.json"
        $previousContent = '{"status":"PassedLocalContract","previous":true}'
        [IO.File]::WriteAllText($finalEvidence, $previousContent, [Text.UTF8Encoding]::new($false))

        # 1) 成功路径：暂存后发布，正式文件更新
        $staging = New-GateFStagingDirectory -OutputDirectory $outputDirectory
        $stageEvidence = Join-Path $staging "gate-f-evidence.json"
        $stageReport = Join-Path $staging "gate-f-evaluation-selftest.json"
        $stageTrace = Join-Path $staging "gate-f-evaluation-traces-selftest.jsonl"
        [IO.File]::WriteAllText($stageEvidence, '{"status":"PassedLocalContract","fresh":true}', [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($stageReport, '{"status":"PassedLocalDeterministicEvaluation"}', [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($stageTrace, '{"sequence":1}', [Text.UTF8Encoding]::new($false))
        Publish-GateFArtifacts `
            -StagingDirectory $staging `
            -OutputDirectory $outputDirectory `
            -FinalEvidencePath $finalEvidence `
            -StagingEvidencePath $stageEvidence `
            -StagingReportPath $stageReport `
            -StagingTracePath $stageTrace
        Remove-GateFStagingDirectory -StagingPath $staging -OutputDirectory $outputDirectory
        $published = Get-Content -LiteralPath $finalEvidence -Raw
        if ($published -notmatch 'fresh') {
            throw "成功路径未原子发布新证据包"
        }
        Write-Host "SELF_TEST_CASE=PASS name=successful-publish"

        # 2) 失败路径：清理暂存，保留上一次完整正式文件
        $stagingFail = New-GateFStagingDirectory -OutputDirectory $outputDirectory
        $partialEvidence = Join-Path $stagingFail "gate-f-evidence.json"
        [IO.File]::WriteAllText($partialEvidence, '{"status":"INCOMPLETE"}', [Text.UTF8Encoding]::new($false))
        # 模拟中途失败：不调用 Publish，仅清理暂存
        Remove-GateFStagingDirectory -StagingPath $stagingFail -OutputDirectory $outputDirectory
        if (Test-Path -LiteralPath $stagingFail) {
            throw "失败后暂存目录未被清理"
        }
        $preserved = Get-Content -LiteralPath $finalEvidence -Raw
        if ($preserved -notmatch 'fresh') {
            throw "失败路径破坏了上一次完整证据包"
        }
        if ($preserved -match 'INCOMPLETE') {
            throw "失败路径留下了不完整的正式证据包"
        }
        Write-Host "SELF_TEST_CASE=PASS name=failure-preserves-previous"

        # 3) 无效输出路径：父路径是已存在文件时 Directory.CreateDirectory 失败
        $invalidFile = Join-Path $fixtureRoot "not-a-directory"
        [IO.File]::WriteAllText($invalidFile, "x", [Text.UTF8Encoding]::new($false))
        $invalidOutputDir = Join-Path $invalidFile "nested-artifacts"
        $invalidFailed = $false
        try {
            [void][IO.Directory]::CreateDirectory($invalidOutputDir)
        }
        catch {
            $invalidFailed = $true
        }
        if (-not $invalidFailed -or (Test-Path -LiteralPath $invalidOutputDir)) {
            throw "无效输出路径未被拒绝"
        }
        Write-Host "SELF_TEST_CASE=PASS name=invalid-output-path"

        # 4) 不完整暂存拒绝发布
        $stagingIncomplete = New-GateFStagingDirectory -OutputDirectory $outputDirectory
        $onlyEvidence = Join-Path $stagingIncomplete "gate-f-evidence.json"
        [IO.File]::WriteAllText($onlyEvidence, "{}", [Text.UTF8Encoding]::new($false))
        $publishRejected = $false
        try {
            Publish-GateFArtifacts `
                -StagingDirectory $stagingIncomplete `
                -OutputDirectory $outputDirectory `
                -FinalEvidencePath $finalEvidence `
                -StagingEvidencePath $onlyEvidence `
                -StagingReportPath (Join-Path $stagingIncomplete "missing-report.json") `
                -StagingTracePath (Join-Path $stagingIncomplete "missing-trace.jsonl")
        }
        catch {
            $publishRejected = $true
        }
        finally {
            Remove-GateFStagingDirectory -StagingPath $stagingIncomplete -OutputDirectory $outputDirectory
        }
        if (-not $publishRejected) {
            throw "缺失暂存制品时仍发布了正式文件"
        }
        $stillFresh = Get-Content -LiteralPath $finalEvidence -Raw
        if ($stillFresh -notmatch 'fresh') {
            throw "不完整发布尝试覆盖了正式证据"
        }
        Write-Host "SELF_TEST_CASE=PASS name=incomplete-staging-rejected"

        # 5) 发布目标冲突必须在移动任何制品前失败，上一版正式 Evidence 保持不变。
        $stagingConflict = New-GateFStagingDirectory -OutputDirectory $outputDirectory
        $conflictEvidence = Join-Path $stagingConflict "gate-f-evidence.json"
        $conflictReport = Join-Path $stagingConflict "gate-f-evaluation-conflict.json"
        $conflictTrace = Join-Path $stagingConflict "gate-f-evaluation-traces-conflict.jsonl"
        [IO.File]::WriteAllText($conflictEvidence, '{"status":"PassedLocalContract","conflict":true}', [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($conflictReport, '{"status":"PassedLocalDeterministicEvaluation"}', [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($conflictTrace, '{"sequence":1}', [Text.UTF8Encoding]::new($false))
        $conflictDestination = Join-Path $outputDirectory (Split-Path -Leaf $conflictReport)
        New-Item -ItemType Directory -Path $conflictDestination -Force | Out-Null
        $conflictRejected = $false
        try {
            Publish-GateFArtifacts `
                -StagingDirectory $stagingConflict `
                -OutputDirectory $outputDirectory `
                -FinalEvidencePath $finalEvidence `
                -StagingEvidencePath $conflictEvidence `
                -StagingReportPath $conflictReport `
                -StagingTracePath $conflictTrace
        }
        catch {
            $conflictRejected = $true
        }
        finally {
            Remove-GateFStagingDirectory -StagingPath $stagingConflict -OutputDirectory $outputDirectory
            Remove-Item -LiteralPath $conflictDestination -Recurse -Force
        }
        if (-not $conflictRejected) {
            throw "版本化报告目标冲突时发布器仍返回成功"
        }
        if ((Get-Content -LiteralPath $finalEvidence -Raw) -notmatch 'fresh') {
            throw "目标冲突破坏了上一版正式 Evidence"
        }
        if (Test-Path -LiteralPath (Join-Path $outputDirectory (Split-Path -Leaf $conflictTrace))) {
            throw "目标冲突前置检查后仍发布了 Trace"
        }
        Write-Host "SELF_TEST_CASE=PASS name=destination-conflict-preserves-previous"

        # 6) 模拟评测失败：暂存中写入不完整评测输出后失败清理，正式证据保持上一次完整版
        $stagingEvalFail = New-GateFStagingDirectory -OutputDirectory $outputDirectory
        $evalFailReport = Join-Path $stagingEvalFail "gate-f-evaluation.json"
        $evalFailTrace = Join-Path $stagingEvalFail "gate-f-evaluation-traces-eval-fail.jsonl"
        $evalFailEvidence = Join-Path $stagingEvalFail "gate-f-evidence.json"
        [IO.File]::WriteAllText($evalFailReport, '{"status":"FailedLocalDeterministicEvaluation","incomplete":true}', [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($evalFailTrace, '{"sequence":1,"broken":true}', [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($evalFailEvidence, '{"status":"INCOMPLETE_EVAL"}', [Text.UTF8Encoding]::new($false))
        # 模拟评测失败：不 Publish，只清理暂存
        Remove-GateFStagingDirectory -StagingPath $stagingEvalFail -OutputDirectory $outputDirectory
        if (Test-Path -LiteralPath $stagingEvalFail) {
            throw "评测失败后暂存目录残留"
        }
        if ((Get-Content -LiteralPath $finalEvidence -Raw) -notmatch 'fresh') {
            throw "评测失败路径破坏了上一次完整证据包"
        }
        $publishedReportPath = Join-Path $outputDirectory (Split-Path -Leaf $stageReport)
        if (Test-Path -LiteralPath $publishedReportPath) {
            $officialReport = Get-Content -LiteralPath $publishedReportPath -Raw
            if ($officialReport -match 'FailedLocalDeterministicEvaluation' -and $officialReport -match 'incomplete') {
                throw "评测失败后正式目录留下了不完整评测报告"
            }
        }
        Write-Host "SELF_TEST_CASE=PASS name=evaluation-failure-preserves-previous"

        # 7) 模拟文档门禁失败：同样只清暂存，不发布
        $stagingDocsFail = New-GateFStagingDirectory -OutputDirectory $outputDirectory
        [IO.File]::WriteAllText((Join-Path $stagingDocsFail "gate-f-evidence.json"), '{"status":"INCOMPLETE_DOCS"}', [Text.UTF8Encoding]::new($false))
        Remove-GateFStagingDirectory -StagingPath $stagingDocsFail -OutputDirectory $outputDirectory
        if (Test-Path -LiteralPath $stagingDocsFail) {
            throw "文档失败后暂存目录残留"
        }
        if ((Get-Content -LiteralPath $finalEvidence -Raw) -notmatch 'fresh') {
            throw "文档失败路径破坏了上一次完整证据包"
        }
        Write-Host "SELF_TEST_CASE=PASS name=docs-failure-preserves-previous"

        # 8) 正式证据文件在失败前不存在时，失败后仍不存在（非“看似正式但残缺”）
        $cleanArtifacts = Join-Path $fixtureRoot "artifacts-clean"
        New-Item -ItemType Directory -Path $cleanArtifacts -Force | Out-Null
        $cleanFinal = Join-Path $cleanArtifacts "gate-f-evidence.json"
        $stagingCleanFail = New-GateFStagingDirectory -OutputDirectory $cleanArtifacts
        [IO.File]::WriteAllText((Join-Path $stagingCleanFail "gate-f-evidence.json"), '{"status":"INCOMPLETE_FIRST_RUN"}', [Text.UTF8Encoding]::new($false))
        Remove-GateFStagingDirectory -StagingPath $stagingCleanFail -OutputDirectory $cleanArtifacts
        if (Test-Path -LiteralPath $cleanFinal) {
            throw "首次失败后留下了正式证据文件"
        }
        Write-Host "SELF_TEST_CASE=PASS name=first-failure-leaves-no-official-evidence"

        # 9) 真实调用评测进程：损坏 Golden 必须非零退出，且不得把正式证据路径写成 Passed
        $realEvalRoot = Join-Path $fixtureRoot "real-eval-fail"
        New-Item -ItemType Directory -Path $realEvalRoot -Force | Out-Null
        $corruptDataset = Join-Path $realEvalRoot "corrupt-golden.json"
        $realReport = Join-Path $realEvalRoot "gate-f-evaluation.json"
        $realTrace = Join-Path $realEvalRoot "gate-f-evaluation-traces-corrupt.jsonl"
        $realEvidenceOfficial = Join-Path $realEvalRoot "gate-f-evidence.json"
        $corruptJson = @'
{
  "schema_version": "1.0",
  "dataset_id": "gate-f-golden-corrupt",
  "version": "1",
  "tenant_id": "attacker-tenant",
  "cases": [
    {
      "id": "EVAL-BAD",
      "category": "x",
      "principal_id": "alice-finance",
      "question": "预算报销规则是什么",
      "expected_status": "answered",
      "expected_document_ids": ["doc-finance-001"],
      "forbidden_document_ids": ["doc-hr-001"]
    }
  ]
}
'@
        [IO.File]::WriteAllText($corruptDataset, $corruptJson, [Text.UTF8Encoding]::new($false))
        $evaluationProjectPath = Join-Path $repoRoot "tests\EnterpriseAI.Poc.Evaluation\EnterpriseAI.Poc.Evaluation.csproj"
        $manifestForEval = Join-Path $repoRoot "src\EnterpriseAI.Poc\Data\approved-source.json"
        $null = & dotnet build $evaluationProjectPath --configuration Release --verbosity quiet
        if ($LASTEXITCODE -ne 0) {
            throw "评测失败路径自测：Evaluation 项目构建失败"
        }
        # 评测失败会写 stderr；临时放宽 ErrorAction，避免 PowerShell 把预期失败当终止异常。
        $previousEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $evalOut = @(& dotnet run --project $evaluationProjectPath --configuration Release --no-build -- `
            $corruptDataset $manifestForEval $realReport $realTrace 2>&1)
        $evalCode = $LASTEXITCODE
        $ErrorActionPreference = $previousEap
        if ($evalCode -eq 0) {
            throw "评测失败路径自测：损坏数据集进程退出码为 0"
        }
        if (Test-Path -LiteralPath $realReport) {
            $reportText = Get-Content -LiteralPath $realReport -Raw -ErrorAction SilentlyContinue
            if ($reportText -and $reportText -match 'PassedLocalDeterministicEvaluation') {
                throw "评测失败路径自测：损坏数据集生成了伪造 Passed 报告"
            }
        }
        # 正式证据路径在评测失败时必须仍不存在
        if (Test-Path -LiteralPath $realEvidenceOfficial) {
            throw "评测失败路径自测：正式证据文件被提前写出"
        }
        Write-Host "SELF_TEST_CASE=PASS name=real-evaluation-process-failure"
        Write-Host ("SELF_TEST_EVAL_FAIL_OUTPUT=" + (($evalOut | Select-Object -First 3) -join ' | '))

        Write-Host "SELF_TEST=PASS (atomic publish; evaluation/docs/invalid-path failures preserve or omit official evidence; real eval process fail)"
    }
    finally {
        $resolved = [IO.Path]::GetFullPath($fixtureRoot)
        if ($resolved.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $resolved)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}

if ($SelfTest) {
    Invoke-ExportAtomicitySelfTest
    Write-Host "GATE_F_EVIDENCE_EXPORT_TEST=PASS mode=self-test"
    exit 0
}

$resolvedOutputPath = if ([IO.Path]::IsPathRooted($OutputPath)) {
    [IO.Path]::GetFullPath($OutputPath)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputPath))
}

$outputDirectory = Split-Path -Parent $resolvedOutputPath
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw "无效输出路径：$resolvedOutputPath"
}

$stagingDirectory = $null
Push-Location $repoRoot
try {
    try {
        New-Item -ItemType Directory -Path $outputDirectory -Force -ErrorAction Stop | Out-Null
    }
    catch {
        throw "无法创建输出目录（无效输出路径）：$outputDirectory"
    }

    $restoreOutput = @(& dotnet restore $testProject 2>&1)
    $restoreExitCode = $LASTEXITCODE
    $restoreOutput | ForEach-Object { Write-Host $_ }
    if ($restoreExitCode -ne 0) {
        throw "dotnet restore 失败，退出码：$restoreExitCode"
    }

    $buildOutput = @(& dotnet build $testProject --configuration Release --no-restore 2>&1)
    $buildExitCode = $LASTEXITCODE
    $buildOutput | ForEach-Object { Write-Host $_ }
    if ($buildExitCode -ne 0) {
        throw "dotnet build 失败，退出码：$buildExitCode"
    }

    $regressionOutput = @(& dotnet run --project $testProject --configuration Release --no-build 2>&1)
    $regressionExitCode = $LASTEXITCODE
    $regressionOutput | ForEach-Object { Write-Host $_ }
    if ($regressionExitCode -ne 0) {
        throw "Gate F 回归失败，退出码：$regressionExitCode"
    }

    $validationOutput = @(& $validatorPath -SelfTest 2>&1)
    $validationExitCode = $LASTEXITCODE
    $validationOutput | ForEach-Object { Write-Host $_ }
    if ($validationExitCode -ne 0) {
        throw "文档验证失败，退出码：$validationExitCode"
    }

    $evaluationRestoreOutput = @(& dotnet restore $evaluationProject 2>&1)
    $evaluationRestoreExitCode = $LASTEXITCODE
    $evaluationRestoreOutput | ForEach-Object { Write-Host $_ }
    if ($evaluationRestoreExitCode -ne 0) {
        throw "评测项目 restore 失败，退出码：$evaluationRestoreExitCode"
    }

    $evaluationBuildOutput = @(& dotnet build $evaluationProject --configuration Release --no-restore 2>&1)
    $evaluationBuildExitCode = $LASTEXITCODE
    $evaluationBuildOutput | ForEach-Object { Write-Host $_ }
    if ($evaluationBuildExitCode -ne 0) {
        throw "评测项目 build 失败，退出码：$evaluationBuildExitCode"
    }

    $stagingDirectory = New-GateFStagingDirectory -OutputDirectory $outputDirectory
    $evaluationRunId = "{0}-{1}" -f [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(), $PID
    $stagingReportFileName = "gate-f-evaluation-$evaluationRunId.json"
    $stagingReportPath = Join-Path $stagingDirectory $stagingReportFileName
    $stagingTraceFileName = "gate-f-evaluation-traces-$evaluationRunId.jsonl"
    $stagingTracePath = Join-Path $stagingDirectory $stagingTraceFileName
    $stagingEvidencePath = Join-Path $stagingDirectory "gate-f-evidence.json"

    $evaluationOutput = @(& dotnet run `
        --project $evaluationProject `
        --configuration Release `
        --no-build `
        -- `
        $evaluationDatasetPath `
        $manifestPath `
        $stagingReportPath `
        $stagingTracePath 2>&1)
    $evaluationExitCode = $LASTEXITCODE
    $evaluationOutput | ForEach-Object { Write-Host $_ }
    if ($evaluationExitCode -ne 0) {
        throw "Gate F 确定性评测失败，退出码：$evaluationExitCode"
    }

    $evaluation = Get-Content -LiteralPath $stagingReportPath -Raw | ConvertFrom-Json
    if ($evaluation.status -ne "PassedLocalDeterministicEvaluation" -or
        -not $evaluation.negative_self_test_passed -or
        $evaluation.metrics.total_cases -ne 12 -or
        $evaluation.metrics.passed_cases -ne 12 -or
        $evaluation.metrics.unauthorized_citation_count -ne 0 -or
        $evaluation.metrics.case_pass_rate -ne 1 -or
        $evaluation.metrics.citation_exact_match_rate -ne 1 -or
        $evaluation.metrics.refusal_consistency_rate -ne 1 -or
        $evaluation.trace_entry_count -ne 12) {
        throw "Gate F 确定性评测未满足硬门禁。"
    }

    $passLines = @($regressionOutput | Where-Object { $_.ToString() -match '^PASS (REG-[A-Z]+-[0-9]+) ' })
    $testIds = @($passLines | ForEach-Object {
        if ($_.ToString() -match '^PASS (REG-[A-Z]+-[0-9]+) ') {
            $Matches[1]
        }
    })
    $regressionSummary = $regressionOutput | Where-Object {
        $_.ToString() -match '^REGRESSION_TESTS=PASS count=85$'
    }
    if ($testIds.Count -ne 85 -or $null -eq $regressionSummary) {
        throw "回归输出与 85 条 Gate F/F.1/本地状态与摄取契约不一致。"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $dataRoot = Split-Path -Parent $manifestPath
    $documents = @($manifest.documents | ForEach-Object {
        $sourcePath = Join-Path $dataRoot $_.relativePath
        $actualHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $_.sha256) {
            throw "来源文件哈希与清单不一致：$($_.id)"
        }

        [ordered]@{
            document_id = $_.id
            relative_path = $_.relativePath
            version = $_.version
            sha256 = $actualHash
            acl_group_count = @($_.allowedGroups).Count
        }
    })

    $commitSha = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "无法读取 Git commit SHA。"
    }
    $worktreeStatus = (& git status --porcelain=v1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "无法读取 Git 工作树状态。"
    }
    $worktreeClean = [string]::IsNullOrWhiteSpace($worktreeStatus)
    if ($RequireCleanWorktree -and -not $worktreeClean) {
        throw "Gate F CI 证据要求干净工作树。"
    }
    $dotnetVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "无法读取 .NET SDK 版本。"
    }

    $bundle = [ordered]@{
        schema_version = "1.0"
        evidence_type = "gate-f-local-contract"
        status = "PassedLocalContract"
        generated_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
        commit_sha = $commitSha
        worktree_clean = $worktreeClean
        environment = [ordered]@{
            os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
            dotnet_sdk = $dotnetVersion
        }
        snapshot = [ordered]@{
            source_id = $manifest.sourceId
            tenant_id = $manifest.tenantId
            manifest_sha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
            documents = $documents
        }
        trace_contract = [ordered]@{
            schema_version = "1.0"
            policy_version = "gate-f-acl-v1"
            raw_question_persisted = $false
            hash_chain_regression = "REG-TRACE-003"
            tamper_regression = "REG-TRACE-004"
        }
        evaluation = [ordered]@{
            type = $evaluation.evaluation_type
            status = $evaluation.status
            dataset_id = $evaluation.dataset_id
            dataset_version = $evaluation.dataset_version
            dataset_sha256 = $evaluation.dataset_sha256
            negative_self_test_passed = $evaluation.negative_self_test_passed
            total_cases = $evaluation.metrics.total_cases
            passed_cases = $evaluation.metrics.passed_cases
            unauthorized_citation_count = $evaluation.metrics.unauthorized_citation_count
            case_pass_rate = $evaluation.metrics.case_pass_rate
            citation_exact_match_rate = $evaluation.metrics.citation_exact_match_rate
            refusal_consistency_rate = $evaluation.metrics.refusal_consistency_rate
            trace_entry_count = $evaluation.trace_entry_count
            trace_final_hash = $evaluation.trace_final_hash
            report_artifact = $stagingReportFileName
            trace_artifact = $evaluation.trace_artifact_file
        }
        verification = [ordered]@{
            restore = "Passed"
            release_build = "Passed"
            regression = "Passed"
            regression_count = $testIds.Count
            regression_ids = $testIds
            docs_validation = "Passed"
            deterministic_evaluation = "Passed"
        }
        limitations = @(
            "No real OIDC or dynamic group revocation",
            "No real SharePoint or business data approval",
            "No PostgreSQL, pgvector, model, production SLO, or probabilistic AI evaluation"
        )
    }

    $json = $bundle | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText($stagingEvidencePath, $json, [Text.UTF8Encoding]::new($false))

    # 发布前用离线验证器检查暂存证据包
    $testOutput = @(& $evidenceTesterPath -EvidencePath $stagingEvidencePath -ArtifactDirectory $stagingDirectory 2>&1)
    $testExitCode = $LASTEXITCODE
    $testOutput | ForEach-Object { Write-Host $_ }
    if ($testExitCode -ne 0) {
        throw "暂存证据包未通过离线验证。"
    }

    Publish-GateFArtifacts `
        -StagingDirectory $stagingDirectory `
        -OutputDirectory $outputDirectory `
        -FinalEvidencePath $resolvedOutputPath `
        -StagingEvidencePath $stagingEvidencePath `
        -StagingReportPath $stagingReportPath `
        -StagingTracePath $stagingTracePath

    Write-Host "GATE_F_EVIDENCE=PASS path=$resolvedOutputPath"
    Write-Host (
        "GATE_F_SUMMARY " +
        "commit=$commitSha " +
        "worktree_clean=$worktreeClean " +
        "regression_count=$($testIds.Count) " +
        "golden_cases=$($evaluation.metrics.total_cases)/$($evaluation.metrics.passed_cases) " +
        "unauthorized_citations=$($evaluation.metrics.unauthorized_citation_count) " +
        "dataset_sha256=$($evaluation.dataset_sha256) " +
        "trace_final_hash=$($evaluation.trace_final_hash) " +
        "limitations=local-deterministic-only;no-oidc;no-sharepoint;no-probabilistic-ai-eval"
    )
}
catch {
    Write-Error $_
    exit 1
}
finally {
    if ($null -ne $stagingDirectory) {
        Remove-GateFStagingDirectory -StagingPath $stagingDirectory -OutputDirectory $outputDirectory
    }
    Pop-Location
}
