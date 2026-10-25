# PowerShell version of tools/ef.sh. Run from repo root:
#   .\tools\ef.ps1 add [Name] [Module...]
#   .\tools\ef.ps1 update
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('add', 'update')]
    [string]$Command,

    [string]$MigrationName = 'Initial',

    [string[]]$Modules = @('Accounts', 'Orders', 'Trades')
)
$ErrorActionPreference = 'Stop'

foreach ($m in $Modules) {
    $project = "src/SimpleBroker.$m"
    $context = "${m}DbContext"
    if ($Command -eq 'add') {
        dotnet ef migrations add $MigrationName -p $project -s src/SimpleBroker.Host -c $context -o Migrations
    } else {
        dotnet ef database update -p $project -s src/SimpleBroker.Host -c $context
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet ef failed for $m" }
}