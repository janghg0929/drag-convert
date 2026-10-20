using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ShiftConvert
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
        [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);

        public const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_SHIFT = 0x10;
        public const int SM_SWAPBUTTON = 23;
        public static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
    }

    static class Program
    {
        const string InstanceName = "ShiftConvert_SingleInstance";
        public const string HealthyEventName = "ShiftConvert_UpdateHealthy";
        static Mutex instance;

        // 업데이트 중 새 버전에게 "실행 중" 자리를 넘겨주고, 실패하면 되찾는다
        public static void ReleaseInstance() { try { instance.ReleaseMutex(); } catch { } }
        public static void ReacquireInstance()
        {
            try { instance.WaitOne(5000); } catch (AbandonedMutexException) { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            // 릴리스 스크립트 검증용: ShiftConvert.exe --verify-update <exe> <sig> <버전>
            if (args.Length >= 4 && args[0] == "--verify-update")
            {
                try { Updater.Verify(File.ReadAllBytes(args[1]), File.ReadAllText(args[2]), args[3]); Console.WriteLine("SIGNATURE OK"); return 0; }
                catch (Exception ex) { Console.WriteLine("SIGNATURE FAIL: " + ex.Message); return 1; }
            }
            if (args.Length >= 1 && args[0] == "--version") { Console.WriteLine(AppVersion.Current); return 0; }

            // 테스트용: ShiftConvert.exe --convert <형식> <파일> [파일...]  (창에 놓는 것과 같은 동작)
            if (args.Length >= 3 && args[0] == "--convert")
            {
                var list = args.Skip(2).Select(Path.GetFullPath).ToList();
                var r = Converter.RunBatch(list, args[1].ToLowerInvariant(), new Converter.Job());
                Console.WriteLine("ok " + r.Ok + "/" + r.Total + (r.Merged ? " (merged)" : "") + (r.Canceled ? " (canceled)" : "") + " -> " + r.LastOut);
                if (r.Error != null) Console.WriteLine("ERROR: " + r.Error);
                return r.Error == null ? 0 : 1;
            }

            // 디자인 확인용: ShiftConvert.exe --preview <out.png> <상태> <강조 칸 번호> <파일...>
            if (args.Length >= 4 && args[0] == "--preview")
            {
                Native.SetProcessDPIAware();
                PopupForm.SavePreview(args[1], args[2], int.Parse(args[3]), args.Skip(4).Select(Path.GetFullPath).ToArray());
                return 0;
            }

            bool updated = args.Contains("--updated");
            bool fresh;
            instance = new Mutex(true, InstanceName, out fresh);
            if (!fresh && updated)
            {
                // 이전 버전이 자리를 넘겨줄 때까지 잠깐 기다림
                try { fresh = instance.WaitOne(10000); } catch (AbandonedMutexException) { fresh = true; }
            }
            if (!fresh)
            {
                MessageBox.Show("Shift 변환기가 이미 실행 중이에요. 작업 표시줄 오른쪽 트레이 아이콘을 확인하세요.", "Shift 변환기");
                return 0;
            }
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp(args.Contains("--silent"), updated));
            GC.KeepAlive(instance);
            return 0;
        }
    }

    // 트레이에 상주하면서 "탐색기에서 파일을 끄는 중 + Shift" 를 감지한다.
    // 다른 프로세스의 드래그 내용은 직접 읽을 수 없으므로, 커서 아래에 드롭 타깃 창(PopupForm)을 띄우고
    // 사용자가 거기에 놓으면 그때 파일 경로를 받는 방식.
    class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "ShiftConvert";

        readonly NotifyIcon tray;
        readonly PopupForm popup;
        readonly System.Windows.Forms.Timer timer;
        readonly ToolStripMenuItem autoStartItem;
        bool enabled = true;

        bool btnPrev, armed, shiftHeldAtStart, shownThisDrag;
        Point downPt;

        // 업데이트
        readonly SynchronizationContext ui;
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer();
        readonly ToolStripMenuItem updateItem;
        Updater.Ready pendingUpdate;
        bool checking, balloonIsUpdate;
        static readonly bool AutoApplyForTest = Environment.GetEnvironmentVariable("SHIFTCONVERT_UPDATE_AUTOAPPLY") == "1";

        public TrayApp(bool silent, bool updated)
        {
            popup = new PopupForm();
            var h = popup.Handle; // 핸들을 미리 만들어 드롭 타깃 등록

            var menu = new ContextMenuStrip();
            var enableItem = new ToolStripMenuItem("사용", null, (s, e) => { enabled = !enabled; ((ToolStripMenuItem)s).Checked = enabled; });
            enableItem.Checked = true;
            autoStartItem = new ToolStripMenuItem("Windows 시작 시 자동 실행", null, (s, e) => ToggleAutoStart());
            autoStartItem.Checked = IsAutoStart();
            menu.Items.Add(enableItem);
            menu.Items.Add(autoStartItem);
            menu.Items.Add("사용법", null, (s, e) => ShowHelp());
            updateItem = new ToolStripMenuItem("업데이트 확인", null, (s, e) => { if (pendingUpdate != null) PromptUpdate(); else CheckForUpdate(true); });
            updateItem.Visible = Updater.Configured;
            menu.Items.Add(updateItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료", null, (s, e) => ExitThread());
            menu.Opening += (s, e) => autoStartItem.Checked = IsAutoStart();

            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            tray.Text = "Shift 변환기 " + AppVersion.Current + " — 파일을 끌면서 Shift";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += (s, e) => ShowHelp();
            tray.BalloonTipClicked += (s, e) => { if (balloonIsUpdate && pendingUpdate != null) PromptUpdate(); };
            if (!silent)
                tray.ShowBalloonTip(4000, "Shift 변환기 실행 중",
                    "탐색기에서 파일을 끌다가 Shift를 누르면 변환 창이 떠요.", ToolTipIcon.Info);

            ThreadPool.QueueUserWorkItem(_ => Docs.CleanStaleTemp());

            ui = SynchronizationContext.Current;
            if (updated) FinishUpdate();
            else Updater.CleanDownloads();
            if (Updater.Configured)
            {
                // 켜고 1분 뒤 한 번, 그 뒤로 하루에 한 번 확인
                updateTimer.Interval = AutoApplyForTest ? 3000 : 60 * 1000;
                updateTimer.Tick += (s, e) => { updateTimer.Interval = 24 * 60 * 60 * 1000; CheckForUpdate(false); };
                updateTimer.Start();
            }

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 15;
            timer.Tick += Poll;
            timer.Start();
        }

        void Poll(object sender, EventArgs e)
        {
            int btn = Native.GetSystemMetrics(Native.SM_SWAPBUTTON) != 0 ? Native.VK_RBUTTON : Native.VK_LBUTTON;
            bool down = Native.Down(btn);
            bool shift = Native.Down(Native.VK_SHIFT);
            Point p = Cursor.Position;

            if (down && !btnPrev)
            {
                downPt = p;
                // Shift를 누른 채 시작한 드래그(범위 선택 등)는 무시 — 드래그 도중에 새로 누른 Shift만 신호로 본다
                shiftHeldAtStart = shift;
                shownThisDrag = false;
                // (숨겨진 팝업의 옛 위치는 무시 — 안 그러면 마지막으로 뜬 자리에서 기능이 안 켜짐)
                armed = enabled && !(popup.Visible && popup.Bounds.Contains(p)) && IsExplorerAt(p);
            }
            if (down && armed)
            {
                if (!shift) shiftHeldAtStart = false;
                int dx = p.X - downPt.X, dy = p.Y - downPt.Y;
                if (!shownThisDrag && shift && !shiftHeldAtStart && dx * dx + dy * dy > 36)
                {
                    shownThisDrag = true;
                    popup.ShowAt(p);
                }
            }
            if (!down && btnPrev && popup.Visible) popup.MouseReleased();
            btnPrev = down;
        }

        // ---------------- 업데이트 ----------------

        void CheckForUpdate(bool manual)
        {
            if (checking || pendingUpdate != null) return;
            checking = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err;
                Updater.Ready r = Updater.CheckAndDownload(out err);
                ui.Post(__ =>
                {
                    checking = false;
                    if (r != null)
                    {
                        pendingUpdate = r;
                        updateItem.Text = "업데이트 설치 (버전 " + r.Version + ")";
                        if (AutoApplyForTest) { ApplyUpdate(); return; }
                        balloonIsUpdate = true;
                        tray.ShowBalloonTip(8000, "새 버전 " + r.Version + "이(가) 있어요", "여기를 누르면 업데이트할 수 있어요.", ToolTipIcon.Info);
                    }
                    else if (manual)
                    {
                        balloonIsUpdate = false;
                        if (err != null) tray.ShowBalloonTip(5000, "업데이트 확인 실패", err, ToolTipIcon.Warning);
                        else tray.ShowBalloonTip(3000, "최신 버전이에요", "지금 버전: " + AppVersion.Current, ToolTipIcon.Info);
                    }
                }, null);
            });
        }

        void PromptUpdate()
        {
            Updater.Ready r = pendingUpdate;
            if (r == null) return;
            if (popup.IsBusy) { MessageBox.Show("지금 변환 중이에요. 끝난 뒤에 다시 눌러 주세요.", "Shift 변환기"); return; }
            string notes = r.Notes.Length > 600 ? r.Notes.Substring(0, 600) + "…" : r.Notes;
            var answer = MessageBox.Show(
                "새 버전 " + r.Version + "이(가) 있어요. (지금 버전 " + AppVersion.Current + ")\n\n" +
                (notes.Length > 0 ? "바뀐 점:\n" + notes + "\n\n" : "") +
                "지금 업데이트하고 다시 시작할까요?",
                "Shift 변환기 업데이트", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes) ApplyUpdate();
        }

        // 실행 중인 exe는 지울 수 없지만 이름은 바꿀 수 있다 → 지금 exe를 .old로, 새 exe를 원래 이름으로 놓고 새 버전 실행.
        // 새 버전이 20초 안에 "정상 시작" 신호를 안 보내면 원래대로 되돌린다.
        void ApplyUpdate()
        {
            Updater.Ready r = pendingUpdate;
            string cur = Application.ExecutablePath, old = cur + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            try { File.Move(cur, old); }
            catch (Exception ex) { Fail("이 위치에서는 프로그램을 바꿀 수 없어요 (" + ex.Message + ")"); return; }
            try { File.Copy(r.ExePath, cur); }
            catch (Exception ex) { try { File.Move(old, cur); } catch { } Fail("새 버전을 복사하지 못했어요 (" + ex.Message + ")"); return; }

            tray.Visible = false;
            timer.Stop();
            updateTimer.Stop();
            using (var healthy = new EventWaitHandle(false, EventResetMode.ManualReset, Program.HealthyEventName))
            {
                healthy.Reset();
                Program.ReleaseInstance();
                Process p = null;
                try { p = Process.Start(cur, "--updated --silent"); } catch { }
                if (p != null && healthy.WaitOne(20000))
                {
                    ExitThread(); // 새 버전이 잘 떴으니 이전 버전은 종료
                    return;
                }
                try { if (p != null && !p.HasExited) p.Kill(); if (p != null) p.WaitForExit(5000); } catch { }
            }

            // 되돌리기
            for (int i = 0; i < 20; i++)
            {
                try { if (File.Exists(cur)) File.Delete(cur); File.Move(old, cur); break; }
                catch { Thread.Sleep(250); }
            }
            Program.ReacquireInstance();
            tray.Visible = true;
            timer.Start();
            pendingUpdate = null;
            updateItem.Text = "업데이트 확인";
            Updater.CleanDownloads();
            Fail("새 버전이 제대로 시작되지 않아서 이전 버전으로 되돌렸어요.");
        }

        // 새 버전 쪽: 이전 버전에게 "잘 떴다"고 알리고, 잠시 뒤 .old 정리
        void FinishUpdate()
        {
            try { using (var ev = EventWaitHandle.OpenExisting(Program.HealthyEventName)) ev.Set(); } catch { }
            balloonIsUpdate = false;
            tray.ShowBalloonTip(4000, "업데이트 완료", "버전 " + AppVersion.Current + "(으)로 바뀌었어요.", ToolTipIcon.Info);
            var cleanup = new System.Windows.Forms.Timer { Interval = 30000 };
            cleanup.Tick += (s, e) =>
            {
                cleanup.Stop();
                try { File.Delete(Application.ExecutablePath + ".old"); } catch { }
                Updater.CleanDownloads();
            };
            cleanup.Start();
        }

        void Fail(string message)
        {
            balloonIsUpdate = false;
            tray.ShowBalloonTip(6000, "업데이트 실패", message, ToolTipIcon.Warning);
        }

        static bool IsExplorerAt(Point p)
        {
            try
            {
                var pt = new Native.POINT { X = p.X, Y = p.Y };
                IntPtr w = Native.GetAncestor(Native.WindowFromPoint(pt), 2 /* GA_ROOT */);
                int pid;
                Native.GetWindowThreadProcessId(w, out pid);
                using (var proc = Process.GetProcessById(pid))
                    return string.Equals(proc.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        bool IsAutoStart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }

        void ToggleAutoStart()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
                else k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --silent");
            }
            autoStartItem.Checked = IsAutoStart();
        }

        void ShowHelp()
        {
            string ff = Converter.FfmpegPath;
            MessageBox.Show(
                "1. 탐색기(또는 바탕화면)에서 파일을 마우스로 끌기 시작하세요.\n" +
                "2. 끄는 도중에 Shift 키를 누르면 커서 옆에 변환 창이 떠요.\n" +
                "3. 원하는 형식 칸 위에 파일을 놓으면 같은 폴더에 변환된 파일이 생겨요.\n" +
                "   (원본은 그대로 남아요. 여러 파일도 한 번에 됩니다)\n\n" +
                "이미지: PNG · JPG · PDF · BMP · GIF · TIFF · ICO (여러 장 → PDF는 한 파일로 합쳐짐)\n" +
                "문서: Word · Excel · PowerPoint → PDF 등 (설치된 Office 사용)\n" +
                "한글: HWP ↔ HWPX, HWP → PDF · DOCX · TXT (설치된 한컴오피스 사용)\n" +
                "PDF · 슬라이드 → 페이지별 PNG/JPG\n" +
                "WebP 만들기 · 오디오 · 영상: ffmpeg 필요\n\n" +
                "ffmpeg: " + (ff ?? "없음 — 'winget install ffmpeg' 으로 설치 후 이 프로그램을 다시 켜세요"),
                "Shift 변환기 " + AppVersion.Current + " 사용법");
        }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    using (var path = PopupForm.RoundRect(new RectangleF(1, 1, 30, 30), 8))
                    using (var br = new SolidBrush(Color.FromArgb(76, 125, 255)))
                        g.FillPath(br, path);
                    using (var f = new Font("Segoe UI Symbol", 20, FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString("⇧", f, Brushes.White, new RectangleF(0, 1, 32, 32), sf);
                    }
                }
                IntPtr hi = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(hi).Clone();
                Native.DestroyIcon(hi);
                return icon;
            }
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            updateTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            popup.Close();
            base.ExitThreadCore();
        }
    }
}
