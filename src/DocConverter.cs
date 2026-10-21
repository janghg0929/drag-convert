// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 janghg0929 — 끌어서 변환 (https://github.com/janghg0929/drag-convert)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ShiftConvert
{
    // 문서 변환. 설치된 Word/Excel/PowerPoint/한글을 화면에 안 띄우고 자동화(COM)로 돌린다.
    // PDF → 이미지는 Windows 내장 PDF 렌더러(Windows.Data.Pdf)를 쓴다.
    // 같은 묶음(여러 파일 한 번에 놓기) 동안은 앱을 재사용하고 끝나면 ReleaseAll()로 닫는다.
    static class Docs
    {
        const string WORD = "Word.Application", EXCEL = "Excel.Application", PPT = "PowerPoint.Application", HWP = "HWPFrame.HwpObject";

        static readonly string[] WordExts = { "docx", "doc", "docm", "dotx", "rtf", "odt", "txt" };
        static readonly string[] ExcelExts = { "xlsx", "xls", "xlsm", "csv", "ods" };
        static readonly string[] PptExts = { "pptx", "ppt", "pptm", "ppsx", "pps", "odp" };
        static readonly string[] HwpExts = { "hwp", "hwpx" };

        static readonly Dictionary<string, bool> hasCache = new Dictionary<string, bool>();
        // 변환 스레드마다 따로 (멈춘 변환이 있어도 다음 변환은 새 Office로 진행되게)
        [ThreadStatic] static object word, excel, ppt, hwp;

        public static string GroupOf(string file)
        {
            string e = Converter.NormExt(file);
            if (e == "pdf") return "pdf";
            if (WordExts.Contains(e)) return "word";
            if (ExcelExts.Contains(e)) return "excel";
            if (PptExts.Contains(e)) return "ppt";
            if (HwpExts.Contains(e)) return "hwp";
            return null;
        }

        static bool Has(string progId)
        {
            bool v;
            if (!hasCache.TryGetValue(progId, out v)) hasCache[progId] = v = Type.GetTypeFromProgID(progId) != null;
            return v;
        }

        public static List<string> TargetsFor(string sample)
        {
            var l = new List<string>();
            switch (GroupOf(sample))
            {
                case "word":
                    // (Word → 한글은 한컴 자동화가 docx를 열지 못해서 제외)
                    if (Has(WORD)) l.AddRange(new[] { "pdf", "docx", "doc", "odt", "rtf", "txt" });
                    break;
                case "pdf":
                    // PDF → Word는 Word가 열 때마다 "PDF 기본 프로그램" 질문을 띄우고, "아니요"면 열기가 취소돼서 뺐다
                    l.Add("png"); l.Add("jpg");
                    break;
                case "excel":
                    if (Has(EXCEL)) l.AddRange(new[] { "pdf", "xlsx", "xls", "csv", "ods" });
                    else if (Converter.NormExt(sample) == "csv") l.Add("xlsx"); // CSV → XLSX는 직접 만들어서 Excel 불필요
                    break;
                case "ppt":
                    if (Has(PPT)) l.AddRange(new[] { "pdf", "pptx", "ppt", "odp", "png", "jpg" });
                    break;
                case "hwp":
                    if (Has(HWP)) l.AddRange(new[] { "pdf", "hwp", "hwpx", "docx", "txt" });
                    break;
            }
            return l;
        }

        public static string MissingAppHint(string sample)
        {
            switch (GroupOf(sample))
            {
                case "word": return "Microsoft Word가 필요해요";
                case "excel": return "Microsoft Excel이 필요해요";
                case "ppt": return "Microsoft PowerPoint가 필요해요";
                case "hwp": return "한컴오피스 한글이 필요해요";
            }
            return "지원하지 않는 형식이에요";
        }

        // dst는 미리 정해 둔 출력 경로. 페이지별 이미지처럼 여러 개가 생기면 첫 번째 경로를 돌려준다.
        // Office는 경로가 길면(약 218자) 파일을 못 열어서, 짧은 임시 폴더에 복사해 변환한 뒤 결과만 옮긴다.
        public static string Convert(string src, string dst, string fmt)
        {
            string grp = GroupOf(src);
            if (grp == null) throw new Exception("지원하지 않는 문서예요");
            string tmp = Path.Combine(Path.GetTempPath(), "DragConvert", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try
            {
                string tsrc = Path.Combine(tmp, Path.GetFileName(src));
                File.Copy(src, tsrc);
                PrepareText(tsrc);
                string tdst = Converter.UniquePath(tsrc, fmt);
                List<string> made = null;
                DialogGuard.Answered = false;
                try
                {
                switch (grp)
                {
                    case "word":
                        WordConvert(tsrc, tdst, fmt);
                        break;
                    case "pdf":
                        made = PdfToImages(tsrc, fmt);
                        break;
                    case "excel":
                        if (Converter.NormExt(tsrc) == "csv")
                        {
                            // CSV는 Excel이 해석하지 않게 직접 "모든 칸 텍스트" xlsx로 만든 뒤, 필요하면 그걸 Excel로 변환
                            if (fmt == "xlsx") CsvXlsx.Convert(tsrc, tdst);
                            else
                            {
                                string tx = Path.Combine(tmp, "csv-as-text.xlsx");
                                CsvXlsx.Convert(tsrc, tx);
                                ExcelConvert(tx, tdst, fmt);
                            }
                        }
                        else ExcelConvert(tsrc, tdst, fmt);
                        break;
                    case "ppt":
                        if (fmt == "png" || fmt == "jpg") made = PptToImages(tsrc, fmt); else PptConvert(tsrc, tdst, fmt);
                        break;
                    case "hwp":
                        HwpConvert(tsrc, tdst, fmt);
                        break;
                }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (DialogGuard.Answered)
                        throw new Exception("Office가 '기본 프로그램으로 설정할까요?' 질문을 띄워서 변환이 취소됐어요");
                    if (IsPasswordError(ex)) throw new Exception("비밀번호가 걸린 문서는 변환할 수 없어요");
                    throw;
                }
                if (made == null) made = new List<string> { tdst };

                Converter.Step(null); // 그 사이 취소됐으면 결과를 옮기지 않고 버림
                if (made.Count == 1) { File.Move(made[0], dst); return dst; }
                List<string> finals = PageOutputs(src, fmt, made.Count);
                for (int i = 0; i < made.Count; i++) File.Move(made[i], finals[i]);
                return finals[0];
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        // 변환 중 강제 종료되면 임시 폴더에 문서 사본이 남는다 → 프로그램 시작 때 1시간 넘은 것 정리
        public static void CleanStaleTemp()
        {
            try
            {
                string root = Path.Combine(Path.GetTempPath(), "DragConvert");
                if (!Directory.Exists(root)) return;
                foreach (string d in Directory.GetDirectories(root))
                    try { if ((DateTime.Now - Directory.GetCreationTime(d)).TotalHours > 1) Directory.Delete(d, true); } catch { }
            }
            catch { }
        }

        public static void ReleaseAll()
        {
            // 사용자가 원래 열어 둔 창이 있으면(문서 수 > 0) 끄지 않는다
            if (word != null) { try { if (Count(Get(word, "Documents")) == 0) Call(word, "Quit", 0); } catch { } Release(word); word = null; }
            if (excel != null) { try { if (Count(Get(excel, "Workbooks")) == 0) Call(excel, "Quit"); } catch { } Release(excel); excel = null; }
            if (ppt != null) { try { if (Count(Get(ppt, "Presentations")) == 0) Call(ppt, "Quit"); } catch { } Release(ppt); ppt = null; }
            if (hwp != null) { try { Call(hwp, "Clear", 1); Call(hwp, "Quit"); } catch { } Release(hwp); hwp = null; }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        // ---- COM 늦은 바인딩 도우미 (dynamic은 Office에서 WeakReference 오류가 나서 InvokeMember 사용) ----

        // InvokeMember는 Office 오류를 TargetInvocationException으로 감싸서 "호출 대상이 예외를…"만 보이게 한다 → 속 오류를 꺼냄
        static object Inv(object o, string name, BindingFlags flags, object[] args, string[] names)
        {
            try { return o.GetType().InvokeMember(name, flags, null, o, args, null, null, names); }
            catch (TargetInvocationException ex)
            {
                if (ex.InnerException != null) ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        static object Call(object o, string name, params object[] args)
        {
            return Inv(o, name, BindingFlags.InvokeMethod, args, null);
        }

        // 이름 붙은 인수: Named(o, "Open", "FileName", path, "ReadOnly", true, ...)  값이 Type.Missing이면 생략
        static object Named(object o, string name, params object[] pairs)
        {
            var names = new List<string>();
            var vals = new List<object>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                if (pairs[i + 1] == Type.Missing) continue;
                names.Add((string)pairs[i]);
                vals.Add(pairs[i + 1]);
            }
            return Inv(o, name, BindingFlags.InvokeMethod, vals.ToArray(), names.ToArray());
        }

        static object Get(object o, string name, params object[] args)
        {
            return Inv(o, name, BindingFlags.GetProperty, args, null);
        }

        static void Set(object o, string name, object value)
        {
            Inv(o, name, BindingFlags.SetProperty, new[] { value }, null);
        }

        static int Count(object collection)
        {
            return System.Convert.ToInt32(Get(collection, "Count"));
        }

        static object Create(string progId)
        {
            Type t = Type.GetTypeFromProgID(progId);
            if (t == null) throw new Exception(progId + " 이 설치돼 있지 않아요");
            string proc = progId == WORD ? "WINWORD" : progId == EXCEL ? "EXCEL" : progId == PPT ? "POWERPNT" : "Hwp";
            var before = new HashSet<int>(Process.GetProcessesByName(proc).Select(p => p.Id));
            object app = Activator.CreateInstance(t);
            foreach (Process p in Process.GetProcessesByName(proc))
            {
                if (!before.Contains(p.Id)) Converter.RegisterPid(p.Id);
                p.Dispose();
            }
            return app;
        }

        // 비밀번호 걸린 문서를 열면 숨은 Office가 비밀번호 창을 띄우고 기다리며 멈춘다.
        // 일부러 틀린 비밀번호를 넘기면 보호된 문서는 바로 실패하고, 보호 안 된 문서는 그냥 열린다.
        const string NoPassword = "__dragconvert_no_password__";

        static bool IsPasswordError(Exception ex)
        {
            string m = ex.Message ?? "";
            return m.Contains("암호") || m.Contains("비밀번호") || m.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); } catch { }
        }

        static bool IsUtf8(string path)
        {
            try
            {
                byte[] b = File.ReadAllBytes(path);
                new UTF8Encoding(false, true).GetString(b);
                return true;
            }
            catch { return false; }
        }

        // txt/csv 사본을 Office가 잘 읽게 손본다:
        //  - txt: 줄바꿈을 CRLF로 (LF만 있으면 Word가 한 줄로 합침)
        static void PrepareText(string path)
        {
            string e = Converter.NormExt(path);
            if (e != "txt") return;
            byte[] b = File.ReadAllBytes(path);
            bool bom = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
            bool utf8 = bom || IsUtf8(path);
            Encoding enc = utf8 ? (Encoding)new UTF8Encoding(true) : Encoding.Default;
            string text = enc.GetString(b, bom ? 3 : 0, b.Length - (bom ? 3 : 0));
            if (e == "txt") text = System.Text.RegularExpressions.Regex.Replace(text, "\r?\n", "\r\n");
            File.WriteAllText(path, text, enc); // UTF8Encoding(true)는 BOM을 씀
        }

        static void CheckOutput(string dst)
        {
            if (!File.Exists(dst)) throw new Exception("결과 파일이 만들어지지 않았어요");
        }

        // ---------------- Word ----------------

        static void WordConvert(string src, string dst, string fmt)
        {
            int code;
            switch (fmt)
            {
                case "pdf": code = 17; break;
                case "docx": code = 16; break;
                case "doc": code = 0; break;
                case "odt": code = 23; break;
                case "rtf": code = 6; break;
                case "txt": code = 7; break; // wdFormatUnicodeText + Encoding 65001 → UTF-8
                default: throw new Exception(fmt + "(으)로는 못 바꿔요");
            }
            if (word == null)
            {
                word = Create(WORD);
                // 자동화로 연 문서는 기본으로 매크로가 켜지고 "보호된 보기"도 없다 → 매크로 강제 차단
                Set(word, "AutomationSecurity", 3); // msoAutomationSecurityForceDisable
                Set(word, "Visible", false);
                Set(word, "DisplayAlerts", 0); // wdAlertsNone — PDF 열 때 "변환합니다" 안내도 생략
            }
            object enc = Type.Missing;
            if (Converter.NormExt(src) == "txt") enc = IsUtf8(src) ? 65001 : Encoding.Default.CodePage; // PrepareText 후라 UTF-8이면 BOM 있음

            object doc = Named(Get(word, "Documents"), "Open", "FileName", src, "ConfirmConversions", false, "ReadOnly", true,
                "AddToRecentFiles", false, "PasswordDocument", NoPassword, "WritePasswordDocument", NoPassword,
                "Visible", false, "Encoding", enc);
            try
            {
                Named(doc, "SaveAs2", "FileName", dst, "FileFormat", code, "Encoding", fmt == "txt" ? (object)65001 : Type.Missing,
                    "AllowSubstitutions", fmt == "txt" ? (object)false : Type.Missing); // txt: — 같은 문자를 -로 바꾸지 않게
            }
            finally
            {
                try { Named(doc, "Close", "SaveChanges", 0); } catch { }
                Release(doc);
            }
            CheckOutput(dst);
        }

        // ---------------- Excel ----------------

        static void ExcelConvert(string src, string dst, string fmt)
        {
            if (excel == null)
            {
                excel = Create(EXCEL);
                Set(excel, "AutomationSecurity", 3); // 매크로 강제 차단
                Set(excel, "EnableEvents", false);   // Workbook_Open 같은 이벤트도 막음
                Set(excel, "Visible", false);
                Set(excel, "DisplayAlerts", false);
                Set(excel, "ScreenUpdating", false);
            }
            // Excel은 이 조합을 이름 붙은 인수로 넘기면 거부해서 위치 인수로 넘긴다
            // (Filename, UpdateLinks, ReadOnly, Format, Password, WriteResPassword, IgnoreReadOnlyRecommended, Origin, Delimiter, Editable, Notify, Converter, AddToMru)
            object M = Type.Missing;
            object wb = Call(Get(excel, "Workbooks"), "Open", src, 0, true, M, NoPassword, M, true, M, M, M, M, M, false);
            try
            {
                switch (fmt)
                {
                    case "pdf": Call(wb, "ExportAsFixedFormat", 0, dst); break;
                    case "xlsx": Named(wb, "SaveAs", "Filename", dst, "FileFormat", 51); break;
                    case "xls": Named(wb, "SaveAs", "Filename", dst, "FileFormat", 56); break;
                    case "ods": Named(wb, "SaveAs", "Filename", dst, "FileFormat", 60); break;
                    case "csv": // 첫 번째(현재) 시트만 저장됨
                        try { Named(wb, "SaveAs", "Filename", dst, "FileFormat", 62); } // xlCSVUTF8 (Excel 2016+)
                        catch { Named(wb, "SaveAs", "Filename", dst, "FileFormat", 6); }
                        break;
                    default: throw new Exception(fmt + "(으)로는 못 바꿔요");
                }
            }
            finally
            {
                try { Call(wb, "Close", false); } catch { }
                Release(wb);
            }
            CheckOutput(dst);
        }

        // ---------------- PowerPoint ----------------

        static object OpenPres(string src)
        {
            if (ppt == null) ppt = Create(PPT); // PowerPoint는 Visible=false를 허용하지 않아서 WithWindow로 숨긴다
            // PowerPoint는 사용자가 켜 둔 창과 같은 프로그램을 쓰므로, 열 때만 매크로를 막고 원래 값으로 돌려놓는다
            object oldSec = null;
            try { oldSec = Get(ppt, "AutomationSecurity"); Set(ppt, "AutomationSecurity", 3); } catch { }
            try
            {
                // PowerPoint는 "파일경로::비밀번호::" 형식으로 열기 비밀번호를 넘긴다
                return Named(Get(ppt, "Presentations"), "Open", "FileName", src + "::" + NoPassword + "::", "ReadOnly", -1, "Untitled", 0, "WithWindow", 0);
            }
            finally
            {
                if (oldSec != null) try { Set(ppt, "AutomationSecurity", oldSec); } catch { }
            }
        }

        static void PptConvert(string src, string dst, string fmt)
        {
            int code;
            switch (fmt)
            {
                case "pdf": code = 32; break;
                case "pptx": code = 24; break;
                case "ppt": code = 1; break;
                case "odp": code = 35; break;
                default: throw new Exception(fmt + "(으)로는 못 바꿔요");
            }
            object pres = OpenPres(src);
            try { Call(pres, "SaveAs", dst, code); }
            finally
            {
                try { Call(pres, "Close"); } catch { }
                Release(pres);
            }
            CheckOutput(dst);
        }

        static List<string> PptToImages(string src, string fmt)
        {
            object pres = OpenPres(src);
            try
            {
                object slides = Get(pres, "Slides");
                int n = Count(slides);
                object setup = Get(pres, "PageSetup");
                double sw = System.Convert.ToDouble(Get(setup, "SlideWidth")), sh = System.Convert.ToDouble(Get(setup, "SlideHeight"));
                int w = 1920, h = (int)Math.Round(1920 * sh / sw);
                List<string> outs = PageOutputs(src, fmt, n);
                for (int i = 0; i < n; i++)
                {
                    Converter.Step("슬라이드 " + (i + 1) + "/" + n);
                    Call(Call(slides, "Item", i + 1), "Export", outs[i], fmt == "png" ? "PNG" : "JPG", w, h);
                }
                CheckOutput(outs[0]);
                return outs;
            }
            finally
            {
                try { Call(pres, "Close"); } catch { }
                Release(pres);
            }
        }

        // ---------------- 한글 ----------------

        // SetMessageBoxMode 값: 확인(0x1) | 확인/취소→취소(0x20) | 예/아니요/취소→취소(0x4000) | 예/아니요→아니요(0x20000)
        const int HwpDeny = 0x1 | 0x20 | 0x4000 | 0x20000;
        // 확인(0x1) | 확인/취소→확인(0x10) | 예/아니요/취소→예(0x1000) | 예/아니요→예(0x10000)
        const int HwpSaveYes = 0x1 | 0x10 | 0x1000 | 0x10000;

        static void HwpConvert(string src, string dst, string fmt)
        {
            string f;
            switch (fmt)
            {
                case "pdf": f = "PDF"; break;
                case "hwp": f = "HWP"; break;
                case "hwpx": f = "HWPX"; break;
                case "docx": f = "OOXML"; break;
                case "txt": f = "UNICODE"; break;
                default: throw new Exception(fmt + "(으)로는 못 바꿔요");
            }
            if (hwp == null)
            {
                hwp = Create(HWP);
                // 보안 모듈이 등록돼 있으면 "파일 접근 허용" 확인창이 안 뜬다 (없으면 확인창이 뜰 수 있음)
                try { Call(hwp, "RegisterModule", "FilePathCheckDLL", "FilePathCheckerModule"); } catch { }
                try { Set(Call(Get(hwp, "XHwpWindows"), "Item", 0), "Visible", false); } catch { }
            }
            // 메시지 상자 자동 응답. 파일을 열 때는 안전한 쪽(아니요·취소)으로 답해서
            // 문서 속 스크립트 실행 같은 질문이 허용되지 않게 한다
            try { Call(hwp, "SetMessageBoxMode", HwpDeny); } catch { }
            bool opened = System.Convert.ToBoolean(Call(hwp, "Open", src, "", "forceopen:true;versionwarning:false;suspendpassword:true"));
            if (!opened) throw new Exception("한글에서 파일을 열지 못했어요");
            Converter.Step(null);
            try
            {
                // 저장할 때만 "다른 형식으로 저장하면 서식이 바뀔 수 있습니다. 계속할까요?"에 예
                try { Call(hwp, "SetMessageBoxMode", HwpSaveYes); } catch { }
                Call(hwp, "SaveAs", dst, f, "");
            }
            finally
            {
                try { Call(hwp, "SetMessageBoxMode", HwpDeny); } catch { }
                try { Call(hwp, "Clear", 1); } catch { }
            }
            CheckOutput(dst);
        }

        // ---------------- PDF → 이미지 ----------------

        static List<string> PageOutputs(string src, string fmt, int n)
        {
            var outs = new List<string>();
            if (n <= 1) { outs.Add(Converter.UniquePath(src, fmt)); return outs; }
            string dir = Path.GetDirectoryName(src), name = Path.GetFileNameWithoutExtension(src);
            for (int i = 1; i <= n; i++)
            {
                string p = Path.Combine(dir, name + "-" + i + "." + fmt);
                for (int k = 1; File.Exists(p); k++) p = Path.Combine(dir, name + "-" + i + " (" + k + ")." + fmt);
                outs.Add(p);
            }
            return outs;
        }

        // Windows SDK 없이 빌드하려고 WinRT 비동기 작업을 직접 기다린다
        static void WaitDone(IAsyncInfo op)
        {
            while (op.Status == AsyncStatus.Started) System.Threading.Thread.Sleep(5);
            if (op.Status == AsyncStatus.Error) throw new Exception("PDF 처리 오류: " + op.ErrorCode.Message);
            if (op.Status != AsyncStatus.Completed) throw new Exception("PDF 처리가 취소됐어요");
        }

        static T Wait<T>(IAsyncOperation<T> op)
        {
            WaitDone(op);
            return op.GetResults();
        }

        static List<string> PdfToImages(string src, string fmt)
        {
            StorageFile file = Wait(StorageFile.GetFileFromPathAsync(src));
            PdfDocument doc = Wait(PdfDocument.LoadFromFileAsync(file));
            int n = (int)doc.PageCount;
            if (n == 0) throw new Exception("페이지가 없는 PDF예요");
            List<string> outs = PageOutputs(src, fmt, n);
            for (int i = 0; i < n; i++)
            {
                Converter.Step("페이지 " + (i + 1) + "/" + n);
                using (PdfPage page = doc.GetPage((uint)i))
                using (var ras = new InMemoryRandomAccessStream())
                {
                    // 2배 크기(약 192dpi)로 렌더링 — 글자가 선명하게
                    var opts = new PdfPageRenderOptions();
                    opts.DestinationWidth = (uint)Math.Round(page.Size.Width * 2);
                    opts.DestinationHeight = (uint)Math.Round(page.Size.Height * 2);
                    WaitDone(page.RenderToStreamAsync(ras, opts));

                    var bytes = new byte[ras.Size];
                    using (var reader = new DataReader(ras.GetInputStreamAt(0)))
                    {
                        Wait(reader.LoadAsync((uint)ras.Size));
                        reader.ReadBytes(bytes);
                    }
                    var s = new MemoryStream(bytes);
                    var dec = BitmapDecoder.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    BitmapSource img = dec.Frames[0];
                    BitmapEncoder enc;
                    if (fmt == "jpg")
                    {
                        var j = new JpegBitmapEncoder();
                        j.QualityLevel = 92;
                        enc = j;
                        img = Converter.FlattenOnWhite(img);
                    }
                    else enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(img));
                    using (var fs = File.Create(outs[i])) enc.Save(fs);
                }
            }
            return outs;
        }
    }

    // Office가 막 켜지는 중이라 호출을 거절(RPC_E_CALL_REJECTED)하면 잠깐 기다렸다 재시도하게 하는 COM 메시지 필터
    [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IOleMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
        [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
        [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
    }

    class ComRetryFilter : IOleMessageFilter
    {
        [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);

        // 호출한 STA 스레드에만 적용된다
        public static void Register() { IOleMessageFilter old; CoRegisterMessageFilter(new ComRetryFilter(), out old); }
        public static void Revoke() { IOleMessageFilter old; CoRegisterMessageFilter(null, out old); }

        public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) { return 0; }
        public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
        {
            // SERVERCALL_RETRYLATER(2)면 0.1초 뒤 재시도, 최대 60초
            return (dwRejectType == 2 && dwTickCount < 60000) ? 100 : -1;
        }
        public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) { return 2; }
    }

    // 자동화로 띄운 Office가 "Word가 기본 프로그램이 아닙니다. 설정할까요?" 같은 질문을 띄우면
    // 숨겨진 상태로 대답을 기다리며 멈춘다. 변환하는 동안 그 질문에만 "아니요"를 눌러 준다 (설정은 안 바뀜).
    static class DialogGuard
    {
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

        static readonly string[] Procs = { "WINWORD", "EXCEL", "POWERPNT", "Hwp" };
        static double lastCpu = -1;
        static System.Threading.Timer timer;
        public static volatile bool Answered;
        static DateTime since;

        static int refs;
        static readonly object gate = new object();

        // 변환 묶음이 겹칠 수 있어서(멈춘 변환 + 새 변환) 참조 횟수로 관리
        public static void Start()
        {
            lock (gate)
            {
                if (refs++ > 0) return;
                since = DateTime.Now.AddSeconds(-2);
                timer = new System.Threading.Timer(Tick, null, 300, 300);
            }
        }

        public static void Stop()
        {
            lock (gate)
            {
                if (--refs > 0) return;
                refs = 0;
                if (timer != null) { timer.Dispose(); timer = null; }
            }
        }

        static string Cls(IntPtr h) { var b = new StringBuilder(256); GetClassName(h, b, 256); return b.ToString(); }
        static string Txt(IntPtr h) { var b = new StringBuilder(2048); GetWindowText(h, b, 2048); return b.ToString(); }

        static void Tick(object _)
        {
            try
            {
                // 이번 변환 중에 새로 뜬 Office 프로세스만 대상 (사용자가 원래 켜 둔 창은 건드리지 않음)
                var pids = new HashSet<int>();
                double cpu = 0;
                foreach (string name in Procs)
                    foreach (Process p in Process.GetProcessesByName(name))
                    {
                        try { if (p.StartTime >= since && name != "Hwp") pids.Add(p.Id); } catch { }
                        try { cpu += p.TotalProcessorTime.TotalMilliseconds; } catch { }
                        p.Dispose();
                    }
                // Office/한글이 CPU를 쓰고 있으면 변환이 진행 중이라고 본다 (오래 걸리는 PDF 저장 등이 "멈춤"으로 오판되지 않게)
                if (lastCpu >= 0 && cpu - lastCpu > 30) Converter.Touch();
                lastCpu = cpu;
                if (pids.Count == 0) return;
                EnumWindows((h, l) =>
                {
                    int pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pids.Contains(pid) && IsWindowVisible(h) && Cls(h) == "#32770") { Log("dialog pid=" + pid); AnswerNo(h); }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex) { Log("ERR " + ex); }
        }

        static void Log(string m)
        {
            if (Environment.GetEnvironmentVariable("DRAGCONVERT_DEBUG") == null) return;
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "dragconvert-guard.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + m + Environment.NewLine); } catch { }
        }

        static void AnswerNo(IntPtr dlg)
        {
            var text = new StringBuilder();
            IntPtr no = IntPtr.Zero;
            EnumChildWindows(dlg, (c, l) =>
            {
                string cls = Cls(c), t = Txt(c);
                if (cls == "Static") text.Append(t);
                if (cls == "Button" && (t.StartsWith("아니요") || t.Replace("&", "") == "No")) no = c;
                return true;
            }, IntPtr.Zero);
            string msg = text.ToString();
            if (no == IntPtr.Zero || !(msg.Contains("기본 프로그램") || msg.Contains("default program"))) return;
            // 이 질문엔 "아니요" = 열기 취소. 멈춰 있는 것보다 빨리 실패하는 게 낫다
            try
            {
                var el = System.Windows.Automation.AutomationElement.FromHandle(no);
                ((System.Windows.Automation.InvokePattern)el.GetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern)).Invoke();
                Answered = true;
                Log("answered no: " + msg);
            }
            catch (Exception ex) { Log("invoke failed " + ex.Message); }
        }
    }
}
