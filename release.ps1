param(
    [string]$Notes = "",      # 친구들에게 보일 "바뀐 점"
    [switch]$DryRun           # 업로드 없이 dist\ 에 파일만 만들어 보기
)
# 새 버전 내기
#   1) src\Version.cs 의 Current 숫자를 올린다 (예: 1.0.0 → 1.0.1)
#   2) pwsh .\release.ps1 -Notes "바뀐 점"
# 빌드 → 서명 → 앱으로 서명 검증 → git push → GitHub 릴리스 업로드
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$ver = [regex]::Match((Get-Content src\Version.cs -Raw), 'Current = "([\d.]+)"').Groups[1].Value
if (-not $ver) { throw "src\Version.cs 에서 버전을 못 찾았어요" }
"version: $ver"

if (-not $DryRun) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "GitHub CLI(gh)가 없어요" }
    gh auth status *> $null; if ($LASTEXITCODE) { throw "GitHub에 로그인돼 있지 않아요: gh auth login" }
    gh release view "v$ver" *> $null
    if ($LASTEXITCODE -eq 0) { throw "v$ver 릴리스가 이미 있어요. src\Version.cs 의 버전을 올려 주세요" }
    if (git status --porcelain) { throw "커밋 안 된 변경이 있어요. 먼저 커밋해 주세요" }
}

& cmd.exe /c "`"$PSScriptRoot\build.bat`""
if ($LASTEXITCODE) { throw "빌드 실패" }
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'ShiftConvert.exe') "$dist\ShiftConvert.exe" -Force
& "$PSScriptRoot\sign.ps1" -Exe "$dist\ShiftConvert.exe" -Version $ver

# 실제 앱의 검증 코드로 한 번 더 확인 (서명 규칙이 어긋나면 친구들 업데이트가 전부 거부되므로)
$out = Join-Path $env:TEMP "shiftconvert-verify.txt"
$p = Start-Process "$dist\ShiftConvert.exe" -ArgumentList '--verify-update', "`"$dist\ShiftConvert.exe`"", "`"$dist\ShiftConvert.exe.sig`"", $ver `
    -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out
$check = Get-Content $out -Raw; Remove-Item $out
if ($p.ExitCode -ne 0) { throw "앱이 서명을 거부했어요: $check" }
"app verification: $($check.Trim())"

if ($DryRun) { "dry run 끝: dist\ 에 v$ver 파일이 준비됐어요 (업로드 안 함)"; return }

git push
if ($LASTEXITCODE) { throw "git push 실패" }
gh release create "v$ver" "$dist\ShiftConvert.exe" "$dist\ShiftConvert.exe.sig" --title "v$ver" --notes $Notes
if ($LASTEXITCODE) { throw "릴리스 업로드 실패" }
"v$ver 릴리스 완료 — 친구들 프로그램이 하루 안에 업데이트를 알려줘요"
