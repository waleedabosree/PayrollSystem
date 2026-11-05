<#
.SYNOPSIS
  Creates the minimum Azure resources needed to test the payroll workflow end to end.

.DESCRIPTION
  Resource group   rg-payroll-dev (already created)
  Storage account  Standard_LRS, private blob container          ~ cents/month
  Service Bus      Basic namespace + queue 'payroll-jobs'        ~ $0.05 per million operations
  Azure SQL        Serverless DB using the FREE OFFER            $0 until the monthly free limit, then AUTO-PAUSES
  Firewall rule    your current public IP only

  Nothing here is created until you run it. Secrets are written to dotnet user-secrets and
  src/Payroll.Functions/local.settings.json (git-ignored) -- never printed.

  Teardown:  az group delete -n rg-payroll-dev --yes --no-wait
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = 'rg-payroll-dev',
    [string]$Location      = 'northeurope',
    # New subscriptions are often blocked from creating SQL servers in some regions
    # (RegionDoesNotAllowProvisioning). SQL is a separate resource, so it may live elsewhere.
    [string]$SqlLocation   = 'swedencentral',
    [string]$Suffix        = (-join ((97..122) | Get-Random -Count 6 | ForEach-Object { [char]$_ }))
)

$ErrorActionPreference = 'Stop'
$az = 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd'
$root = Split-Path $PSScriptRoot -Parent

$storage = "stpayroll$Suffix"
$sbName  = "sb-payroll-$Suffix"
$sqlSrv  = "sql-payroll-$Suffix"
$db      = 'PayrollDb'
$queue   = 'payroll-jobs'
$sqlUser = 'payrolladmin'
$sqlPass = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char]$_ }) + '!a1'
$myIp    = (Invoke-RestMethod 'https://api.ipify.org').Trim()

function Az { & $az @args --only-show-errors; if ($LASTEXITCODE) { throw "az $($args[0..2] -join ' ') failed (exit $LASTEXITCODE)" } }

Write-Host "1/4 Storage account $storage"
Az storage account create -n $storage -g $ResourceGroup -l $Location --sku Standard_LRS --kind StorageV2 `
   --min-tls-version TLS1_2 --allow-blob-public-access false -o none
$storageCs = Az storage account show-connection-string -n $storage -g $ResourceGroup --query connectionString -o tsv

Write-Host "2/4 Service Bus (Basic) + queue $queue"
Az servicebus namespace create -n $sbName -g $ResourceGroup -l $Location --sku Basic -o none
# MaxDeliveryCount=5 must match Processing:MaxDeliveryCount in the Function. 2-minute lock covers a slow batch.
Az servicebus queue create -n $queue --namespace-name $sbName -g $ResourceGroup `
   --max-delivery-count 5 --lock-duration PT2M -o none
$sbCs = Az servicebus namespace authorization-rule keys list --namespace-name $sbName -g $ResourceGroup `
   --name RootManageSharedAccessKey --query primaryConnectionString -o tsv

Write-Host "3/4 Azure SQL (free offer) $sqlSrv/$db"
Az sql server create -n $sqlSrv -g $ResourceGroup -l $SqlLocation -u $sqlUser -p $sqlPass -o none
Az sql server firewall-rule create -g $ResourceGroup -s $sqlSrv -n dev-machine --start-ip-address $myIp --end-ip-address $myIp -o none
Az sql db create -g $ResourceGroup -s $sqlSrv -n $db --edition GeneralPurpose --family Gen5 --capacity 2 `
   --compute-model Serverless --use-free-limit --free-limit-exhaustion-behavior AutoPause -o none
$sqlCs = "Server=tcp:$sqlSrv.database.windows.net,1433;Database=$db;User ID=$sqlUser;Password=$sqlPass;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;"

Write-Host "4/4 Writing local configuration (secrets not printed)"
$api = Join-Path $root 'src\Payroll.Api'
dotnet user-secrets set 'ConnectionStrings:PayrollDb' $sqlCs --project $api | Out-Null
dotnet user-secrets set 'Storage:ConnectionString'    $storageCs --project $api | Out-Null
dotnet user-secrets set 'ServiceBus:ConnectionString' $sbCs --project $api | Out-Null

$settings = [ordered]@{
    IsEncrypted = $false
    Values = [ordered]@{
        AzureWebJobsStorage          = $storageCs
        FUNCTIONS_WORKER_RUNTIME     = 'dotnet-isolated'
        ServiceBusConnection         = $sbCs
        PayrollQueueName             = $queue
        'AzureSql__ConnectionString' = $sqlCs
        'Storage__ConnectionString'  = $storageCs
    }
}
$settings | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'src\Payroll.Functions\local.settings.json')

Write-Host "Applying EF migrations to Azure SQL"
$env:ConnectionStrings__PayrollDb = $sqlCs
dotnet ef database update --project $api --startup-project $api
Remove-Item Env:\ConnectionStrings__PayrollDb

Write-Host "`nDone. Suffix=$Suffix  Storage=$storage  ServiceBus=$sbName  Sql=$sqlSrv"
