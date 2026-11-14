Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Wp17CpuSets {
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool GetSystemCpuSetInformation(byte[] buffer, uint length, out uint required, IntPtr process, uint flags);
}
"@

[uint32]$required = 0
[void][Wp17CpuSets]::GetSystemCpuSetInformation($null, 0, [ref]$required, [IntPtr]::Zero, 0)
$bytes = [byte[]]::new($required)
if (![Wp17CpuSets]::GetSystemCpuSetInformation($bytes, $required, [ref]$required, [IntPtr]::Zero, 0)) {
    throw 'CPU set topology unavailable.'
}
for ($offset = 0; $offset -lt $required;) {
    $length = [BitConverter]::ToInt32($bytes, $offset)
    if ($length -lt 8 -or $offset + $length -gt $required) { throw 'Invalid CPU set topology.' }
    if ([BitConverter]::ToInt32($bytes, $offset + 4) -eq 0 -and $length -ge 20) {
        [pscustomobject]@{
            Group = [BitConverter]::ToUInt16($bytes, $offset + 12)
            Processor = $bytes[$offset + 14]
            Core = $bytes[$offset + 15]
            EfficiencyClass = $bytes[$offset + 18]
            Parked = ($bytes[$offset + 19] -band 1) -ne 0
        }
    }
    $offset += $length
}
