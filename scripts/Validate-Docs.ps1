[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}

function Invoke-DocsValidation {
    param([Parameter(Mandatory)][string]$Root)

    $errors = [System.Collections.Generic.List[string]]::new()
    $docsRoot = Join-Path $Root 'docs'
    if (-not (Test-Path -LiteralPath $docsRoot -PathType Container)) {
        $errors.Add('STRUCTURE: 缺少 docs 目录')
        return [pscustomobject]@{ Errors = @($errors); Documents = 0; Links = 0; JsonBlocks = 0; TbdIds = 0 }
    }

    $markdownFiles = @(Get-ChildItem -LiteralPath $Root -Recurse -Filter '*.md' -File |
        Where-Object { $_.FullName -notmatch '[\\/]\.git[\\/]' } |
        Sort-Object FullName)
    $docsFiles = @($markdownFiles | Where-Object { $_.FullName.StartsWith($docsRoot, [System.StringComparison]::OrdinalIgnoreCase) })
    $linkCount = 0
    $jsonCount = 0

    foreach ($file in $markdownFiles) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
        $lines = @(Get-Content -LiteralPath $file.FullName -Encoding UTF8)
        $rootPrefix = $Root.TrimEnd([char[]]@('\', '/'))
        $relative = $file.FullName.Substring($rootPrefix.Length).TrimStart([char[]]@('\', '/'))

        $h1Count = [regex]::Matches($text, '(?m)^#\s+').Count
        if ($h1Count -ne 1) {
            $errors.Add("HEADING: $relative 必须且只能有一个 H1，当前为 $h1Count")
        }

        $previousLevel = 0
        for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
            if ($lines[$lineIndex] -match '^(#{1,6})\s+') {
                $level = $Matches[1].Length
                if ($previousLevel -gt 0 -and $level -gt ($previousLevel + 1)) {
                    $errors.Add("HEADING: $relative 第 $($lineIndex + 1) 行从 H$previousLevel 跳到 H$level")
                }
                $previousLevel = $level
            }
        }

        $fenceCount = [regex]::Matches($text, '(?m)^```').Count
        if (($fenceCount % 2) -ne 0) {
            $errors.Add("FENCE: $relative 代码围栏数量为奇数 $fenceCount")
        }

        foreach ($jsonMatch in [regex]::Matches($text, '(?ms)^```json\s*\r?\n(.*?)\r?\n```')) {
            $jsonCount++
            try {
                $null = $jsonMatch.Groups[1].Value | ConvertFrom-Json -ErrorAction Stop
            }
            catch {
                $errors.Add("JSON: $relative 包含不可解析的 JSON 代码块")
            }
        }

        foreach ($linkMatch in [regex]::Matches($text, '\[[^\]]+\]\(([^)]+\.md(?:#[^)]*)?)\)')) {
            $rawTarget = $linkMatch.Groups[1].Value.Trim('<', '>')
            if ($rawTarget -match '^(https?:|mailto:)') { continue }
            $linkCount++
            $targetWithoutAnchor = $rawTarget.Split('#')[0]
            $decodedTarget = [System.Uri]::UnescapeDataString($targetWithoutAnchor)
            $targetPath = [System.IO.Path]::GetFullPath((Join-Path $file.DirectoryName $decodedTarget))
            if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
                $errors.Add("LINK: $relative -> $rawTarget 不存在")
            }
        }

        foreach ($versionMatch in [regex]::Matches($text, '(?m)^版本：(.+?)\s*$')) {
            if ($versionMatch.Groups[1].Value.Trim() -ne 'V1.0') {
                $errors.Add("VERSION: $relative 文档发布版本必须为 V1.0")
            }
        }
    }

    $srsFiles = @(Get-ChildItem -LiteralPath $docsRoot -Filter '*_SRS_V1.0.md' -File)
    if ($srsFiles.Count -ne 1) {
        $errors.Add("BASELINE: 必须且只能存在一个 *_SRS_V1.0.md，当前为 $($srsFiles.Count)")
        $srsText = ''
    }
    else {
        $srsText = Get-Content -LiteralPath $srsFiles[0].FullName -Raw -Encoding UTF8
    }

    $allDocsText = ($docsFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }) -join "`n"
    $srsTbdIds = @([regex]::Matches($srsText, '\bTBD-[A-Z]+-\d{3}\b') | ForEach-Object Value | Sort-Object -Unique)
    $allTbdIds = @([regex]::Matches($allDocsText, '\bTBD-[A-Z]+-\d{3}\b') | ForEach-Object Value | Sort-Object -Unique)
    foreach ($tbdId in $allTbdIds) {
        if ($tbdId -notin $srsTbdIds) {
            $errors.Add("TBD: $tbdId 未在 SRS 基线登记")
        }
    }

    $srsRequirementIds = @([regex]::Matches($srsText, '\b(?:FR|NFR|KPI)-[A-Z]+-\d{3}\b') | ForEach-Object Value | Sort-Object -Unique)
    $allRequirementIds = @([regex]::Matches($allDocsText, '\b(?:FR|NFR|KPI)-[A-Z]+-\d{3}\b') | ForEach-Object Value | Sort-Object -Unique)
    foreach ($requirementId in $allRequirementIds) {
        if ($requirementId -notin $srsRequirementIds) {
            $errors.Add("REQUIREMENT: $requirementId 未在 SRS 基线定义")
        }
    }

    $badShorthand = @([regex]::Matches($allDocsText, '\b(?:FR|NFR|KPI|THR)-[A-Z]+-\d{3}/\d{3}\b') |
        ForEach-Object Value | Sort-Object -Unique)
    foreach ($item in $badShorthand) {
        $errors.Add("IDENTIFIER: 禁止使用缩写标识 $item")
    }

    return [pscustomobject]@{
        Errors = @($errors)
        Documents = $markdownFiles.Count
        Links = $linkCount
        JsonBlocks = $jsonCount
        TbdIds = $allTbdIds.Count
    }
}

function Invoke-ValidatorSelfTest {
    $temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $fixtureRoot = Join-Path $temporaryBase ("enterprise-ai-doc-validator-" + [guid]::NewGuid().ToString('N'))
    try {
        $fixtureDocs = New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'docs') -Force
        @'
# SRS

版本：V1.0

| ID | 要求 |
|---|---|
| FR-IAM-001 | 测试要求 |
| TBD-TEST-001 | 测试待决项 |
'@ | Set-Content -LiteralPath (Join-Path $fixtureDocs.FullName 'Fixture_SRS_V1.0.md') -Encoding UTF8

        @'
# Index

[SRS](Fixture_SRS_V1.0.md)

```json
{"valid": true}
```
'@ | Set-Content -LiteralPath (Join-Path $fixtureDocs.FullName '00_Index.md') -Encoding UTF8

        $validResult = Invoke-DocsValidation -Root $fixtureRoot
        if ($validResult.Errors.Count -ne 0) {
            throw "校验器自测失败：有效样例被拒绝：$($validResult.Errors -join '; ')"
        }

        @'
# Broken

[Missing](missing.md)

```json
{"broken": }
```
'@ | Set-Content -LiteralPath (Join-Path $fixtureDocs.FullName 'Broken.md') -Encoding UTF8

        $invalidResult = Invoke-DocsValidation -Root $fixtureRoot
        if (-not ($invalidResult.Errors | Where-Object { $_ -like 'LINK:*' })) {
            throw '校验器自测失败：未发现故意损坏的相对链接'
        }
        if (-not ($invalidResult.Errors | Where-Object { $_ -like 'JSON:*' })) {
            throw '校验器自测失败：未发现故意损坏的 JSON'
        }
        Write-Host 'SELF_TEST=PASS (valid fixture accepted; broken link and JSON rejected)'
    }
    finally {
        $resolvedFixture = [System.IO.Path]::GetFullPath($fixtureRoot)
        if ($resolvedFixture.StartsWith($temporaryBase, [System.StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $resolvedFixture)) {
            Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
        }
    }
}

if ($SelfTest) {
    Invoke-ValidatorSelfTest
}

$resolvedRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$result = Invoke-DocsValidation -Root $resolvedRoot
Write-Host "DOCUMENTS=$($result.Documents) INTERNAL_LINKS=$($result.Links) JSON_BLOCKS=$($result.JsonBlocks) TBD_IDS=$($result.TbdIds)"
if ($result.Errors.Count -gt 0) {
    foreach ($validationError in $result.Errors) {
        Write-Error $validationError
    }
    exit 1
}

Write-Host 'DOCS_VALIDATION=PASS'
