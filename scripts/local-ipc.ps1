# Shared by development tools. Production uses the Windows applications' DPAPI client.
Add-Type -AssemblyName System.Security

function Protect-CursivisProfileValue {
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value) -or $Value.StartsWith('dpapi:v1:')) {
        return $Value
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    try {
        $protected = [Security.Cryptography.ProtectedData]::Protect(
            $bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        return 'dpapi:v1:' + [Convert]::ToBase64String($protected)
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Get-CursivisServiceKey {
    param([ValidateSet('trigger', 'haptics', 'backend', 'browser-agent', 'browser-bridge', 'browser-relay')][string]$Scope)
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $mutex = [Threading.Mutex]::new($false, "Global\Cursivis.LocalIpc.v1.$sid")
    $held = $false
    try {
        try {
            $held = $mutex.WaitOne([TimeSpan]::FromSeconds(3))
        }
        catch [Threading.AbandonedMutexException] {
            $held = $true
        }
        if (-not $held) {
            throw 'Could not open the Cursivis local credential.'
        }

        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Cursivis\LocalIpc')
        $secret = $null
        try {
            $saved = $key.GetValue('CredentialV1')
            if ($saved -is [byte[]] -and $saved.Length -le 4096) {
                try {
                    $secret = [Security.Cryptography.ProtectedData]::Unprotect(
                        $saved, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
                }
                catch [Security.Cryptography.CryptographicException] {}
            }
            if ($null -eq $secret -or $secret.Length -ne 32) {
                $secret = New-Object byte[] 32
                $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
                try { $rng.GetBytes($secret) } finally { $rng.Dispose() }
                $protected = [Security.Cryptography.ProtectedData]::Protect(
                    $secret, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
                $key.SetValue('CredentialV1', $protected, [Microsoft.Win32.RegistryValueKind]::Binary)
            }
            return ,(Get-CursivisHmac $secret "Cursivis.LocalIpc/v1/$Scope")
        }
        finally {
            if ($null -ne $secret) {
                [Array]::Clear($secret, 0, $secret.Length)
            }
            $key.Dispose()
        }
    }
    finally {
        if ($held) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

function Get-CursivisHmac {
    param([byte[]]$Key, [string]$Value)
    $hmac = [Security.Cryptography.HMACSHA256]::new($Key)
    try { return ,($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))) }
    finally { $hmac.Dispose() }
}

function ConvertTo-CursivisHex {
    param([byte[]]$Value)
    return [BitConverter]::ToString($Value).Replace('-', '').ToLowerInvariant()
}

function New-CursivisIpcProof {
    param([Uri]$Uri, [string]$Method, [byte[]]$Key)
    if ($Uri.Host -ne '127.0.0.1' -or $Uri.Scheme -notin @('http','ws') -or $Uri.UserInfo) {
        throw 'The Cursivis connection must use the numeric loopback address.'
    }
    $seconds = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $nonce = [Guid]::NewGuid().ToString('N')
    $mac = Get-CursivisHmac $Key (@('request', $Method, $Uri.Authority, $Uri.PathAndQuery, $seconds, $nonce) -join [char]10)
    return "v1.$seconds.$nonce.$(ConvertTo-CursivisHex $mac)"
}

function Get-CursivisIpcAck {
    param([byte[]]$Key, [string]$Proof)
    return ConvertTo-CursivisHex (Get-CursivisHmac $Key ("response" + [char]10 + $Proof))
}

function Invoke-CursivisDiagnosticRequest {
    param([Uri]$Uri, [string]$Scope, [string]$Method = 'GET', [string]$Body, [int]$TimeoutSec = 4)
    $key = Get-CursivisServiceKey -Scope $Scope
    try {
        $proof = New-CursivisIpcProof -Uri $Uri -Method $Method -Key $key
        $request = @{
            Uri = $Uri
            Method = $Method
            Headers = @{ 'X-Cursivis-IPC' = $proof }
            TimeoutSec = $TimeoutSec
            MaximumRedirection = 0
            UseBasicParsing = $true
        }
        if ($PSVersionTable.PSVersion.Major -ge 7) { $request.NoProxy = $true }
        if ($Body) {
            $request.Body = $Body
            $request.ContentType = 'application/json'
        }
        $response = Invoke-WebRequest @request
        if ([string]($response.Headers['X-Cursivis-IPC-Ack'] | Select-Object -First 1) -cne
            (Get-CursivisIpcAck $key $proof)) {
            throw 'Could not verify the Cursivis local service.'
        }
        return $response
    }
    finally {
        [Array]::Clear($key, 0, $key.Length)
    }
}
