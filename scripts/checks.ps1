# Runs the self-checks in checks\ and exits with the number that failed, the same here and on CI.
# From the repo root:  .\scripts\checks.ps1                 every check
#                      .\scripts\checks.ps1 status sessions  only these
#                      .\scripts\checks.ps1 -Core            only the ones that target net10.0
param([switch]$Core, [Parameter(ValueFromRemainingArguments)][string[]]$Names)

$dir = Join-Path (Split-Path $PSScriptRoot -Parent) 'checks'
# mascot.cs renders PNGs to compare by eye, it isn't a pass/fail check.
$checks = Get-ChildItem (Join-Path $dir '*.cs') | Where-Object Name -ne 'mascot.cs'
if ($Names) {
    $want = $Names | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) }
    $missing = $want | Where-Object { -not (Test-Path (Join-Path $dir "$_.cs")) }
    if ($missing) { Write-Host "No such check: $($missing -join ', ')"; exit 1 }
    $checks = $checks | Where-Object { $want -contains $_.BaseName }
}
# Core checks need no Windows: they run on macOS too.
if ($Core) { $checks = $checks | Where-Object { (Get-Content $_.FullName -TotalCount 1) -eq '#:property TargetFramework=net10.0' } }
if (-not $checks) { Write-Host 'No checks to run'; exit 0 }

$failed = 0
Push-Location $dir
try {
    foreach ($check in $checks) {
        # Windows PowerShell wraps each stderr line in an error record; the line is its TargetObject.
        $out = dotnet run -c Release $check.Name 2>&1 |
            ForEach-Object { if ($_ -is [Management.Automation.ErrorRecord]) { $_.TargetObject } else { $_ } } | Out-String
        if ($LASTEXITCODE -eq 0) { Write-Host "PASS  $($check.BaseName)" }
        else {
            $failed++
            # The check's own output, so the log says which case failed.
            Write-Host "FAIL  $($check.BaseName) (exit $LASTEXITCODE)"
            Write-Host $out
        }
    }
}
finally { Pop-Location }

Write-Host ''
Write-Host $(if ($failed -eq 0) { "all $(@($checks).Count) checks passed" } else { "$failed of $(@($checks).Count) checks FAILED" })
exit $failed
