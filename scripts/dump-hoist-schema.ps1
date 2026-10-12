<#
.SYNOPSIS
  Dumps the DDL of the Hoist (xh_*) tables from an existing hoist-core MySQL database into
  docs/schema/hoist-core-42-mysql.sql - the reference the EF mapping is checked against.

.EXAMPLE
  ./scripts/dump-hoist-schema.ps1 -DbHost localhost -Schema hoist -User hoist
  (prompts for the password; nothing is written except the schema-only DDL)
#>
param(
    [string]$DbHost = "localhost",
    [Parameter(Mandatory = $true)][string]$Schema,
    [Parameter(Mandatory = $true)][string]$User,
    [string]$MySqlDump = "c:\Projects\mysqldump.exe",
    [string]$MySql = "c:\Projects\mysql.exe"
)

$securePwd = Read-Host -AsSecureString "MySQL password for $User"
$pwd = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePwd))
$env:MYSQL_PWD = $pwd
try {
    $tables = & $MySql -h $DbHost -u $User -N -e "SELECT table_name FROM information_schema.tables WHERE table_schema='$Schema' AND table_name LIKE 'xh\_%' ORDER BY table_name" $Schema
    if (-not $tables) { throw "No xh_* tables found in schema $Schema" }

    $out = Join-Path $PSScriptRoot "..\docs\schema\hoist-core-42-mysql.sql"
    & $MySqlDump -h $DbHost -u $User --no-data --skip-comments --skip-add-drop-table --skip-set-charset $Schema @tables |
        ForEach-Object { $_ -replace ' AUTO_INCREMENT=\d+', '' } |
        Set-Content -Encoding utf8 $out
    Write-Host "Wrote $($tables.Count) tables to $out"
}
finally {
    Remove-Item Env:\MYSQL_PWD -ErrorAction SilentlyContinue
}
