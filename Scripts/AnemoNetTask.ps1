# Runs elevated (via the Anemo_ElevatedNetworkAction scheduled task) and performs ONE of a
# fixed set of network actions on behalf of the widget. This file lives in an admin-only
# directory (Program Files), so unprivileged code can't alter what the task executes.
#
# The request file, by contrast, sits in the user's profile and must be treated as hostile
# input: every field is validated against a strict allowlist here, and values are only ever
# handed to System32 binaries as arguments (never through cmd.exe or Invoke-Expression).
#
# Exit codes (read back by the widget from the task's Last Result):
#   0 ok, 2 malformed request, 3 unknown/unsafe adapter, 4 invalid address values,
#   5 the underlying command failed
param([Parameter(Mandatory = $true)][string]$RequestFile)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

function ConvertTo-IPv4($text) {
    if ($text -isnot [string]) { return $null }
    $addr = $null
    if (-not [Net.IPAddress]::TryParse($text, [ref]$addr)) { return $null }
    if ($addr.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { return $null }
    # Reject shorthand/odd spellings ("1", "0x7f.1", leading zeros...) - only canonical dotted quads.
    if ($addr.ToString() -ne $text) { return $null }
    return $addr
}

function Get-Prop($obj, [string]$name) {
    $prop = $obj.PSObject.Properties[$name]
    if ($prop) { return $prop.Value }
    return $null
}

function Test-SubnetMask($addr) {
    $bits = ($addr.GetAddressBytes() | ForEach-Object { [Convert]::ToString($_, 2).PadLeft(8, '0') }) -join ''
    return ($bits -match '^1+0*$')
}

function Invoke-System32([string]$exe, [string]$arguments) {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path ([Environment]::GetFolderPath('System')) $exe
    $psi.Arguments = $arguments
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $proc = [Diagnostics.Process]::Start($psi)
    $proc.WaitForExit()
    return $proc.ExitCode
}

try {
    # ---- read + shape-check the request --------------------------------------------------
    try {
        if ((New-Object IO.FileInfo $RequestFile).Length -gt 4096) { exit 2 }
        $req = [IO.File]::ReadAllText($RequestFile) | ConvertFrom-Json
        $action = Get-Prop $req 'action'
        $adapterName = Get-Prop $req 'adapter'
        if ($action -isnot [string] -or $adapterName -isnot [string]) { exit 2 }
    } catch { exit 2 }

    if ($action -ne 'renew' -and $action -ne 'static' -and $action -ne 'dhcp') { exit 2 }

    # ---- adapter: must exactly match a real interface, and be safe to quote ----------------
    # (ipconfig treats * and ? as wildcards, so "release everything" must never be expressible.)
    $adapter = $null
    foreach ($nic in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($nic.Name -ceq $adapterName) { $adapter = $nic.Name; break }
    }
    if (-not $adapter) { exit 3 }
    if ($adapter -match '[\x00-\x1f"\\*?]') { exit 3 }

    # ---- run the one action --------------------------------------------------------------
    switch ($action) {
        'renew' {
            # Release can legitimately fail (static/disconnected adapter); the renew result decides.
            [void](Invoke-System32 'ipconfig.exe' "/release `"$adapter`"")
            if ((Invoke-System32 'ipconfig.exe' "/renew `"$adapter`"") -ne 0) { exit 5 }
        }
        'static' {
            $ip = ConvertTo-IPv4 (Get-Prop $req 'ip')
            $mask = ConvertTo-IPv4 (Get-Prop $req 'mask')
            if (-not $ip -or -not $mask -or -not (Test-SubnetMask $mask)) { exit 4 }
            $first = $ip.GetAddressBytes()[0]
            if ($first -lt 1 -or $first -gt 223 -or $first -eq 127) { exit 4 }

            $cmd = "interface ip set address name=`"$adapter`" static $ip $mask"
            $gwText = Get-Prop $req 'gateway'
            if ($null -ne $gwText) {
                $gw = ConvertTo-IPv4 $gwText
                if (-not $gw -or $gw.GetAddressBytes()[0] -eq 0) { exit 4 }
                $cmd += " $gw 1"
            }
            if ((Invoke-System32 'netsh.exe' $cmd) -ne 0) { exit 5 }
        }
        'dhcp' {
            $rc1 = Invoke-System32 'netsh.exe' "interface ip set address name=`"$adapter`" dhcp"
            $rc2 = Invoke-System32 'netsh.exe' "interface ip set dns name=`"$adapter`" dhcp"
            if ($rc1 -ne 0 -or $rc2 -ne 0) { exit 5 }
        }
    }
    exit 0
} catch {
    exit 5
}
