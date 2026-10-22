<#
.SYNOPSIS
    配置 JSON Schema 门禁（ARCH P-14 / A-12）：校验 config.schema.json 与代码契约同步、示例配置合法。

.DESCRIPTION
    本脚本是 CI 中的显式门禁入口，**不重复实现校验逻辑**——校验实现只有一处：
    tests/BatchAgentRunner.Tests/Architecture/ConfigSchemaTests.cs 内的断言（含轻量 schema 校验器）。

    该测试类覆盖四件事：
      ① samples/config.example.json 通过 docs/config.schema.json 校验（schema 校验生效）；
      ② schema 顶层 properties 键集合 == RunOptions 的 [JsonPropertyName] 键集合（防漂移）；
      ③ 各 $defs 键集合 == 对应 C# 类型键集合（嵌套类型同步）；
      ④ 畸形配置必须被拦截（防校验器退化为空操作）。

    单独成步的意义：schema 门禁在 CI 日志中可独立辨识（红/绿不淹没在大批单测里），
    且本地可一键复跑；实现侧仍复用同一套断言，避免「校验器写两份」的漂移。

.EXAMPLE
    pwsh -File scripts/ci/Assert-ConfigSchema.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

$schemaPath = Join-Path $repoRoot 'docs/config.schema.json'
$samplePath = Join-Path $repoRoot 'samples/config.example.json'
$testProj   = Join-Path $repoRoot 'tests/BatchAgentRunner.Tests/BatchAgentRunner.Tests.csproj'

foreach ($required in @($schemaPath, $samplePath, $testProj)) {
    if (-not (Test-Path $required)) {
        Write-Error "配置 Schema 门禁缺少必需文件：$required"
        exit 1
    }
}

Write-Host '== 配置 Schema 门禁（ARCH P-14）=='
Write-Host "schema : $schemaPath"
Write-Host "sample : $samplePath"

# --no-build：门禁消费 CI 前置 build 步骤的产物（与覆盖率门禁同口径）。
# 参数用数组传，避免 Windows PowerShell 5.1 把行首 `--` 解析成一元运算符。
$testArgs = @(
    'test', $testProj,
    '--no-build',
    '-c', $Configuration,
    '--filter', 'FullyQualifiedName~ConfigSchemaTests',
    '--logger', 'console;verbosity=normal'
)
& dotnet @testArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host '配置 Schema 门禁：FAIL —— schema 与 RunOptions 契约不一致，或示例配置未通过校验。' -ForegroundColor Red
    Write-Host '修复指引：改 RunOptions 字段后须同步 docs/config.schema.json（键集合逐字一致）与 docs/CONFIG-配置项说明.md。' -ForegroundColor Yellow
    exit 1
}

Write-Host '配置 Schema 门禁：PASS' -ForegroundColor Green
exit 0
