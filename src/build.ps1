$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$out = Join-Path (Split-Path $src) 'LicznikOdwiedzin.exe'
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$refs = 'System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Security.dll' | % { "/r:$fw\$_" }
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 "/out:$out" "/win32manifest:$src\app.manifest" $refs "$src\LicznikOdwiedzin.cs"
if ($LASTEXITCODE -ne 0) { throw "Kompilacja nieudana ($LASTEXITCODE)" }
"Zbudowano: $out ($([math]::Round((Get-Item $out).Length/1KB)) KB)"
