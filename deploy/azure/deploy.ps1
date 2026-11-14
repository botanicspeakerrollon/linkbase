<#
.SYNOPSIS
  Deploys the Linkbase demo to Azure, and redeploys it after a change. See README.md in this folder.

.DESCRIPTION
  Creates (or updates) a resource group holding a Container App, Postgres, a registry and a Key Vault, builds the image in the
  registry, and puts the secrets in the vault. Your Anthropic key is typed at a hidden prompt: it goes to the vault and nowhere
  else, is never in a file in the repo, a command line, an environment variable or a deployment parameter, and the app reads it
  from the vault with its own identity.

  Safe to run again: existing secrets are kept, and the example data loads only into an empty database.

.PARAMETER AllowedCidr
  Addresses allowed to reach the demo, such as 203.0.113.7/32. Defaults to your current public address.
.PARAMETER AllowAnyone
  Leaves the demo open to the internet. The demo's sign-in has no password, so only do this with something else in front.
.PARAMETER NoAnthropic
  Deploys without an Anthropic key. Read text and Ask are then switched off.
.PARAMETER ReplaceAnthropicKey
  Asks for the key again, even if the vault already holds one.
#>
[CmdletBinding()]
param(
    [string] $ResourceGroup = 'rg-linkbase-demo',
    [string] $Location = 'uksouth',
    [string[]] $AllowedCidr,
    [switch] $AllowAnyone,
    [switch] $NoAnthropic,
    [switch] $ReplaceAnthropicKey
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("linkbase-azure-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null

# Runs az and stops on failure. Windows PowerShell treats anything az writes to stderr as an error, so it's let through.
function Invoke-Az {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & az @args 2>&1 | ForEach-Object { "$_" }
        if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed:`n$($output -join "`n")" }
        return ($output -join "`n").Trim()
    } finally { $ErrorActionPreference = $previous }
}

# Writes a secret to the vault from a file that exists only for that moment, so the value is never on a command line.
function Set-VaultSecret([string] $Vault, [string] $Name, [string] $Value) {
    $file = Join-Path $tmp ([guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($file, $Value, (New-Object Text.UTF8Encoding($false)))
    try {
        # The role that lets this account write takes a minute to arrive after the first deployment.
        for ($try = 1; $try -le 12; $try++) {
            try {
                Invoke-Az keyvault secret set --vault-name $Vault --name $Name --file $file --encoding utf-8 --output none
                return
            } catch {
                if ($try -eq 12) { throw }
                Write-Host "  waiting for access to the vault ($try)..."
                Start-Sleep -Seconds 10
            }
        }
    } finally { Remove-Item $file -Force -ErrorAction SilentlyContinue }
}

function Test-VaultSecret([string] $Vault, [string] $Name) {
    try { Invoke-Az keyvault secret show --vault-name $Vault --name $Name --query id --output tsv | Out-Null; return $true }
    catch { return $false }
}

try {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw "The Azure CLI isn't installed. Run: winget install Microsoft.AzureCLI"
    }
    try { $account = Invoke-Az account show --output json | ConvertFrom-Json }
    catch { throw "Not signed in. Run: az login" }
    Write-Host "Subscription: $($account.name) ($($account.id))"
    Invoke-Az provider register --namespace Microsoft.App --wait --output none
    Invoke-Az provider register --namespace Microsoft.OperationalInsights --wait --output none

    # Who may reach the demo.
    if ($AllowAnyone) {
        $cidrs = @()
        Write-Warning 'Open to the whole internet. Anyone who finds the address can sign in as anyone.'
    } elseif ($AllowedCidr) {
        $cidrs = @($AllowedCidr)
    } else {
        $ip = (Invoke-RestMethod -Uri 'https://api.ipify.org').Trim()
        $cidrs = @("$ip/32")
        Write-Host "Only $ip can reach the demo. Add others with -AllowedCidr, and see README.md for signing in through Entra instead."
    }

    $deployer = Invoke-Az ad signed-in-user show --query id --output tsv
    Invoke-Az group create --name $ResourceGroup --location $Location --output none

    # The database password: kept if a previous run made one, otherwise new. Letters and digits only, so it needs no escaping
    # in a connection string.
    $vault = $null
    $existing = Invoke-Az keyvault list --resource-group $ResourceGroup --query '[0].name' --output tsv
    if ($existing) { $vault = $existing }
    $password = $null
    if ($vault -and (Test-VaultSecret $vault 'postgres-password')) {
        $password = Invoke-Az keyvault secret show --vault-name $vault --name 'postgres-password' --query value --output tsv
    }
    if (-not $password) {
        $chars = [char[]]'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
        $bytes = New-Object byte[] 32
        $random = New-Object Security.Cryptography.RNGCryptoServiceProvider
        $random.GetBytes($bytes)
        $random.Dispose()
        $password = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
    }

    function Invoke-Deployment([bool] $DeployApp, [string] $ImageTag) {
        $parametersFile = Join-Path $tmp 'parameters.json'
        $parameters = @{
            '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
            contentVersion = '1.0.0.0'
            parameters = @{
                deployerObjectId = @{ value = $deployer }
                postgresPassword = @{ value = $password }
                allowedCidrs = @{ value = @($cidrs) }
                deployApp = @{ value = $DeployApp }
                imageTag = @{ value = $ImageTag }
                extractionEnabled = @{ value = (-not $NoAnthropic) }
            }
        }
        [IO.File]::WriteAllText($parametersFile, ($parameters | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
        try {
            $name = if ($DeployApp) { 'linkbase-app' } else { 'linkbase-base' }
            return Invoke-Az deployment group create --resource-group $ResourceGroup --name $name `
                --template-file (Join-Path $PSScriptRoot 'main.bicep') --parameters "@$parametersFile" `
                --query properties.outputs --output json | ConvertFrom-Json
        } finally { Remove-Item $parametersFile -Force -ErrorAction SilentlyContinue }
    }

    Write-Host "`n1/4  Vault, registry, database and identity..."
    $base = Invoke-Deployment $false 'latest'
    $vault = $base.vaultName.value
    $registry = $base.registryName.value
    $dbHost = $base.postgresHost.value
    $dbUser = $base.postgresAdmin.value

    Write-Host "`n2/4  Secrets, into vault $vault..."
    Set-VaultSecret $vault 'postgres-password' $password
    $databases = @{
        'cs-climbing-demo' = 'climbing_demo'; 'cs-consultancy' = 'consultancy'
        'cs-linkbase-code' = 'linkbase_code'; 'cs-archimate' = 'archimate'
    }
    foreach ($secret in $databases.Keys) {
        # Require encrypts without checking the certificate's chain, which is enough for a demo of made-up data.
        $connection = "Host=$dbHost;Database=$($databases[$secret]);Username=$dbUser;Password=$password;SSL Mode=Require;Trust Server Certificate=true"
        Set-VaultSecret $vault $secret $connection
    }
    if (-not $NoAnthropic) {
        if ($ReplaceAnthropicKey -or -not (Test-VaultSecret $vault 'anthropic-api-key')) {
            $secure = Read-Host -AsSecureString 'Anthropic API key (hidden; goes straight to the vault)'
            $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
            try { $key = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
            finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
            if ([string]::IsNullOrWhiteSpace($key)) { throw 'No key entered. Run again, or use -NoAnthropic.' }
            Set-VaultSecret $vault 'anthropic-api-key' $key.Trim()
            $key = $null
        } else {
            Write-Host '  The vault already holds an Anthropic key. -ReplaceAnthropicKey changes it.'
        }
    }

    Write-Host "`n3/4  Building the image in $registry (a few minutes)..."
    $tag = Get-Date -Format 'yyyyMMddHHmmss'
    Push-Location $repo
    try { Invoke-Az acr build --registry $registry --image "linkbase:$tag" --file Dockerfile . --output none }
    finally { Pop-Location }

    Write-Host "`n4/4  The app..."
    $app = Invoke-Deployment $true $tag

    Write-Host "`nDone: $($app.appUrl.value)"
    Write-Host 'The first start loads the example data into four databases, which takes a minute or two.'
    Write-Host "Sign in at $($app.appUrl.value)/signin"
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
