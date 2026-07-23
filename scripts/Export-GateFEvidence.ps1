[CmdletBinding()]
param(
    [string]$OutputPath = "artifacts\gate-f-evidence.json",
    [switch]$RequireCleanWorktree
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot "tests\EnterpriseAI.Poc.Regression\EnterpriseAI.Poc.Regression.csproj"
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
        verification = [ordered]@{
            restore = "Passed"
            release_build = "Passed"
            regression = "Passed"
            regression_count = $testIds.Count
            regression_ids = $testIds
            docs_validation = "Passed"
        }
        limitations = @(
            "No real OIDC or dynamic group revocation",
            "No real SharePoint or business data approval",
            "No PostgreSQL, pgvector, model, production SLO, or AI evaluation"
        )
    }

    $outputDirectory = Split-Path -Parent $resolvedOutputPath
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    $json = $bundle | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText($resolvedOutputPath, $json, [Text.UTF8Encoding]::new($false))
    Write-Host "GATE_F_EVIDENCE=PASS path=$resolvedOutputPath"
} finally {
    Pop-Location
}
