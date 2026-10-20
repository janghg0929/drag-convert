# 끌어서 변환

탐색기에서 파일을 끌다가 **Shift**(또는 스페이스바·Ctrl·Alt 중 고른 키)를 누르면 커서 주위에 원형 메뉴가 뜨고, 원하는 형식 칸에 놓으면 같은 폴더에 변환된 파일이 생기는 Windows 프로그램입니다. 원본은 그대로 남습니다.

## 받는 법

[Releases](https://github.com/janghg0929/drag-convert/releases/latest)에서 `DragConvert.exe`를 받아 실행하세요.

- 처음 실행할 때 "Windows의 PC 보호" 창이 뜨면 **추가 정보 → 실행**을 누르세요. 서명 인증서가 없는 개인 프로그램이라 뜨는 경고예요.
- 실행하면 설정 창이 뜹니다. 여기서 메뉴를 띄우는 키, 자동 실행, 업데이트, 기록(로그)을 확인할 수 있어요.
- 창을 닫아도 작업 표시줄 오른쪽 트레이에서 계속 실행돼요. 완전히 끄려면 창의 **종료**를 누르세요.
- 관리자 권한으로 실행하지 마세요. 끌어 놓기가 막힙니다.

## 되는 변환

| 끌어온 파일 | 바꿀 수 있는 형식 |
|---|---|
| 이미지 (png, jpg, bmp, gif, tiff, ico, webp…) | PNG · JPG · PDF(여러 장은 한 파일로) · BMP · GIF · TIFF · ICO |
| Word (docx, doc, rtf, odt, txt) | PDF · DOCX · DOC · ODT · RTF · TXT — Word 필요 |
| Excel (xlsx, xls, ods) | PDF · XLSX · XLS · CSV · ODS — Excel 필요 |
| CSV | XLSX(모든 칸을 텍스트로, Excel 없어도 됨) · PDF · XLS · ODS |
| PowerPoint (pptx, ppt, odp) | PDF · PPTX · PPT · ODP · 슬라이드별 PNG/JPG — PowerPoint 필요 |
| 한글 (hwp, hwpx) | PDF · HWP↔HWPX · DOCX · TXT — 한컴오피스 필요 |
| PDF | 페이지별 PNG / JPG |
| 오디오·영상, WebP 만들기 | ffmpeg가 설치돼 있을 때만 |

## 알아둘 점

- 원 **바깥**에 놓으면 Windows 기본 동작이 실행돼요 (Shift = 이동, Ctrl = 복사, Alt = 바로가기). 스페이스바는 겹치는 기능이 없어요.
- 변환이 끝난 뒤 완료 표시를 누르면 새 파일이 있는 폴더가 열려요.
- 비밀번호가 걸린 문서는 변환하지 않아요.
- 변환 중에 원의 가운데를 누르면 취소됩니다.
- Office 문서는 변환할 때 매크로가 강제로 꺼진 상태로 열립니다.
- 업데이트는 하루 한 번 이 저장소의 최신 릴리스를 확인하고, 개발자 서명이 맞을 때만 물어본 뒤 설치합니다.

## 개발

Windows에 기본으로 들어 있는 .NET Framework 컴파일러로 빌드합니다. 따로 설치할 것은 없습니다.

```
build.bat
```

새 버전 내기: `src\Version.cs`의 `Current`를 올린 뒤 실행합니다. 서명 비밀키가 필요합니다.

```
pwsh .\release.ps1 -Notes "바뀐 점"
```
