<#
.SYNOPSIS
  Removes the Linkbase demo from Azure: everything deploy.ps1 created. See README.md in this folder.

.DESCRIPTION
  Deletes the resource group, which takes the Container App, Postgres (and every database in it), the registry, the vault with
  its secrets, Log Analytics and Application Insights with it. This cannot be undone. Nothing outside the resource group is
  touched, and nothing on your PC.

  It lists what is in the group and asks you to type the group's name before deleting. Deleting the group leaves the vault
  soft-deleted for seven days; -PurgeVault removes it for good now, which also erases the secrets, the Anthropic key included.
  (deploy.ps1 purges it itself the next time it runs, so this is only for a clean break.)

.PARAMETER ResourceGroup
  The group to delete. Defaults to rg-linkbase, as deploy.ps1 does.
.PARAMETER PurgeVault
  After the delete, permanently purges the group's soft-deleted Key Vault.
.PARAMETER Yes
  Skips the typed confirmation, for scripts. Without it, you type the resource group's name.
.PARAMETER NoWait
  Returns as soon as Azure accepts the delete, instead of waiting for it to finish. Ignored with -PurgeVault, which needs the
  delete to finish first.
#>
[CmdletBinding()]
param(
    [string] $ResourceGroup = 'rg-linkbase',
    [switch] $PurgeVault,
    [switch] $Yes,
    [switch] $NoWait
)

$ErrorActionPreference = 'Stop'

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

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "The Azure CLI isn't installed. Run: winget install Microsoft.AzureCLI"
}
try { $account = Invoke-Az account show --output json | ConvertFrom-Json }
catch { throw "Not signed in. Run: az login" }
Write-Host "Subscription: $($account.name) ($($account.id))"

$exists = (Invoke-Az group exists --name $ResourceGroup) -eq 'true'
if (-not $exists) {
    Write-Host "Resource group $ResourceGroup doesn't exist, so there's nothing to delete."
} else {
    $resources = Invoke-Az resource list --resource-group $ResourceGroup --query '[].{name:name, type:type}' --output json | ConvertFrom-Json
    Write-Host "`nResource group $ResourceGroup holds $(@($resources).Count) resources:"
    foreach ($r in @($resources)) { Write-Host "  $($r.type)  $($r.name)" }
    # Remember the vaults now: once the group is gone they can only be found in the soft-deleted list.
    $vaults = @(@($resources) | Where-Object { $_.type -eq 'Microsoft.KeyVault/vaults' } | ForEach-Object { $_.name })

    Write-Host "`nThis deletes all of them, every database and every secret. It cannot be undone."
    if (-not $Yes) {
        $typed = Read-Host "Type the resource group's name ($ResourceGroup) to confirm"
        if ($typed -ne $ResourceGroup) { Write-Host 'Not confirmed. Nothing was deleted.'; return }
    }

    if ($NoWait -and -not $PurgeVault) {
        Invoke-Az group delete --name $ResourceGroup --yes --no-wait --output none
        Write-Host "Delete requested. It finishes in a few minutes: az group exists --name $ResourceGroup"
    } else {
        Write-Host "Deleting $ResourceGroup (a few minutes)..."
        Invoke-Az group delete --name $ResourceGroup --yes --output none
        Write-Host 'Deleted.'
    }

    if ($PurgeVault) {
        foreach ($name in $vaults) {
            $deleted = Invoke-Az keyvault list-deleted --query "[?name=='$name'] | [0].properties.location" --output tsv
            if ($deleted) {
                Write-Host "Purging the vault $name..."
                Invoke-Az keyvault purge --name $name --location $deleted --output none
            } else {
                Write-Host "The vault $name isn't in the soft-deleted list (already purged, or purge protection is on)."
            }
        }
    } elseif ($vaults) {
        Write-Host "The vault $($vaults -join ', ') is soft-deleted for seven days. deploy.ps1 purges it on its next run, or use -PurgeVault."
    }
}

Write-Host "`nLeft on your PC: the images deploy.ps1 built (docker image ls) and your az login. Neither costs anything in Azure."
