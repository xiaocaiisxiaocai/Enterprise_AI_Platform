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

    $finalReportPath = Join-Path $OutputDirectory "gate-f-evaluation.json"
    $finalTracePath = Join-Path $OutputDirectory (Split-Path -Leaf $StagingTracePath)

    foreach ($source in @($StagingEvidencePath, $StagingReportPath, $StagingTracePath)) {
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "暂存制品缺失，拒绝发布：$source"
        }
    }

    # 同卷 Move-Item 近似原子替换正式文件；失败不删除历史正式证据。
    Move-Item -LiteralPath $StagingEvidencePath -Destination $FinalEvidencePath -Force
    Move-Item -LiteralPath $StagingReportPath -Destination $finalReportPath -Force
    Move-Item -LiteralPath $StagingTracePath -Destination $finalTracePath -Force
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
        $stageReport = Join-Path $staging "gate-f-evaluation.json"
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

        Write-Host "SELF_TEST=PASS (atomic publish, failure preserve, invalid path, incomplete staging)"
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
    $stagingReportPath = Join-Path $stagingDirectory "gate-f-evaluation.json"
    $evaluationRunId = "{0}-{1}" -f [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(), $PID
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
        $_.ToString() -match '^REGRESSION_TESTS=PASS count=57$'
    }
    if ($testIds.Count -ne 57 -or $null -eq $regressionSummary) {
        throw "回归输出与 57 条 Gate F 契约不一致。"
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
            report_artifact = "gate-f-evaluation.json"
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
