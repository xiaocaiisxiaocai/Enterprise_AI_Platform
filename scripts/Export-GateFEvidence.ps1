[CmdletBinding()]
param(
    [string]$OutputPath = "artifacts\gate-f-evidence.json",
    [switch]$RequireCleanWorktree
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot "tests\EnterpriseAI.Poc.Regression\EnterpriseAI.Poc.Regression.csproj"
$evaluationProject = Join-Path $repoRoot "tests\EnterpriseAI.Poc.Evaluation\EnterpriseAI.Poc.Evaluation.csproj"
$evaluationDatasetPath = Join-Path $repoRoot "evaluation\gate-f-golden-v1.json"
$manifestPath = Join-Path $repoRoot "src\EnterpriseAI.Poc\Data\approved-source.json"
$validatorPath = Join-Path $repoRoot "scripts\Validate-Docs.ps1"
$resolvedOutputPath = if ([IO.Path]::IsPathRooted($OutputPath)) {
    [IO.Path]::GetFullPath($OutputPath)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputPath))
}

Push-Location $repoRoot
try {
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

    $outputDirectory = Split-Path -Parent $resolvedOutputPath
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    $evaluationReportPath = Join-Path $outputDirectory "gate-f-evaluation.json"
$evaluationRunId = "{0}-{1}" -f [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(), $PID
$evaluationTracePath = Join-Path $outputDirectory "gate-f-evaluation-traces-$evaluationRunId.jsonl"
    $evaluationOutput = @(& dotnet run `
        --project $evaluationProject `
        --configuration Release `
        --no-build `
        -- `
        $evaluationDatasetPath `
        $manifestPath `
        $evaluationReportPath `
        $evaluationTracePath 2>&1)
    $evaluationExitCode = $LASTEXITCODE
    $evaluationOutput | ForEach-Object { Write-Host $_ }
    if ($evaluationExitCode -ne 0) {
        throw "Gate F 确定性评测失败，退出码：$evaluationExitCode"
    }

    $evaluation = Get-Content -LiteralPath $evaluationReportPath -Raw | ConvertFrom-Json
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
        $_.ToString() -match '^REGRESSION_TESTS=PASS count=19$'
    }
    if ($testIds.Count -ne 19 -or $null -eq $regressionSummary) {
        throw "回归输出与 19 条 Gate F 契约不一致。"
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
            report_artifact = [IO.Path]::GetFileName($evaluationReportPath)
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
    [IO.File]::WriteAllText($resolvedOutputPath, $json, [Text.UTF8Encoding]::new($false))
    Write-Host "GATE_F_EVIDENCE=PASS path=$resolvedOutputPath"
} finally {
    Pop-Location
}
