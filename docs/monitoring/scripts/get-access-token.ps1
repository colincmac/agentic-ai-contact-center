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

# Convert only in memory, immediately before sending the token request.
$secretPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ApplicationClientSecret)

try {
    $plainTextSecret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($secretPointer)

    $tokenEndpoint = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"

    $tokenResponse = Invoke-RestMethod `
        -Method POST `
        -Uri $tokenEndpoint `
        -ContentType 'application/x-www-form-urlencoded' `
        -Body @{
            client_id     = $ApplicationClientId
            client_secret = $plainTextSecret
            scope         = 'https://graph.microsoft.com/.default'
            grant_type    = 'client_credentials'
        } `
        -ErrorAction Stop

    # Write only the raw token to the output pipeline.
    $tokenResponse.access_token
}
finally {
    if ($secretPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secretPointer)
    }

    Remove-Variable plainTextSecret -ErrorAction SilentlyContinue
}
