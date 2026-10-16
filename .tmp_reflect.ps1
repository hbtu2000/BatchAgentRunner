$bin = 'E:\Src\CodeBak\WorkAgent\src\BatchAgentRunner.Desktop\bin\Debug\net10.0'
Add-Type -Path (Join-Path $bin 'BatchAgentRunner.Core.dll')
Add-Type -Path (Join-Path $bin 'BatchAgentRunner.Infrastructure.dll')
$t = [BatchAgentRunner.Infrastructure.CredentialVault.CliLoginLauncher]
$m = $t.GetMethod('FindOnPath', [System.Reflection.BindingFlags]'NonPublic,Static')
$exe = $m.Invoke($null, @('codebuddy'))
Write-Host "FindOnPath('codebuddy') = $exe"
