[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ApplicationClientId,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $TenantId,

    [Parameter(Mandatory)]
    [ValidateNotNull()]
    [securestring] $ApplicationClientSecret
)
# $SecureClientSecret = ConvertTo-SecureString -String $ApplicationClientSecret -AsPlainText -Force

$ClientSecretCredential  = [System.Management.Automation.PSCredential]::new(
    $ApplicationClientId,
    $ApplicationClientSecret
)

try {
    Connect-MgGraph `
        -TenantId $TenantId `
        -ClientSecretCredential $ClientSecretCredential  `
        -NoWelcome `
        -ErrorAction Stop

    $response = Invoke-MgGraphRequest `
        -Method GET `
        -Uri 'https://graph.microsoft.com/v1.0/communications/callRecords/getPstnCalls(fromDateTime=2026-06-07,toDateTime=2026-06-10)' `
        -ErrorAction Stop

    $response.value
}
finally {
    Disconnect-MgGraph -ErrorAction SilentlyContinue
}
