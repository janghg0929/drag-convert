# 끌어서 변환

탐색기에서 파일을 끌다가 **Shift**(또는 직접 정한 키나 마우스 가운데·옆 버튼)를 누르면 커서 주위에 원형 메뉴가 뜨고, 원하는 형식 칸에 놓으면 같은 폴더에 변환된 파일이 생기는 Windows 프로그램입니다. 원본은 그대로 남습니다.

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
| 움직이는 GIF | 위 이미지 형식 + **MP4 영상**(ffmpeg 없이 Windows 인코더로) · WebM(ffmpeg 있을 때) |
| Word (docx, doc, rtf, odt, txt) | PDF · DOCX · DOC · ODT · RTF · TXT — Word 필요 |
| Excel (xlsx, xls, ods) | PDF · XLSX · XLS · CSV · ODS — Excel 필요 |
| CSV | XLSX(모든 칸을 텍스트로, Excel 없어도 됨) · PDF · XLS · ODS |
| PowerPoint (pptx, ppt, odp) | PDF · PPTX · PPT · ODP · 슬라이드별 PNG/JPG — PowerPoint 필요 |
| 한글 (hwp, hwpx) | PDF · HWP↔HWPX · DOCX · TXT — 한컴오피스 필요 |
| PDF | 페이지별 PNG / JPG |
| 문서·표·슬라이드 → **MD(마크다운)** | docx·csv는 Office 없이, doc·rtf·odt는 Word, hwp·hwpx는 한글, xlsx·xls·ods는 Excel, pptx·ppt·odp는 PowerPoint 필요. 제목·굵게·기울임·목록·표·링크·그림을 옮기고, 그림은  폴더에 저장 |
| 오디오·영상, WebP 만들기 | ffmpeg가 설치돼 있을 때만 |

## 알아둘 점

- 메뉴 키는 설정 창의 **키 변경**을 누르고 원하는 키를 누르면 바뀌어요 (Esc·Delete·Enter·Windows 키는 제외).
- 파일을 끄는 동안에는 그 키가 탐색기에 전달되지 않아요. 그래서 Shift를 눌러도 원 바깥에 놓을 때 파일이 강제로 이동되지 않아요.
- 변환이 끝난 뒤 완료 표시를 누르면 새 파일이 있는 폴더가 열려요.
- 비밀번호가 걸린 문서는 변환하지 않아요.
- 종류가 다른 파일을 한꺼번에 끌어도 돼요. 원에는 하나라도 바꿀 수 있는 형식이 모두 나오고, 일부만 되는 칸엔 "2/3개"처럼 표시돼요. 그 형식으로 못 바꾸는 파일은 건너뛰어요.
- 변환 중에 원의 가운데를 누르면 취소됩니다.
- Office 문서는 변환할 때 매크로가 강제로 꺼진 상태로 열립니다.
- 업데이트는 하루 한 번 이 저장소의 최신 릴리스를 확인하고, 개발자 서명이 맞을 때만 물어본 뒤 설치합니다.

## 라이선스

[GNU GPL v3](LICENSE) 이상(GPL-3.0-or-later)으로 배포합니다. 누구나 자유롭게 쓰고, 고치고, 다시 배포할 수 있어요. 다만 고친 버전을 배포할 때는 그 소스도 같은 GPL 조건으로 공개해야 해요.

Copyright (C) 2026 janghg0929
