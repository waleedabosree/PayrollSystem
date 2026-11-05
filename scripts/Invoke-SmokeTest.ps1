<#
.SYNOPSIS
    End-to-end functional smoke test for the Payroll Processing API.

.DESCRIPTION
    Exercises every live endpoint plus the rejection paths, and asserts on the things that
    actually matter: status codes, the ProblemDetails envelope, correlation-ID round-tripping,
    enum serialisation, pagination semantics and filename sanitisation.

    Run it any time with:
        ./scripts/Invoke-SmokeTest.ps1

    Requires: the API on $BaseUrl, and local/docker-compose.yml up.

.NOTES
    Phase 1 scope: upload, list, detail, health, OpenAPI. Blob Storage and Service Bus arrive
    in Phase 2, so batches are expected to stop at status "Received".
#>

[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5088",
    [string]$Samples = (Join-Path $PSScriptRoot "..\samples")
)

$ErrorActionPreference = "Stop"
$script:passed = 0
$script:failed = 0
$script:results = New-Object System.Collections.Generic.List[object]

function Assert-Case {
    param(
        [string]$Name,
        [scriptblock]$Check,
        [string]$Detail = ""
    )

    # NB: values must be resolved into variables BEFORE building the result object.
    # `Something.Add(@{ X = if (...) {1} })` fails with "The term 'if' is not recognized",
    # because a method argument list accepts expressions only -- and `if` is a statement.
    try {
        $outcome = & $Check
        $ok = [bool]$outcome
        $why = ""
        if ($outcome -is [string]) { $why = $outcome }
    } catch {
        $ok = $false
        $why = $_.Exception.Message
    }

    if ($ok) { $script:passed++ } else { $script:failed++ }

    $label = "FAIL"
    $note  = "$why $Detail"
    if ($ok) {
        $label = "PASS"
        $note  = $why
    }

    $script:results.Add([pscustomobject]@{
        Result = $label
        Test   = $Name
        Note   = $note
    })
}

# curl helper -> returns status, content-type and body without throwing on 4xx
function Invoke-Curl {
    param([string[]]$CurlArgs, [string]$OutFile = "$env:TEMP\smoke-body.json")
    Remove-Item $OutFile -ErrorAction SilentlyContinue
    $meta = curl.exe -s -o $OutFile -w "%{http_code}|%{content_type}" @CurlArgs
    $parts = $meta -split "\|", 2
    $body = if (Test-Path $OutFile) { Get-Content $OutFile -Raw } else { "" }
    $contentType = ""
    if ($parts.Count -gt 1) { $contentType = $parts[1] }

    # Parse JSON defensively: /health/live returns the plain text "Healthy", and a throwing
    # parse here would abort the whole suite instead of failing a single assertion.
    $json = $null
    if (-not [string]::IsNullOrWhiteSpace($body)) {
        try { $json = $body | ConvertFrom-Json } catch { $json = $null }
    }

    return [pscustomobject]@{
        StatusCode  = [int]$parts[0]
        ContentType = $contentType
        Body        = $body
        Json        = $json
    }
}

function New-TempCsv {
    param([string]$Content, [string]$Name = "temp.csv")
    $path = Join-Path $env:TEMP $Name
    Set-Content -Path $path -Value $Content -Encoding utf8NoBOM -NoNewline
    return $path
}

Write-Host "`n=== Payroll API smoke test :: $BaseUrl ===" -ForegroundColor Cyan

# ---------------------------------------------------------------- health ----
$r = Invoke-Curl @("$BaseUrl/health/live")
Assert-Case "Health: /health/live is 200" { $r.StatusCode -eq 200 }

$r = Invoke-Curl @("$BaseUrl/health/ready")
Assert-Case "Health: /health/ready confirms SQL reachable" {
    $r.StatusCode -eq 200 -and $r.Json.status -eq "Healthy" -and $r.Json.checks.'azure-sql'.status -eq "Healthy"
}

# --------------------------------------------------------------- openapi ----
$r = Invoke-Curl @("$BaseUrl/openapi/v1.json")
Assert-Case "OpenAPI: document served with title" {
    $r.StatusCode -eq 200 -and $r.Json.info.title -eq "Payroll Processing API"
}
Assert-Case "OpenAPI: both endpoints documented" {
    ($null -ne $r.Json.paths.'/api/batches') -and ($null -ne $r.Json.paths.'/api/batches/{id}')
}

# ---------------------------------------------------------------- upload ----
$valid = Join-Path $Samples "payroll-valid.csv"
$clientCorrelation = ([guid]::NewGuid()).ToString("N")

$r = Invoke-Curl @(
    "-X", "POST", "$BaseUrl/api/batches/",
    "-H", "X-Correlation-Id: $clientCorrelation",
    "-F", "entityId=ACME-UK",
    "-F", "file=@$valid"
)

Assert-Case "Upload: valid file returns 202 Accepted" { $r.StatusCode -eq 202 }

Assert-Case "Upload: body carries batchId + correlationId + row count" {
    $r.Json.batchId -and $r.Json.correlationId -eq $clientCorrelation -and $r.Json.validatedRows -eq 5
} "body=$($r.Body)"

$batchId = $r.Json.batchId

Assert-Case "Upload: status serialises as NAME not ordinal" {
    $r.Json.status -eq "Received"
} "got '$($r.Json.status)' (an int here would render as a meaningless number in Angular)"

# ------------------------------------------------------- rejection paths ----
$badExt = $valid -replace "\.csv$", ".xlsx"
$r = Invoke-Curl @("-X", "POST", "$BaseUrl/api/batches/", "-F", "entityId=X", "-F", "file=@$badExt")
Assert-Case "Reject: wrong extension -> 400 VALIDATION_FAILED" {
    $r.StatusCode -eq 400 -and $r.ContentType -like "application/problem+json" -and
    $r.Json.code -eq "VALIDATION_FAILED" -and
    $r.Json.errors[0].code -eq "CSV_INVALID_EXTENSION"
}

$r = Invoke-Curl @("-X", "POST", "$BaseUrl/api/batches/", "-F", "file=@$valid")
Assert-Case "Reject: missing entityId -> 400 INVALID_ENTITY_ID" {
    $r.StatusCode -eq 400 -and $r.Json.code -eq "INVALID_ENTITY_ID"
}

$raggedPath = New-TempCsv -Content @"
EmployeeId,EmployeeName,PayPeriodStart,PayPeriodEnd,RegularHours,OvertimeHours,HourlyRate,Deductions,Currency
E-001,Only,Three
"@
$r = Invoke-Curl @("-X", "POST", "$BaseUrl/api/batches/", "-F", "entityId=X", "-F", "file=@$raggedPath")
Assert-Case "Reject: ragged row -> 400 with rowNumber=1 (header excluded)" {
    $r.StatusCode -eq 400 -and $r.Json.errors[0].code -eq "CSV_FIELD_COUNT_MISMATCH" -and
    $r.Json.errors[0].rowNumber -eq 1
} "errors=$($r.Body)"

$emptyPath = New-TempCsv -Content "" -Name "empty.csv"
$r = Invoke-Curl @("-X", "POST", "$BaseUrl/api/batches/", "-F", "entityId=X", "-F", "file=@$emptyPath")
Assert-Case "Reject: empty file -> 400 EMPTY_FILE" {
    $r.StatusCode -eq 400 -and $r.Json.code -eq "EMPTY_FILE"
}

$r = Invoke-Curl @(
    "-X", "POST", "$BaseUrl/api/batches/",
    "-H", "Content-Type: application/json",
    "-d", "{}"
)
Assert-Case "Reject: non-multipart body -> 400 UNSUPPORTED_MEDIA_TYPE" {
    $r.StatusCode -eq 400 -and $r.Json.code -eq "UNSUPPORTED_MEDIA_TYPE"
}

# Oversized: 10MB + 1 (validated at the edge before parsing)
$bigPath = Join-Path $env:TEMP "oversize.csv"
$hdr = "EmployeeId,EmployeeName,PayPeriodStart,PayPeriodEnd,RegularHours,OvertimeHours,HourlyRate,Deductions,Currency`n"
[System.IO.File]::WriteAllText($bigPath, $hdr + ("X" * 11 * 1024 * 1024))
$r = Invoke-Curl @("-X", "POST", "$BaseUrl/api/batches/", "-F", "entityId=X", "-F", "file=@$bigPath")
Assert-Case "Reject: file > 10MB -> 400 CSV_TOO_LARGE before parsing" {
    $r.StatusCode -eq 400 -and $r.Json.code -eq "CSV_TOO_LARGE"
}
Remove-Item $bigPath -ErrorAction SilentlyContinue

# Filename sanitisation: client-controlled path segments must not survive into storage
$travName = New-TempCsv -Content "$hdr`nE-001,Jane Doe,2026-01-01,2026-01-31,160,10,50.00,100.00,USD"
$renamed = Join-Path $env:TEMP "traversal.csv"
Copy-Item $travName $renamed -Force
$r = Invoke-Curl @(
    "-X", "POST", "$BaseUrl/api/batches/",
    "-F", "entityId=ACME-UK",
    "-F", "file=@$renamed;filename=../../../evil.csv"
)
Assert-Case "Security: path segments in filename are stripped" {
    $r.StatusCode -eq 202 -and $r.Json -ne $null
}
if ($r.StatusCode -eq 202) {
    $detail = Invoke-Curl @("$BaseUrl/api/batches/$($r.Json.batchId)")
    $stored = $detail.Json.originalFileName
    Assert-Case "Security: stored name has no directory traversal" {
        $stored -notmatch "[\\/]" -and $stored -eq "evil.csv"
    } "stored as '$stored'"
}

# -------------------------------------------------- business-rule deferral ----
# Deliberately business-invalid but structurally sound: must be ACCEPTED at the gate and
# left for the Function to fail in Phase 3. This documents the Structure/Full split.
$businessBad = Join-Path $Samples "payroll-invalid.csv"
$r = Invoke-Curl @("-X", "POST", "$BaseUrl/api/batches/", "-F", "entityId=ACME-UK", "-F", "file=@$businessBad")
Assert-Case "Design: business-rule errors deferred to processing (202 at upload)" {
    $r.StatusCode -eq 202
} "got $($r.StatusCode) - see ValidationDepth.Structure vs Full"

# ------------------------------------------------------------ list / query ----
$r = Invoke-Curl @("$BaseUrl/api/batches/?pageSize=2")
Assert-Case "List: returns paged envelope with items + total" {
    $r.StatusCode -eq 200 -and $r.Json.total -ge 1 -and $r.Json.items.Count -ge 1
} "total=$($r.Json.total)"

Assert-Case "List: enum serialised as string in items" {
    $r.Json.items[0].status -is [string] -and $r.Json.items[0].status -in @(
        "Received","Queued","Processing","Completed","Failed","DeadLettered","Retryable")
} "got '$($r.Json.items[0].status)'"

$p1 = Invoke-Curl @("$BaseUrl/api/batches/?pageSize=1&page=1")
$p2 = Invoke-Curl @("$BaseUrl/api/batches/?pageSize=1&page=2")
Assert-Case "Pagination: page 1 and page 2 return different items" {
    $p1.Json.total -gt 0 -and $p1.Json.items[0].id -ne $p2.Json.items[0].id
}

$filtered = Invoke-Curl @("$BaseUrl/api/batches/?status=Received")
$badFilter = Invoke-Curl @("$BaseUrl/api/batches/?status=Bogus")
Assert-Case "Filter: valid status accepted, invalid status -> 400 listing allowed values" {
    $filtered.StatusCode -eq 200 -and $badFilter.StatusCode -eq 400 -and
    $badFilter.Json.code -eq "INVALID_QUERY" -and
    $badFilter.Json.detail -match "DeadLettered"
} "bad=$($badFilter.StatusCode) $($badFilter.Json.code)"

$search = Invoke-Curl @("$BaseUrl/api/batches/?search=payroll-valid")
Assert-Case "Search: filename substring narrows the result set" {
    $search.StatusCode -eq 200 -and $search.Json.total -ge 1
} "total=$($search.Json.total)"

# ------------------------------------------------------------------ detail ----
$r = Invoke-Curl @("$BaseUrl/api/batches/$batchId")
Assert-Case "Detail: returns full record with correlationId" {
    $r.StatusCode -eq 200 -and $r.Json.id -eq $batchId -and $r.Json.correlationId -eq $clientCorrelation
}

$notFound = Invoke-Curl @("$BaseUrl/api/batches/00000000-0000-0000-0000-000000000000")
Assert-Case "Detail: unknown id -> 404 with code NOT_FOUND" {
    $notFound.StatusCode -eq 404 -and $notFound.Json.code -eq "NOT_FOUND"
}

# -------------------------------------------------- correlation on errors ----
$err = Invoke-Curl @("-X", "POST", "$BaseUrl/api/batches/", "-F", "file=@$valid")
Assert-Case "Diagnostics: error responses carry correlationId + traceId" {
    $err.Json.correlationId -and $err.Json.traceId
}

$colour = "Red"
if ($script:failed -eq 0) { $colour = "Green" }
$exitCode = 1
if ($script:failed -eq 0) { $exitCode = 0 }

Write-Host ""
Write-Host "=== Results: $script:passed passed, $script:failed failed ===" -ForegroundColor $colour

$script:results |
    Sort-Object @{Expression = { if ($_.Result -eq "FAIL") { 0 } else { 1 } }}, Test |
    Format-Table -AutoSize -Wrap Result, Test, Note

exit $exitCode