# Signs files with Azure Artifact Signing, through signtool and Microsoft's signing client (dlib).
#
# release.yml calls it for SwissOlly's own binaries before Inno Setup packs them, and Inno Setup
# calls it (as the sign tool given to ISCC with /S) for Setup and the uninstaller. One path for
# every signature, so they all carry the same publisher.
#
# Needs an Azure sign-in (azure/login in the workflow) and these variables, which the workflow's
# "Prepare signing" step sets:
#   SWISSOLLY_SIGNTOOL        signtool.exe (Windows SDK 10.0.22621.755 or later)
#   SWISSOLLY_SIGN_DLIB       Azure.CodeSigning.Dlib.dll, the x64 one to match signtool
#   SWISSOLLY_SIGN_METADATA   metadata.json: endpoint, account and certificate profile
# Optional:
#   SWISSOLLY_PUBLISHER       the publisher name we publish. When set, every signature's subject
#                             must match it exactly, or the release stops. The in-app update
#                             check will compare against the same name.

param(
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
    [string[]] $Path
)

$ErrorActionPreference = 'Stop'

foreach ($name in 'SWISSOLLY_SIGNTOOL', 'SWISSOLLY_SIGN_DLIB', 'SWISSOLLY_SIGN_METADATA') {
    if (-not [Environment]::GetEnvironmentVariable($name)) {
        Write-Error "$name isn't set. Run the workflow's Prepare signing step first."
        exit 1
    }
}

foreach ($file in $Path) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        Write-Error "Nothing to sign at $file."
        exit 1
    }
}

# The certificate lives three days, so the timestamp is what keeps a signature valid afterwards.
& $env:SWISSOLLY_SIGNTOOL sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 `
    /dlib $env:SWISSOLLY_SIGN_DLIB /dmdf $env:SWISSOLLY_SIGN_METADATA @Path
if ($LASTEXITCODE -ne 0) {
    Write-Error "signtool returned $LASTEXITCODE."
    exit $LASTEXITCODE
}

# Check what a user's PC will check: a valid, trusted signature from the expected publisher.
foreach ($file in $Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -ne 'Valid') {
        Write-Error "$file is not validly signed after signing: $($signature.Status) ($($signature.StatusMessage))."
        exit 1
    }

    $publisher = $signature.SignerCertificate.GetNameInfo(
        [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    if ($env:SWISSOLLY_PUBLISHER -and $publisher -cne $env:SWISSOLLY_PUBLISHER) {
        Write-Error "$file is signed by '$publisher', but the published name is '$env:SWISSOLLY_PUBLISHER'."
        exit 1
    }

    Write-Host "Signed $([System.IO.Path]::GetFileName($file)) as '$publisher'."
}

exit 0
