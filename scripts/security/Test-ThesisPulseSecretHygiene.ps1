#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$failures = New-Object System.Collections.Generic.List[string]

$scanExtensions = @(
    ".cs", ".csproj", ".props", ".targets",
    ".ps1", ".psm1",
    ".json", ".jsonc",
    ".yml", ".yaml",
    ".md", ".txt",
    ".sql",
    ".ts", ".tsx", ".js", ".jsx",
    ".py", ".toml",
    ".env", ".example"
)

$sourceExpressionExtensions = @(
    ".cs", ".ps1", ".psm1",
    ".ts", ".tsx", ".js", ".jsx",
    ".py"
)

$excludedPathFragments = @(
    "/.git/",
    "/.vs/",
    "/.venv/",
    "/venv/",
    "/node_modules/",
    "/bin/",
    "/obj/",
    "/dist/",
    "/coverage/",
    "/.thesispulse-dev/"
)
$excludedFileNames = @(
    "package-lock.json",
    "project.assets.json",
    "yarn.lock",
    "pnpm-lock.yaml"
)

$placeholderPattern = [regex]::new(
    "(<[^>]+>|\$\{[^}]+\}|%[^%]+%|PLACEHOLDER|REPLACE_ME|REDACTED|EXAMPLE|SAMPLE|DUMMY|TEST|LOCAL|GENERATED|UNVERSIONED|YOUR_|_HERE)",
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

$ruleDefinitions = @(
    @{ Name = "PrivateKeyBlock"; Pattern = "-----BEGIN (?:RSA |EC |OPENSSH |DSA |)?PRIVATE KEY-----" },
    @{ Name = "BearerTokenLiteral"; Pattern = "Bearer\s+[A-Za-z0-9_\-\.]{24,}" },
    @{ Name = "JwtLiteral"; Pattern = "eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}" },
    @{ Name = "CloudAccessKeyLiteral"; Pattern = "\b(?:AKIA|ASIA)[A-Z0-9]{16}\b" },
    @{ Name = "RawSqlPasswordConnectionString"; Pattern = "(?i)(Server|Data Source)=.+;(User ID|UID)=.+;(Password|PWD)=([^;<>{}\s$][^;]{7,})" }
)

$assignmentPattern = [regex]::new(
    "(?i)(api[_-]?key|api[_-]?secret|client[_-]?secret|signing[_-]?key|password|passwd|pwd|access[_-]?token|refresh[_-]?token|bearer[_-]?token|connection[_-]?string)\s*[:=]\s*([`"']?)([^`"'\s,;]{12,})",
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

function Should-SkipFile {
    param([System.IO.FileInfo]$File)

    if ($excludedFileNames -contains $File.Name) {
        return $true
    }

    if ($scanExtensions -notcontains $File.Extension) {
        return $true
    }

    $normalizedPath = $File.FullName.Replace('\', '/')
    $relativeNormalizedPath = $normalizedPath.Substring($repositoryRoot.Replace('\', '/').Length + 1)
    if ($relativeNormalizedPath -eq "scripts/security/Test-ThesisPulseSecretHygiene.ps1") {
        return $true
    }

    foreach ($fragment in $excludedPathFragments) {
        if ($normalizedPath.IndexOf($fragment, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}

function Is-AllowedLine {
    param([string]$Line)

    if ($placeholderPattern.IsMatch($Line)) {
        return $true
    }

    if ($Line -match "^\s*@\{ Name = ") {
        return $true
    }

    if ($Line -match '^\s*"\(\?i\)\(') {
        return $true
    }

    if ($Line -match "(?i)(localhost|127\.0\.0\.1|::1)") {
        return $true
    }

    if ($Line -match "(?i)(TokenType|PasswordChar|PasswordBox|PasswordSignIn|PasswordChanged)") {
        return $true
    }

    if ($Line -match "(?i)(Invalid credentials|generated password|one-time password|password is not persisted|wrong|test-password)") {
        return $true
    }
	if ($Line -match '(?i)(builder\.Configuration|configuration)\s*(\.GetValue|\.GetConnectionString|\[)') {
        return $true
    }

    if ($Line -match '(?i)(api[_-]?key|api[_-]?secret|client[_-]?secret|signing[_-]?key|password|passwd|pwd|access[_-]?token|refresh[_-]?token|bearer[_-]?token|connection[_-]?string)\s*[:=]\s*(\$|[A-Za-z_][A-Za-z0-9_]*\.|[A-Za-z_][A-Za-z0-9_]*\()') {
        return $true
    }

    return $false
}

function Add-Finding {
    param(
        [string]$RelativePath,
        [int]$LineNumber,
        [string]$RuleName
    )

    $failures.Add("${RelativePath}:$LineNumber possible secret/config leak [$RuleName]")
}

$files = Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File |
    Where-Object { -not (Should-SkipFile $_) }

foreach ($file in $files) {
    $relativePath = $file.FullName.Substring($repositoryRoot.Length + 1)
    $lines = @(Get-Content -LiteralPath $file.FullName)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = [string]$lines[$index]
        if (Is-AllowedLine $line) {
            continue
        }

        foreach ($rule in $ruleDefinitions) {
            if ($line -match $rule.Pattern) {
                Add-Finding -RelativePath $relativePath -LineNumber ($index + 1) -RuleName $rule.Name
            }
        }

        $assignment = $assignmentPattern.Match($line)
		if ($assignment.Success) {
			$quote = $assignment.Groups[2].Value
			$value = $assignment.Groups[3].Value
			$isSourceExpression = $sourceExpressionExtensions -contains $file.Extension -and
				[string]::IsNullOrEmpty($quote) -and
				(
					$value -match "^[A-Za-z_][A-Za-z0-9_-]*(\.[A-Za-z_][A-Za-z0-9_]*)*(\([^)]*)?$" -or
					$value -match "^\[[A-Za-z_.]+\]::[A-Za-z_][A-Za-z0-9_]*\([^)]*$" -or
					$value -match "^\$[A-Za-z_][A-Za-z0-9_]*$"
				)

			if ($value.Length -ge 12 -and
                $value -notmatch "(?i)(placeholder|redacted|example|sample|dummy|test|local|generated|unversioned|your_|_here|localhost)" -and
                $value -notmatch "(?i)^(self\.)?(encodedToken|internal_key|internal_api_key|api_key|api_secret|client_secret|signing_key|password|passwd|pwd|access_token|refresh_token|bearer_token|connection_string|database_connection_string)$" -and
                -not $isSourceExpression -and
                $value -notmatch "^[A-Z_]+$" -and
                $value -notmatch "^\$") {
                Add-Finding -RelativePath $relativePath -LineNumber ($index + 1) -RuleName "SecretLikeAssignment"
            }
        }
    }
}

$gitignorePath = Join-Path $repositoryRoot ".gitignore"
if (-not (Test-Path -LiteralPath $gitignorePath -PathType Leaf)) {
    $failures.Add(".gitignore is missing")
} else {
    $gitignore = Get-Content -LiteralPath $gitignorePath -Raw
    foreach ($requiredPattern in @(
        "/.env",
        "/.env.*",
        "*.pfx",
        "*.pem",
        "*.key",
        "*.dump",
        "*.bak",
        "*.log",
        "/secrets/")) {
        if ($gitignore.IndexOf($requiredPattern, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            $failures.Add(".gitignore missing secret hygiene pattern: $requiredPattern")
        }
    }
}

$powerShellFiles = Get-ChildItem -LiteralPath $repositoryRoot -Recurse -Filter "*.ps1" -File
foreach ($file in $powerShellFiles) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $file.FullName,
        [ref]$tokens,
        [ref]$errors) | Out-Null
    foreach ($parseError in @($errors)) {
        $relativePath = $file.FullName.Substring($repositoryRoot.Length + 1)
        $failures.Add("PowerShell parse error in ${relativePath}: $($parseError.Message)")
    }
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Phase 6.3 secret hygiene validation failed:")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }
    exit 1
}

Write-Host "Phase 6.3 secret hygiene validation passed."
Write-Host "Scanned repository text files for secret-like literals without printing secret values."
exit 0
