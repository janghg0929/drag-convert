param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Version)
# 업데이트 파일 서명: <exe>.sig 를 만든다 (1행 버전, 2행 base64 서명)
# 서명 대상 = "DragConvert-update|버전|exe의 SHA-256"  — 앱의 Updater.Verify와 같은 규칙
$ErrorActionPreference = 'Stop'
# 사용자 폴더 바로 아래 (OneDrive 동기화 X, 패키지 앱의 AppData 숨김 이동 X). 다른 곳에 두려면 DRAGCONVERT_SIGNING_KEY로 지정
$key = if ($env:DRAGCONVERT_SIGNING_KEY) { $env:DRAGCONVERT_SIGNING_KEY } else { Join-Path $env:USERPROFILE '.dragconvert-signing\private-key.xml' }
if (-not (Test-Path $key)) { throw "서명 비밀키가 없어요: $key (백업해 둔 키를 이 위치에 복사하세요)" }

$hash = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
$payload = [Text.Encoding]::UTF8.GetBytes("DragConvert-update|$Version|$hash")
$rsa = [Security.Cryptography.RSA]::Create()
$rsa.FromXmlString([IO.File]::ReadAllText($key))
$sig = $rsa.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
[IO.File]::WriteAllText((Resolve-Path $Exe).Path + '.sig', "$Version`n" + [Convert]::ToBase64String($sig))
"signed $Exe as $Version"
