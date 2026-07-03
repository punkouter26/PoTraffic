# lint-inline-styles.ps1 — fail the build if any non-dynamic `style="..."` attribute
# exists in src/PoTraffic.Client/**/*.razor. Dynamic CSS-var bindings
# (style="@expr" and style="--var:@expr") are allowed.
# Run from repo root: ./SCRIPTS/lint-inline-styles.ps1

[CmdletBinding()]
param(
    [string]$Root = "src/PoTraffic.Client",
    # Allow dynamic CSS-variable bindings (style="@someExpression") and
    # explicit --foo:...; declarations.
    [string]$AllowPattern = 'style="--[a-z-]+:[^"]*"|style="--[a-z-]+-[a-z-]+:[^"]*"|style="@[A-Za-z_][A-Za-z0-9_]*"'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$violations = @()
Get-ChildItem -Recurse -Path $Root -Include *.razor -ErrorAction SilentlyContinue | ForEach-Object {
    $matches = Select-String -Path $_.FullName -Pattern ' style="[^"]*"' -AllMatches
    foreach ($m in $matches) {
        $snippet = $m.Line.Trim()
        if ($snippet -notmatch $AllowPattern) {
            $rel = $_.FullName.Substring($_.FullName.IndexOf("$Root\") + $Root.Length + 1)
            $violations += [PSCustomObject]@{ File = $rel; Line = $m.LineNumber; Snippet = $snippet }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "Inline style attributes found in $($violations.Count) places:" -ForegroundColor Yellow
    $violations | Format-Table -AutoSize | Out-String | Write-Host
    exit 1
}

Write-Host "No inline style attributes found in $Root." -ForegroundColor Green
exit 0