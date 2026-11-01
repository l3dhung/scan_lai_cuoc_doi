# Builds the Microsoft Store package from windows/LifeScanner.csproj and windows/msix (Windows only, needs the
# .NET 8 SDK and the Windows 10/11 SDK). Output in windows/msix/out:
#   Life-scanner_<version>_x64.msix            unsigned, for uploading to Partner Center (the Store signs it)
#   test/Life-scanner_<version>_x64.msix       with -TestSign: signed with a throwaway certificate, for trying it out
#   test/Life-scanner-test.cer                 that certificate's public part, to trust on the test machine
param([switch]$TestSign)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path "$PSScriptRoot\..\.."
$here = $PSScriptRoot
$out = Join-Path $here 'out'
$pkg = Join-Path $out 'package'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $pkg | Out-Null

# Store versions have four parts and the last one must be 0.
$version = (Get-Content "$root\app.config.json" -Raw | ConvertFrom-Json).version
$parts = @($version.Split('.') + @('0', '0', '0'))[0..2]
$msixVersion = ($parts -join '.') + '.0'

# A normal (not single-file) publish: MSIX installs the files itself, so nothing needs unpacking at start-up.
dotnet publish "$root\windows\LifeScanner.csproj" -c Release -p:Version=$version `
  -p:PublishSingleFile=false -p:EnableCompressionInSingleFile=false -o $pkg --nologo
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

(Get-Content "$here\AppxManifest.xml" -Raw -Encoding UTF8).Replace('__VERSION__', $msixVersion) |
  Set-Content "$pkg\AppxManifest.xml" -Encoding UTF8
Copy-Item "$here\Assets" "$pkg\Assets" -Recurse

$sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\makeappx.exe" |
  Sort-Object FullName -Descending | Select-Object -First 1
if (-not $sdk) { throw 'Windows 10/11 SDK not found (makeappx.exe)' }
$bin = $sdk.DirectoryName

# resources.pri lets Windows pick the right logo size (scale-200, targetsize-16, ...).
$priConfig = Join-Path $out 'priconfig.xml'
& "$bin\makepri.exe" createconfig /cf $priConfig /dq vi-VN /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE) { throw 'makepri createconfig failed' }
& "$bin\makepri.exe" new /pr $pkg /cf $priConfig /mn "$pkg\AppxManifest.xml" /of "$pkg\resources.pri" /o | Out-Null
if ($LASTEXITCODE) { throw 'makepri new failed' }

$name = "Life-scanner_${msixVersion}_x64.msix"
& "$bin\makeappx.exe" pack /d $pkg /p "$out\$name" /o | Out-Null
if ($LASTEXITCODE) { throw 'makeappx pack failed' }
Write-Host "Built $out\$name"

if ($TestSign) {
  # The certificate subject has to match the manifest's Publisher. The private key never leaves this run.
  $publisher = ([xml](Get-Content "$pkg\AppxManifest.xml" -Raw -Encoding UTF8)).Package.Identity.Publisher
  $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
    -FriendlyName 'Life-scanner test signing' -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
  try {
    $test = Join-Path $out 'test'
    New-Item -ItemType Directory $test | Out-Null
    Copy-Item "$out\$name" "$test\$name"
    & "$bin\signtool.exe" sign /fd SHA256 /sha1 $cert.Thumbprint /s My "$test\$name" | Out-Null
    if ($LASTEXITCODE) { throw 'signtool sign failed' }
    Export-Certificate -Cert $cert -FilePath "$test\Life-scanner-test.cer" | Out-Null
    Copy-Item "$here\TEST-INSTALL.txt" $test
    Write-Host "Built $test\$name (test signed)"
  } finally {
    try { Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey }
    catch { Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" }
  }
}
