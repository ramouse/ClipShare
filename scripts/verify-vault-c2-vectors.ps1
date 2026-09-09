[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertFrom-Hex {
    param([Parameter(Mandatory)][string]$Hex)
    if ($Hex.Length % 2 -ne 0 -or $Hex -notmatch '^[0-9a-f]*$') {
        throw 'Invalid lowercase hexadecimal test vector.'
    }
    $result = [byte[]]::new($Hex.Length / 2)
    for ($index = 0; $index -lt $result.Length; $index++) {
        $result[$index] = [Convert]::ToByte($Hex.Substring($index * 2, 2), 16)
    }
    return $result
}

function ConvertFrom-Uuid {
    param([Parameter(Mandatory)][string]$Value)
    return ConvertFrom-Hex ($Value.Replace('-', ''))
}

function ConvertTo-U16BigEndian {
    param([Parameter(Mandatory)][int]$Value)
    if ($Value -lt 0 -or $Value -gt [uint16]::MaxValue) { throw 'u16 out of range.' }
    $result = [byte[]]::new(2)
    $result[0] = [byte](($Value -shr 8) -band 0xff)
    $result[1] = [byte]($Value -band 0xff)
    return $result
}

function ConvertTo-U64BigEndian {
    param([Parameter(Mandatory)][long]$Value)
    if ($Value -lt 0) { throw 'u64 contract value must be non-negative.' }
    $result = [byte[]]::new(8)
    $remaining = [uint64]$Value
    for ($index = 7; $index -ge 0; $index--) {
        $result[$index] = [byte]($remaining -band 0xff)
        $remaining = $remaining -shr 8
    }
    return $result
}

function Join-Bytes {
    param([Parameter(Mandatory)][object[]]$Parts)
    $length = 0
    foreach ($part in $Parts) { $length += ([byte[]]$part).Length }
    $result = [byte[]]::new($length)
    $offset = 0
    foreach ($part in $Parts) {
        $bytes = [byte[]]$part
        [Array]::Copy($bytes, 0, $result, $offset, $bytes.Length)
        $offset += $bytes.Length
    }
    return $result
}

function ConvertTo-LengthPrefixedUtf8 {
    param([Parameter(Mandatory)][string]$Value)
    $utf8 = [Text.UTF8Encoding]::new($false, $true).GetBytes($Value)
    return Join-Bytes @((ConvertTo-U16BigEndian $utf8.Length), $utf8)
}

function Get-HkdfKey {
    param(
        [Parameter(Mandatory)][byte[]]$EpochSecret,
        [Parameter(Mandatory)][string]$VaultId,
        [Parameter(Mandatory)][string]$Purpose,
        [Parameter(Mandatory)][string]$OriginDeviceId
    )
    $extract = [Security.Cryptography.HMACSHA256]::new((ConvertFrom-Uuid $VaultId))
    try { $prk = $extract.ComputeHash($EpochSecret) } finally { $extract.Dispose() }
    $info = Join-Bytes @(
        ([Text.Encoding]::ASCII.GetBytes("clipshare:vault:key:v1`0")),
        (ConvertTo-LengthPrefixedUtf8 $Purpose),
        (ConvertFrom-Uuid $OriginDeviceId),
        ([byte[]]@(1))
    )
    $expand = [Security.Cryptography.HMACSHA256]::new($prk)
    try { return $expand.ComputeHash($info) } finally { $expand.Dispose() }
}

function Get-Aad {
    param([Parameter(Mandatory)]$Vector)
    return Join-Bytes @(
        ([Text.Encoding]::ASCII.GetBytes("clipshare:vault:aad:v1`0")),
        (ConvertTo-U16BigEndian 1),
        (ConvertTo-LengthPrefixedUtf8 ([string]$Vector.purpose)),
        (ConvertFrom-Uuid ([string]$Vector.vaultId)),
        (ConvertFrom-Uuid ([string]$Vector.originDeviceId)),
        (ConvertFrom-Uuid ([string]$Vector.entityId)),
        (ConvertTo-LengthPrefixedUtf8 ([string]$Vector.field)),
        (ConvertTo-U64BigEndian ([long]$Vector.keyEpoch)),
        (ConvertTo-U64BigEndian ([long]$Vector.nonceCounter)),
        (ConvertTo-U64BigEndian ([long]$Vector.paddedPlaintextBytes))
    )
}

function Protect-AesGcm {
    param(
        [Parameter(Mandatory)][byte[]]$Key,
        [Parameter(Mandatory)][byte[]]$Nonce,
        [Parameter(Mandatory)][byte[]]$Plaintext,
        [Parameter(Mandatory)][byte[]]$Aad
    )
    $ciphertext = [byte[]]::new($Plaintext.Length)
    $tag = [byte[]]::new(16)
    $aes = [Security.Cryptography.AesGcm]::new($Key, 16)
    try { $aes.Encrypt($Nonce, $Plaintext, $ciphertext, $tag, $Aad) } finally { $aes.Dispose() }
    return Join-Bytes @($ciphertext, $tag)
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)][byte[]]$Value)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Value)).ToLowerInvariant()
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$vectorsPath = Join-Path $repositoryRoot 'contracts/vault/test-vectors/positive-vectors.json'
$suite = Get-Content -LiteralPath $vectorsPath -Raw | ConvertFrom-Json
$item = $suite.vectors | Where-Object id -eq 'epoch1-item-payload-4k'
$epoch2 = $suite.vectors | Where-Object id -eq 'independent-epoch2-same-context'
$chunk = $suite.vectors | Where-Object id -eq 'file-generation-chunk-3'
if ($null -eq $item -or $null -eq $epoch2 -or $null -eq $chunk) { throw 'Required vectors missing.' }

$itemKey = Get-HkdfKey (ConvertFrom-Hex $item.epochSecretHex) $item.vaultId $item.purpose $item.originDeviceId
if ([Convert]::ToHexString($itemKey).ToLowerInvariant() -ne $item.derivedKeyHex) {
    throw 'Item HKDF vector mismatch.'
}
$payload = ConvertFrom-Hex $item.payloadHex
$framed = [byte[]]::new([int]$item.paddedPlaintextBytes)
(ConvertTo-U64BigEndian $payload.Length).CopyTo($framed, 0)
$payload.CopyTo($framed, 8)
if ((Get-Sha256Hex $framed) -ne $item.paddedPlaintextSha256Hex) {
    throw '4 KiB frame vector mismatch.'
}
$itemAad = Get-Aad $item
if ([Convert]::ToHexString($itemAad).ToLowerInvariant() -ne $item.aadHex) {
    throw 'Item AAD vector mismatch.'
}
$itemEncrypted = Protect-AesGcm $itemKey (ConvertFrom-Hex $item.nonceHex) $framed $itemAad
if ((Get-Sha256Hex $itemEncrypted) -ne $item.cipherAndTagSha256Hex) {
    throw 'Item AES-GCM vector mismatch.'
}

$epoch2Key = Get-HkdfKey (ConvertFrom-Hex $epoch2.epochSecretHex) $epoch2.vaultId $epoch2.purpose $epoch2.originDeviceId
if ([Convert]::ToHexString($epoch2Key).ToLowerInvariant() -ne $epoch2.derivedKeyHex) {
    throw 'Independent epoch HKDF vector mismatch.'
}
if ([Convert]::ToHexString($itemKey) -eq [Convert]::ToHexString($epoch2Key)) {
    throw 'Independent epoch keys unexpectedly match.'
}

$chunkAad = Get-Aad $chunk
if ([Convert]::ToHexString($chunkAad).ToLowerInvariant() -ne $chunk.aadHex) {
    throw 'File chunk AAD vector mismatch.'
}
$chunkEncrypted = Protect-AesGcm (ConvertFrom-Hex $chunk.fileDekHex) (ConvertFrom-Hex $chunk.nonceHex) (ConvertFrom-Hex $chunk.payloadHex) $chunkAad
if ([Convert]::ToBase64String($chunkEncrypted).TrimEnd('=').Replace('+', '-').Replace('/', '_') -ne $chunk.cipherAndTagB64Url) {
    throw 'File chunk AES-GCM vector mismatch.'
}

Write-Host 'Vault C2 vectors verified independently with .NET AES-GCM/HMAC-SHA256.'
