using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShiftConvert
{
    enum Kind { Unknown, Image, Audio, Video, Document }

    // 실제 변환을 담당. 이미지는 Windows 내장 코덱(WIC)으로, 오디오/영상/WebP 인코딩은 ffmpeg가 있으면 ffmpeg로.
    // WPF 이미징을 쓰므로 반드시 STA 스레드에서 호출할 것.
    static class Converter
    {
        static readonly string[] ImageExts = { "png", "jpg", "bmp", "gif", "tiff", "ico", "webp", "heic", "heif", "avif", "jxr", "wdp", "dds" };
        static readonly string[] AudioExts = { "mp3", "wav", "flac", "ogg", "m4a", "aac", "wma", "opus", "aiff" };
        static readonly string[] VideoExts = { "mp4", "mkv", "mov", "avi", "webm", "wmv", "flv", "m4v", "mpg", "mpeg", "ts", "3gp" };

        static string ffmpegPath;

        public static string FfmpegPath
        {
            get
            {
                if (ffmpegPath == null || !File.Exists(ffmpegPath)) ffmpegPath = FindFfmpeg();
                return ffmpegPath;
            }
        }

        static string FindFfmpeg()
        {
            // exe 옆 폴더는 찾지 않는다: 다운로드 폴더 등에 있는 정체불명의 ffmpeg.exe를 실행하지 않게
            var dirs = new List<string>();
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string d in path.Split(';'))
            {
                string t = d.Trim().Trim('"');
                if (t.Length > 0 && Path.IsPathRooted(t)) dirs.Add(t); // "." 같은 상대 경로 제외
            }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dirs.Add(Path.Combine(local, @"Microsoft\WinGet\Links"));
            dirs.Add(Path.Combine(home, @"scoop\shims"));
            dirs.Add(@"C:\ProgramData\chocolatey\bin");
            dirs.Add(@"C:\ffmpeg\bin");
            foreach (string d in dirs)
            {
                if (string.IsNullOrWhiteSpace(d)) continue;
                try
                {
                    string p = Path.Combine(d.Trim().Trim('"'), "ffmpeg.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            // winget이 Links를 안 만든 경우 패키지 폴더를 직접 뒤짐
            try
            {
                string pk = Path.Combine(local, @"Microsoft\WinGet\Packages");
                if (Directory.Exists(pk))
                    foreach (string d in Directory.GetDirectories(pk, "*FFmpeg*"))
                    {
                        string hit = Directory.GetFiles(d, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                        if (hit != null) return hit;
                    }
            }
            catch { }
            return null;
        }

        public static string NormExt(string file)
        {
            string e = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
            if (e == "jpeg" || e == "jfif" || e == "jpe") e = "jpg";
            if (e == "tif") e = "tiff";
            if (e == "aif") e = "aiff";
            return e;
        }

        public static Kind KindOf(string file)
        {
            string e = NormExt(file);
            if (ImageExts.Contains(e)) return Kind.Image;
            if (AudioExts.Contains(e)) return Kind.Audio;
            if (VideoExts.Contains(e)) return Kind.Video;
            if (Docs.GroupOf(file) != null) return Kind.Document;
            return Kind.Unknown;
        }

        public static List<string> TargetsFor(Kind k, string sample)
        {
            if (k == Kind.Document) return Docs.TargetsFor(sample);
            bool ff = FfmpegPath != null;
            switch (k)
            {
                case Kind.Image:
                    var l = new List<string> { "png", "jpg", "pdf", "webp", "bmp", "gif", "tiff", "ico" };
                    if (!ff) l.Remove("webp");
                    // 움직이는 GIF는 사실상 짧은 영상 → 영상으로도 (MP4는 ffmpeg 없이 Windows 인코더로)
                    if (sample != null && NormExt(sample) == "gif" && GifVideo.IsAnimated(sample))
                    {
                        if (GifVideo.Available || ff) l.Add("mp4");
                        if (ff) l.Add("webm");
                    }
                    return l;
                case Kind.Audio:
                    return ff ? new List<string> { "mp3", "wav", "flac", "m4a", "ogg" } : new List<string>();
                case Kind.Video:
                    return ff ? new List<string> { "mp4", "mkv", "webm", "gif", "mp3", "wav" } : new List<string>();
            }
            return new List<string>();
        }

        // 이 파일 하나를 바꿀 수 있는 형식 (같은 형식으로 바꾸기는 제외)
        public static List<string> TargetsForFile(string file)
        {
            var l = TargetsFor(KindOf(file), file);
            l.Remove(NormExt(file));
            return l;
        }

        // 여러 파일을 한 번에 놓을 때: 어느 파일이든 하나라도 바꿀 수 있는 형식 전부.
        // 모든 파일에 되는 형식을 앞에, 일부만 되는 형식을 뒤에. support[형식] = 그 형식으로 바꿀 수 있는 파일 수
        public static List<string> TargetsForAll(List<string> files, out Dictionary<string, int> support, out Dictionary<string, string> firstFile)
        {
            support = new Dictionary<string, int>();
            firstFile = new Dictionary<string, string>();
            var order = new List<string>();
            var cache = new Dictionary<string, List<string>>();
            foreach (string f in files)
            {
                string ext = NormExt(f);
                List<string> t;
                // 같은 확장자는 한 번만 계산 (GIF는 움직이는지에 따라 달라서 매번)
                if (ext == "gif" || !cache.TryGetValue(ext, out t)) { t = TargetsForFile(f); if (ext != "gif") cache[ext] = t; }
                foreach (string fmt in t)
                {
                    if (!support.ContainsKey(fmt)) { support[fmt] = 0; order.Add(fmt); firstFile[fmt] = f; }
                    support[fmt]++;
                }
            }
            var sup = support;
            return order.Select((fmt, i) => new { fmt, i }).OrderByDescending(x => sup[x.fmt] == files.Count).ThenBy(x => x.i).Select(x => x.fmt).ToList();
        }

        // 원본은 그대로 두고 같은 폴더에 새 파일을 만든다. 이름이 겹치면 "이름 (1).ext"
        public static string UniquePath(string src, string fmt)
        {
            string dir = Path.GetDirectoryName(src);
            string name = Path.GetFileNameWithoutExtension(src);
            string p = Path.Combine(dir, name + "." + fmt);
            for (int i = 1; File.Exists(p); i++) p = Path.Combine(dir, name + " (" + i + ")." + fmt);
            return p;
        }

        public static string Convert(string src, string fmt)
        {
            string dst = UniquePath(src, fmt);
            Kind k = KindOf(src);
            try
            {
                if (k == Kind.Image && (fmt == "mp4" || fmt == "webm")) GifToVideo(src, dst, fmt);
                else if (k == Kind.Image && fmt == "pdf") ImagesToPdf(new List<string> { src }, dst);
                else if (k == Kind.Image) ConvertImage(src, dst, fmt);
                else if (k == Kind.Document) return Docs.Convert(src, dst, fmt);
                else if (k == Kind.Audio || k == Kind.Video) RunFfmpeg(src, dst, FfArgs(k, fmt));
                else throw new Exception("지원하지 않는 파일이에요");
            }
            catch
            {
                try { if (File.Exists(dst)) File.Delete(dst); } catch { }
                throw;
            }
            return dst;
        }

        public class BatchResult { public int Ok, Total, Skipped; public string LastOut, Error; public bool Merged, Canceled; }

        // 움직이는 GIF → 영상. MP4는 Windows 인코더 우선(친구들 PC엔 ffmpeg가 거의 없음), 안 되면 ffmpeg
        static void GifToVideo(string src, string dst, string fmt)
        {
            if (fmt == "mp4" && GifVideo.Available)
            {
                try { GifVideo.ToMp4(src, dst); return; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (FfmpegPath == null) throw new Exception("영상으로 만들지 못했어요 (" + ex.Message + ")");
                    Log.Error("Windows 인코더 실패, ffmpeg로 다시 시도: " + ex.Message);
                    try { if (File.Exists(dst)) File.Delete(dst); } catch { }
                }
            }
            string scale = "-vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" -pix_fmt yuv420p";
            RunFfmpeg(src, dst, fmt == "mp4"
                ? scale + " -c:v libx264 -crf 20 -preset veryfast -movflags +faststart"
                : scale + " -c:v libvpx-vp9 -crf 32 -b:v 0 -deadline realtime -cpu-used 8");
        }

        // 변환 묶음 하나의 진행 보고·취소 창구. 변환 코드는 중간중간 Step()을 불러 준다.
        public class Job
        {
            public volatile bool Cancel;
            public volatile bool Finished;     // 변환 스레드가 정리까지 끝냈는지
            public volatile int Done;          // 이미 끝난 파일 수
            public Action<string> Report;      // "2/5", "페이지 3/20" 같은 진행 문구
            readonly List<int> pids = new List<int>();
            public void AddPid(int pid) { lock (pids) pids.Add(pid); }
            public int[] Pids { get { lock (pids) return pids.ToArray(); } }
        }

        // 이번 변환이 새로 띄운 Office·한글 프로세스 기록
        public static void RegisterPid(int pid) { Job j = current; if (j != null) j.AddPid(pid); }

        // 멈춘 변환이 남긴 "숨은" Office만 끈다. 화면에 창이 보이는 건(사용자가 쓰기 시작한 것) 건드리지 않음
        public static int KillHidden(Job j)
        {
            int n = 0;
            if (j == null) return 0;
            foreach (int pid in j.Pids)
            {
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                    {
                        if (p.HasExited) continue;
                        if (Native.HasVisibleDocumentWindow(pid)) continue; // 질문창만 떠 있는 건 정리 대상
                        p.Kill();
                        n++;
                    }
                }
                catch { }
            }
            if (n > 0) Log.Info("멈춘 변환이 남긴 숨은 Office/한글 " + n + "개를 껐어요");
            return n;
        }

        // 변환이 끝난 뒤: 이번에 띄운 Office가 몇 초 안에 스스로 안 꺼지면(질문창에 막힌 경우 등) 정리
        static void CleanupOffice(Job j)
        {
            int[] pids = j.Pids;
            if (pids.Length == 0) return;
            for (int i = 0; i < 30; i++)
            {
                bool alive = false;
                foreach (int pid in pids)
                    try { using (Process p = Process.GetProcessById(pid)) if (!p.HasExited) alive = true; } catch { }
                if (!alive) return;
                System.Threading.Thread.Sleep(200);
            }
            KillHidden(j);
        }

        // 기록에 남길 오류 문구에서 파일 이름·경로를 뺀다
        static string Scrub(string message, string file)
        {
            if (message == null) return "";
            try
            {
                message = message.Replace(file, "(파일)").Replace(Path.GetFileName(file), "(파일)");
                string dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir)) message = message.Replace(dir, "(폴더)");
            }
            catch { }
            return message;
        }

        [ThreadStatic] static Job current;
        static long activityTicks = DateTime.Now.Ticks;

        // 마지막으로 "일하고 있다"는 신호가 온 시각 (감시 기능이 멈춤 판단에 사용)
        public static DateTime LastActivity { get { return new DateTime(System.Threading.Interlocked.Read(ref activityTicks)); } }
        public static void Touch() { System.Threading.Interlocked.Exchange(ref activityTicks, DateTime.Now.Ticks); }
        public static bool Canceled { get { Job j = current; return j != null && j.Cancel; } }

        // 진행 신호 + 취소 확인. 취소됐으면 OperationCanceledException
        public static void Step(string detail)
        {
            Touch();
            Job j = current;
            if (j == null) return;
            if (j.Cancel) throw new OperationCanceledException();
            if (detail != null && j.Report != null) j.Report(detail);
        }

        // 한 번에 놓은 파일 묶음을 변환. 이미지 여러 장 → PDF는 한 파일로 합친다.
        public static BatchResult RunBatch(List<string> list, string fmt, Job job)
        {
            var r = new BatchResult();
            r.Total = list.Count;
            current = job;
            Touch();
            ComRetryFilter.Register();
            DialogGuard.Start();
            try
            {
                // 파일마다 확장자를 보고, 이 형식으로 못 바꾸는 파일은 오류가 아니라 "건너뜀"
                var todo = new List<string>();
                foreach (string f in list)
                {
                    if (TargetsForFile(f).Contains(fmt)) todo.Add(f);
                    else { r.Skipped++; Log.Info("건너뜀: " + Log.Describe(f) + " → " + fmt + " (이 형식으로 바꿀 수 없음)"); }
                }
                var images = todo.Where(f => KindOf(f) == Kind.Image).ToList();
                if (fmt == "pdf" && images.Count > 1)
                {
                    // 이미지 여러 장은 PDF 하나로 합치고, 나머지(문서 등)는 따로 변환
                    string dst = UniquePath(images[0], "pdf");
                    try
                    {
                        ImagesToPdf(images, dst); r.Ok += images.Count; r.LastOut = dst; r.Merged = true; job.Done = r.Ok;
                        Log.Info("변환 성공: 이미지 " + images.Count + "장 → pdf 1개로 합침");
                    }
                    catch (Exception ex)
                    {
                        try { if (File.Exists(dst)) File.Delete(dst); } catch { }
                        if (ex is OperationCanceledException) throw;
                        r.Error = ex.Message;
                        Log.Error("변환 실패: 이미지 " + images.Count + "장 → pdf 합치기: " + Scrub(ex.Message, images[0]));
                    }
                    todo = todo.Where(f => KindOf(f) != Kind.Image).ToList();
                }
                for (int i = 0; i < todo.Count; i++)
                {
                    Step(todo.Count > 1 ? (i + 1) + "/" + todo.Count : null);
                    var sw = Stopwatch.StartNew();
                    string what = Log.Describe(todo[i]) + " → " + fmt;
                    try
                    {
                        r.LastOut = Convert(todo[i], fmt); r.Ok++; job.Done = r.Ok;
                        Log.Info("변환 성공: " + what + " (" + sw.Elapsed.TotalSeconds.ToString("0.0") + "초)");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        r.Error = Path.GetFileName(todo[i]) + ": " + (Environment.GetEnvironmentVariable("DRAGCONVERT_DEBUG") != null ? ex.ToString() : ex.Message);
                        Log.Error("변환 실패: " + what + ": " + Scrub(ex.Message, todo[i]));
                    }
                }
            }
            catch (OperationCanceledException) { r.Canceled = true; Log.Info("변환 취소 (" + r.Ok + "개는 저장됨)"); }
            finally
            {
                Docs.ReleaseAll();
                CleanupOffice(job);
                ComRetryFilter.Revoke();
                DialogGuard.Stop();
                current = null;
                job.Finished = true;
            }
            return r;
        }

        static string FfArgs(Kind k, string fmt)
        {
            switch (fmt)
            {
                case "mp3": return "-vn -c:a libmp3lame -q:a 2";
                case "wav": return "-vn -c:a pcm_s16le";
                case "flac": return "-vn -c:a flac";
                case "m4a": return "-vn -c:a aac -b:a 192k";
                case "ogg": return "-vn -c:a libvorbis -q:a 5";
                case "mp4": return "-c:v libx264 -crf 20 -preset veryfast -pix_fmt yuv420p -c:a aac -b:a 160k -movflags +faststart";
                case "mkv": return "-map 0 -c copy";
                case "webm": return "-c:v libvpx-vp9 -crf 32 -b:v 0 -row-mt 1 -deadline realtime -cpu-used 8 -c:a libopus";
                case "gif": return "-vf \"fps=12,scale='min(480,iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse\" -loop 0";
                case "webp": return "-quality 90";
            }
            return "";
        }

        static void RunFfmpeg(string src, string dst, string args)
        {
            string ff = FfmpegPath;
            if (ff == null) throw new Exception("ffmpeg가 필요해요");
            var psi = new ProcessStartInfo(ff, "-hide_banner -loglevel error -nostdin -y -i \"" + src + "\" " + args + " \"" + dst + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.StandardErrorEncoding = Encoding.UTF8;
            using (var p = Process.Start(psi))
            {
                var errTask = p.StandardError.ReadToEndAsync();
                while (!p.WaitForExit(500))
                {
                    if (Canceled)
                    {
                        try { p.Kill(); } catch { }
                        p.WaitForExit();
                        throw new OperationCanceledException();
                    }
                    Touch(); // ffmpeg는 질문창으로 멈추는 일이 없으니 돌고 있는 동안은 진행 중으로 본다
                }
                p.WaitForExit();
                string err = errTask.Result;
                if (p.ExitCode != 0)
                {
                    string last = err.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                    throw new Exception(last ?? ("ffmpeg 오류 코드 " + p.ExitCode));
                }
            }
        }

        // ---------------- 이미지 ----------------

        static void ConvertImage(string src, string dst, string fmt)
        {
            if (fmt == "webp") { RunFfmpeg(src, dst, FfArgs(Kind.Image, fmt)); return; }

            BitmapSource img;
            try
            {
                img = LoadImage(src);
            }
            catch (Exception ex)
            {
                // WIC 코덱이 없는 형식(예: 확장 미설치 HEIC)은 ffmpeg로 우회
                if (FfmpegPath != null && fmt != "ico") { RunFfmpeg(src, dst, ""); return; }
                throw new Exception("이 이미지를 읽을 코덱이 없어요 (" + ex.Message + ")");
            }

            if (fmt == "ico") { WriteIco(img, dst); return; }

            BitmapEncoder enc;
            switch (fmt)
            {
                case "png": enc = new PngBitmapEncoder(); img = ToBgra(img); break;
                case "jpg": var j = new JpegBitmapEncoder(); j.QualityLevel = 92; enc = j; img = FlattenOnWhite(img); break;
                case "bmp": enc = new BmpBitmapEncoder(); img = FlattenOnWhite(img); break;
                case "gif": enc = new GifBitmapEncoder(); img = ToBgra(img); break;
                case "tiff": enc = new TiffBitmapEncoder(); img = ToBgra(img); break;
                default: throw new Exception(fmt + " 형식은 아직 못 만들어요");
            }
            enc.Frames.Add(BitmapFrame.Create(img));
            using (var fs = File.Create(dst)) enc.Save(fs);
        }

        internal static BitmapSource LoadImage(string src)
        {
            BitmapFrame frame;
            using (var fs = File.OpenRead(src))
            {
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                // ICO처럼 여러 프레임이면 가장 큰 것
                frame = dec.Frames.OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight).First();
            }
            BitmapSource img = frame;
            int orient = ReadOrientation(frame);
            if (orient > 1) img = ApplyOrientation(img, orient);
            img.Freeze();
            return img;
        }

        // 폰 사진은 EXIF에 회전 정보만 있고 픽셀은 누워 있으니, 변환할 때 실제로 돌려준다
        static int ReadOrientation(BitmapFrame f)
        {
            try
            {
                var md = f.Metadata as BitmapMetadata;
                if (md == null) return 1;
                foreach (string q in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                    if (md.ContainsQuery(q)) return System.Convert.ToInt32(md.GetQuery(q));
            }
            catch { }
            return 1;
        }

        static BitmapSource ApplyOrientation(BitmapSource src, int o)
        {
            var g = new TransformGroup();
            switch (o)
            {
                case 2: g.Children.Add(new ScaleTransform(-1, 1)); break;
                case 3: g.Children.Add(new RotateTransform(180)); break;
                case 4: g.Children.Add(new ScaleTransform(1, -1)); break;
                case 5: g.Children.Add(new RotateTransform(90)); g.Children.Add(new ScaleTransform(-1, 1)); break;
                case 6: g.Children.Add(new RotateTransform(90)); break;
                case 7: g.Children.Add(new RotateTransform(270)); g.Children.Add(new ScaleTransform(-1, 1)); break;
                case 8: g.Children.Add(new RotateTransform(270)); break;
                default: return src;
            }
            return new TransformedBitmap(src, g);
        }

        static BitmapSource ToBgra(BitmapSource src)
        {
            if (src.Format == PixelFormats.Bgra32) return src;
            return new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        }

        // JPG/BMP는 투명도가 없으니 투명한 부분을 흰 배경으로 합성 (안 하면 검게 나옴)
        internal static BitmapSource FlattenOnWhite(BitmapSource src)
        {
            var b = ToBgra(src);
            int w = b.PixelWidth, h = b.PixelHeight;
            int s4 = w * 4, s3 = (w * 3 + 3) & ~3;
            var px = new byte[s4 * h];
            b.CopyPixels(px, s4, 0);
            var outp = new byte[s3 * h];
            for (int y = 0; y < h; y++)
            {
                int i = y * s4, o = y * s3;
                for (int x = 0; x < w; x++, i += 4, o += 3)
                {
                    int a = px[i + 3], inv = 255 - a;
                    outp[o] = (byte)((px[i] * a + 255 * inv) / 255);
                    outp[o + 1] = (byte)((px[i + 1] * a + 255 * inv) / 255);
                    outp[o + 2] = (byte)((px[i + 2] * a + 255 * inv) / 255);
                }
            }
            double dx = b.DpiX > 0 ? b.DpiX : 96, dy = b.DpiY > 0 ? b.DpiY : 96;
            return BitmapSource.Create(w, h, dx, dy, PixelFormats.Bgr24, null, outp, s3);
        }

        // 이미지들을 한 페이지씩 담은 PDF를 직접 작성 (JPEG를 그대로 넣음). 페이지는 이미지 비율, 긴 변 = A4 긴 변
        public static void ImagesToPdf(List<string> imgs, string dst)
        {
            var jpgs = new List<byte[]>();
            var dims = new List<int[]>();
            for (int k = 0; k < imgs.Count; k++)
            {
                string f = imgs[k];
                if (imgs.Count > 1) Step("이미지 " + (k + 1) + "/" + imgs.Count);
                BitmapSource flat = FlattenOnWhite(LoadImage(f));
                var enc = new JpegBitmapEncoder();
                enc.QualityLevel = 92;
                enc.Frames.Add(BitmapFrame.Create(flat));
                using (var ms = new MemoryStream()) { enc.Save(ms); jpgs.Add(ms.ToArray()); }
                dims.Add(new[] { flat.PixelWidth, flat.PixelHeight });
            }

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var offsets = new List<long>();
            using (var fs = File.Create(dst))
            {
                Action<string> W = str => { byte[] b = Encoding.ASCII.GetBytes(str); fs.Write(b, 0, b.Length); };
                Action begin = () => { offsets.Add(fs.Position); W(offsets.Count + " 0 obj\n"); };
                W("%PDF-1.4\n");
                fs.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }, 0, 6);

                begin(); W("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
                var kids = new StringBuilder();
                for (int i = 0; i < jpgs.Count; i++) kids.Append(3 + i * 3).Append(" 0 R ");
                begin(); W("<< /Type /Pages /Kids [" + kids + "] /Count " + jpgs.Count + " >>\nendobj\n");

                for (int i = 0; i < jpgs.Count; i++)
                {
                    int w = dims[i][0], h = dims[i][1];
                    double sc = 842.0 / Math.Max(w, h);
                    string pw = (w * sc).ToString("0.##", inv), ph = (h * sc).ToString("0.##", inv);
                    int pageObj = 3 + i * 3;
                    begin();
                    W("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + pw + " " + ph + "] /Resources << /XObject << /Im0 " + (pageObj + 1) +
                      " 0 R >> >> /Contents " + (pageObj + 2) + " 0 R >>\nendobj\n");
                    begin();
                    W("<< /Type /XObject /Subtype /Image /Width " + w + " /Height " + h +
                      " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpgs[i].Length + " >>\nstream\n");
                    fs.Write(jpgs[i], 0, jpgs[i].Length);
                    W("\nendstream\nendobj\n");
                    string content = "q " + pw + " 0 0 " + ph + " 0 0 cm /Im0 Do Q";
                    begin(); W("<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream\nendobj\n");
                }

                long xref = fs.Position;
                W("xref\n0 " + (offsets.Count + 1) + "\n0000000000 65535 f \n");
                foreach (long o in offsets) W(o.ToString("D10") + " 00000 n \n");
                W("trailer\n<< /Size " + (offsets.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
            }
        }

        // 여러 크기의 PNG를 담은 .ico (16~256px, 비율 유지 + 정사각형 투명 여백)
        static void WriteIco(BitmapSource src, string dst)
        {
            int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
            var pngs = new List<byte[]>();
            foreach (int s in sizes)
            {
                var dv = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
                double sc = Math.Min((double)s / src.PixelWidth, (double)s / src.PixelHeight);
                double w = src.PixelWidth * sc, h = src.PixelHeight * sc;
                using (var dc = dv.RenderOpen())
                    dc.DrawImage(src, new Rect((s - w) / 2, (s - h) / 2, w, h));
                var rtb = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var ms = new MemoryStream()) { enc.Save(ms); pngs.Add(ms.ToArray()); }
            }
            using (var fs = File.Create(dst))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    bw.Write((byte)0); bw.Write((byte)0);
                    bw.Write((ushort)1); bw.Write((ushort)32);
                    bw.Write((uint)pngs[i].Length); bw.Write((uint)offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) bw.Write(p);
            }
        }
    }
}
