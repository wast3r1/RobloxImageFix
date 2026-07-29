$root = Split-Path -Parent $PSCommandPath
$setupRoot = Join-Path $root "Setup"
$dist = Join-Path $root "dist"
$tmpPublish = Join-Path $root "dist-tmp"

Remove-Item $dist -Recurse -ErrorAction SilentlyContinue
Remove-Item $tmpPublish -Recurse -ErrorAction SilentlyContinue
$null = New-Item -ItemType Directory -Path $dist -Force
$null = New-Item -ItemType Directory -Path (Join-Path $dist "App") -Force

Write-Host "=== Building main app (framework-dependent) ==="
dotnet publish $root\RobloxImageFix.csproj -c Release -o (Join-Path $dist "App") -nologo
if ($LASTEXITCODE -ne 0) { throw "Main build failed" }
Copy-Item "$root\app.ico" (Join-Path $dist "App\app.ico") -ErrorAction SilentlyContinue

Write-Host "=== Building setup (self-contained single-file) ==="
dotnet publish $setupRoot\Setup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $tmpPublish -nologo
if ($LASTEXITCODE -ne 0) { throw "Setup build failed" }

# Copy only the setup exe + any needed native DLLs to dist root
Copy-Item (Join-Path $tmpPublish "RobloxImageFix-Setup.exe") $dist
Copy-Item (Join-Path $tmpPublish "*.dll") $dist -ErrorAction SilentlyContinue

Remove-Item $tmpPublish -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "=== Done ==="
Write-Host "Distribution: $dist"
Get-ChildItem $dist -Recurse | Select-Object Mode, Length, Name | Format-Table -AutoSize
