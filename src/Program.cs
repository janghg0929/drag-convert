using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
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
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);

        // 그 프로세스에 대화상자(#32770) 말고 "보이는" 창이 있는지 = 사용자가 문서를 보고 있는지
        public static bool HasVisibleDocumentWindow(int pid)
        {
            bool found = false;
            EnumWindows((h, l) =>
            {
                int p;
                GetWindowThreadProcessId(h, out p);
                if (p != pid || !IsWindowVisible(h)) return true;
                var cls = new System.Text.StringBuilder(256);
                GetClassName(h, cls, 256);
                if (cls.ToString() != "#32770") { found = true; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02;
        public const int SM_SWAPBUTTON = 23;
        public static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
    }

    static class Program
    {
        const string InstanceName = "DragConvert_SingleInstance";
        public const string HealthyEventName = "DragConvert_UpdateHealthy";
        public const string ShowEventName = "DragConvert_ShowWindow";
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
            // 릴리스 스크립트 검증용: DragConvert.exe --verify-update <exe> <sig> <버전>
            if (args.Length >= 4 && args[0] == "--verify-update")
            {
                try { Updater.Verify(File.ReadAllBytes(args[1]), File.ReadAllText(args[2]), args[3]); Console.WriteLine("SIGNATURE OK"); return 0; }
                catch (Exception ex) { Console.WriteLine("SIGNATURE FAIL: " + ex.Message); return 1; }
            }
            if (args.Length >= 1 && args[0] == "--version") { Console.WriteLine(AppVersion.Current); return 0; }

            // 테스트용: DragConvert.exe --convert <형식> <파일> [파일...]  (창에 놓는 것과 같은 동작)
            if (args.Length >= 3 && args[0] == "--convert")
            {
                var list = args.Skip(2).Select(Path.GetFullPath).ToList();
                var r = Converter.RunBatch(list, args[1].ToLowerInvariant(), new Converter.Job());
                Console.WriteLine("ok " + r.Ok + "/" + r.Total + (r.Skipped > 0 ? " skipped " + r.Skipped : "") + (r.Merged ? " (merged)" : "") + (r.Canceled ? " (canceled)" : "") + " -> " + r.LastOut);
                if (r.Error != null) Console.WriteLine("ERROR: " + r.Error);
                return r.Error == null ? 0 : 1;
            }

            // 애니메이션 확인용: DragConvert.exe --preview-anim <폴더> <파일...> (시간대별 장면 저장)
            if (args.Length >= 3 && args[0] == "--preview-anim")
            {
                Native.SetProcessDPIAware();
                PopupForm.SaveAnimFrames(args[1], args.Skip(2).Select(Path.GetFullPath).ToArray());
                return 0;
            }

            // 애니메이션 성능 확인용: DragConvert.exe --demo <파일> (화면 가운데에서 재생하고 장면당 그리는 시간 출력)
            if (args.Length >= 2 && args[0] == "--demo")
            {
                Native.SetProcessDPIAware();
                PopupForm.RunDemo(Path.GetFullPath(args[1]));
                var raw = PopupForm.FrameMs; var slow = new System.Text.StringBuilder(); for (int i = 0; i < raw.Count; i++) if (raw[i] > 25) slow.Append("#" + i + "=" + raw[i].ToString("0") + "ms ");
                Console.WriteLine("slow frames: " + slow);
                var ms = new System.Collections.Generic.List<double>(raw); ms.Sort();
                Console.WriteLine("frames " + ms.Count + ", median " + ms[ms.Count / 2].ToString("0.0") + "ms, p95 " + ms[(int)(ms.Count * 0.95)].ToString("0.0") + "ms, max " + ms[ms.Count - 1].ToString("0.0") + "ms");
                return 0;
            }

            // 디자인 확인용: DragConvert.exe --preview <out.png> <상태> <강조 칸 번호> <파일...>
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
                // 이미 실행 중이면 그쪽 창을 앞으로 띄우고 끝
                try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); }
                catch { MessageBox.Show("끌어서 변환이 이미 실행 중이에요.", "끌어서 변환"); }
                return 0;
            }

            // 예상 못 한 오류로 꺼지지 않게 기록만 하고 계속
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log.Error("처리 안 된 오류: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("치명적 오류: " + e.ExceptionObject);

            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp(args.Contains("--silent"), updated));
            GC.KeepAlive(instance);
            return 0;
        }
    }

    // 트레이에 상주하면서 "탐색기에서 파일을 끄는 중 + 지정한 키"를 감지한다.
    // 다른 프로세스의 드래그 내용은 직접 읽을 수 없으므로, 커서 아래에 드롭 타깃 창(PopupForm)을 띄우고
    // 사용자가 거기에 놓으면 그때 파일 경로를 받는 방식.
    class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "DragConvert";

        readonly NotifyIcon tray;
        readonly PopupForm popup;
        readonly System.Windows.Forms.Timer timer;
        readonly ToolStripMenuItem enableItem;
        MainForm main;

        bool btnPrev, armed, keyHeldAtStart, shownThisDrag;
        readonly DragHook hook = new DragHook(); // 끄는 동안에만 지정 키를 가로챔
        Point downPt;

        public readonly bool Elevated;
        public bool Exiting { get; private set; }

        // 업데이트
        readonly SynchronizationContext ui;
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer();
        Updater.Ready pendingUpdate;
        bool checking, balloonIsUpdate;
        string updateStatus;
        static readonly bool AutoApplyForTest = Environment.GetEnvironmentVariable("DRAGCONVERT_UPDATE_AUTOAPPLY") == "1";

        public TrayApp(bool silent, bool updated)
        {
            bool firstRun = !File.Exists(Path.Combine(Settings.Folder, "settings.ini"));
            Settings.Load();
            Elevated = IsElevated();
            Log.Info("시작: 버전 " + AppVersion.Current + ", " + WindowsVersion() +
                     (Elevated ? ", 관리자 권한" : "") + (silent ? ", 자동 실행" : "") + (updated ? ", 업데이트 직후" : "") +
                     ", 키 " + Settings.TriggerName);

            popup = new PopupForm();
            var h = popup.Handle; // 핸들을 미리 만들어 드롭 타깃 등록
            ui = SynchronizationContext.Current;

            var menu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem("창 열기", null, (s, e) => ShowMain());
            openItem.Font = new Font(openItem.Font, FontStyle.Bold);
            enableItem = new ToolStripMenuItem("사용하기", null, (s, e) => SetEnabled(!Settings.Enabled));
            menu.Items.Add(openItem);
            menu.Items.Add(enableItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료", null, (s, e) => Quit());
            menu.Opening += (s, e) => enableItem.Checked = Settings.Enabled;

            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            tray.ContextMenuStrip = menu;
            UpdateTrayText();
            tray.Visible = true;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
            tray.BalloonTipClicked += (s, e) => { if (balloonIsUpdate && pendingUpdate != null) PromptUpdate(); else ShowMain(); };

            ThreadPool.QueueUserWorkItem(_ => Docs.CleanStaleTemp());
            FixAutoStartPath();

            // 두 번째로 실행하면 이미 떠 있는 이 창을 앞으로
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            var waiter = new Thread(() => { while (true) { showEvent.WaitOne(); ui.Post(_ => ShowMain(), null); } });
            waiter.IsBackground = true;
            waiter.Start();

            updateStatus = Updater.Configured ? "지금 버전 " + AppVersion.Current + " · 하루에 한 번 새 버전을 확인해요" : "업데이트 설정이 없는 버전이에요";
            if (updated) FinishUpdate();
            else Updater.CleanDownloads();
            if (Updater.Configured)
            {
                // 켜고 1분 뒤 한 번, 그 뒤로 하루에 한 번 확인
                updateTimer.Interval = AutoApplyForTest ? 3000 : 60 * 1000;
                updateTimer.Tick += (s, e) => { updateTimer.Interval = 24 * 60 * 60 * 1000; CheckForUpdate(false); };
                updateTimer.Start();
            }

            if (Elevated)
                Toast("관리자 권한으로 실행 중이에요", "이 상태에서는 끌어 놓기가 막혀요. 창에서 '일반 권한으로 다시 시작'을 눌러 주세요.", ToolTipIcon.Warning);
            if (firstRun || Elevated || (!silent && Settings.ShowWindowOnStart)) ShowMain();
            else if (!silent && !updated) Toast("끌어서 변환 실행 중", "탐색기에서 파일을 끌다가 " + Settings.TriggerPhrase + " 누르면 원형 메뉴가 떠요.");

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 15;
            timer.Tick += Poll;
            timer.Start();
        }

        void Poll(object sender, EventArgs e)
        {
            int btn = Native.GetSystemMetrics(Native.SM_SWAPBUTTON) != 0 ? Native.VK_RBUTTON : Native.VK_LBUTTON;
            bool down = Native.Down(btn);
            int vk = Settings.TriggerVk;
            Point p = Cursor.Position;

            if (down && !btnPrev)
            {
                downPt = p;
                // 키를 누른 채 시작한 드래그(Shift 범위 선택 등)는 무시 — 드래그 도중에 새로 누른 키만 신호로 본다
                keyHeldAtStart = KeyNames.IsDown(vk);
                shownThisDrag = false;
                // (숨겨진 팝업의 옛 위치는 무시 — 안 그러면 마지막으로 뜬 자리에서 기능이 안 켜짐)
                armed = Settings.Enabled && !(popup.Visible && popup.Bounds.Contains(p)) && IsExplorerAt(p);
                if (armed) hook.Start(vk);
            }
            // 가로채는 중에는 키 눌림이 Windows 키 상태에 안 남으므로 가로채기 쪽 상태를 본다
            bool key = hook.Active ? hook.TriggerDown : KeyNames.IsDown(vk);
            if (down && armed)
            {
                if (!key) keyHeldAtStart = false;
                int dx = p.X - downPt.X, dy = p.Y - downPt.Y;
                if (!shownThisDrag && key && !keyHeldAtStart && dx * dx + dy * dy > 36)
                {
                    shownThisDrag = true;
                    popup.ShowAt(p);
                }
            }
            if (!down && btnPrev)
            {
                hook.Stop(); // 마우스를 떼면 바로 원래대로
                if (popup.Visible) popup.MouseReleased();
            }
            btnPrev = down;
        }

        // ---------------- 메인 창에서 쓰는 동작 ----------------

        public void ShowMain()
        {
            if (Exiting) return;
            if (main == null || main.IsDisposed) main = new MainForm(this);
            else main.RefreshAll();
            main.Show();
            if (main.WindowState == FormWindowState.Minimized) main.WindowState = FormWindowState.Normal;
            main.Activate();
            Native.SetForegroundWindow(main.Handle);
        }

        public void OnMainWindowHidden()
        {
            if (Settings.CloseHintShown) return;
            Settings.CloseHintShown = true;
            Settings.Save();
            Toast("트레이에서 계속 실행 중이에요", "창을 닫아도 꺼지지 않아요. 작업 표시줄 오른쪽 아이콘을 누르면 다시 열 수 있고, 끄려면 창의 '종료'를 누르세요.");
        }

        void RefreshMain() { if (main != null && !main.IsDisposed && main.Visible) main.RefreshAll(); }

        public void SetEnabled(bool on)
        {
            Settings.Enabled = on;
            Settings.Save();
            Log.Info(on ? "사용 켬" : "사용 끔");
            UpdateTrayText();
            RefreshMain();
        }

        public void SetTrigger(int vk)
        {
            if (KeyNames.ForbiddenReason(vk) != null) return;
            Settings.TriggerVk = vk;
            Settings.Save();
            Log.Info("메뉴 키 변경: " + Settings.TriggerName);
            UpdateTrayText();
            RefreshMain();
        }

        void UpdateTrayText()
        {
            string t = "끌어서 변환 " + AppVersion.Current + (Settings.Enabled ? " — 끌면서 " + Settings.TriggerName : " — 꺼짐");
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        public void Toast(string title, string text) { Toast(title, text, ToolTipIcon.Info); }
        void Toast(string title, string text, ToolTipIcon icon)
        {
            balloonIsUpdate = false;
            tray.ShowBalloonTip(5000, title, text, icon);
        }

        public void Quit()
        {
            Log.Info("종료");
            Exiting = true;
            ExitThread();
        }

        // 관리자 권한으로 실행됐을 때: 탐색기를 통해 일반 권한으로 다시 실행
        public void RelaunchUnelevated()
        {
            Log.Info("일반 권한으로 다시 시작");
            Exiting = true;
            timer.Stop();
            tray.Visible = false;
            Program.ReleaseInstance();
            try { Process.Start("explorer.exe", "\"" + Application.ExecutablePath + "\""); } catch (Exception ex) { Log.Error("다시 시작 실패: " + ex.Message); }
            ExitThread();
        }

        public void Uninstall()
        {
            string exe = Application.ExecutablePath;
            var answer = MessageBox.Show(
                "끌어서 변환을 제거할까요?\n\n" +
                "· Windows 자동 실행 설정\n· 설정, 기록(로그), 임시 파일, 받아 둔 업데이트\n· 프로그램 파일 (" + Path.GetFileName(exe) + ")\n\n" +
                "을 모두 지우고 종료해요. 변환해 둔 파일은 지우지 않아요.",
                "끌어서 변환 제거", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null && k.GetValue(RunName) != null) k.DeleteValue(RunName); } catch { }
            Log.Disabled = true;
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DragConvert");
            foreach (string d in new[] { Settings.Folder, local, Path.Combine(Path.GetTempPath(), "DragConvert") })
                try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }

            // 실행 중인 exe는 스스로 못 지우므로, 종료하고 3초 뒤 숨은 명령 창이 지운다
            try
            {
                var psi = new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 4 > nul & del /f /q \"" + exe + "\" & del /f /q \"" + exe + ".old\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch { }
            Exiting = true;
            ExitThread();
        }

        // Environment.OSVersion은 매니페스트가 없으면 Windows 8(6.2)로 속여 알려줘서 레지스트리에서 읽는다
        static string WindowsVersion()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int build; int.TryParse(k.GetValue("CurrentBuildNumber") as string, out build);
                    string disp = k.GetValue("DisplayVersion") as string;
                    return "Windows " + (build >= 22000 ? "11" : "10") + (disp != null ? " " + disp : "") + " (빌드 " + build + ")";
                }
            }
            catch { return "Windows " + Environment.OSVersion.Version; }
        }

        static bool IsElevated()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // ---------------- 자동 실행 ----------------

        public bool IsAutoStart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }

        public void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (!on) { if (k.GetValue(RunName) != null) k.DeleteValue(RunName); }
                    else k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --silent");
                }
                Log.Info(on ? "자동 실행 켬" : "자동 실행 끔");
            }
            catch (Exception ex) { Log.Error("자동 실행 설정 실패: " + ex.Message); }
            RefreshMain();
        }

        // 자동 실행을 켠 뒤 exe를 옮기면 예전 경로가 남는다 → 예전 파일이 없으면 지금 경로로 고침
        void FixAutoStartPath()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    string v = k == null ? null : k.GetValue(RunName) as string;
                    if (v == null) return;
                    string path = v.StartsWith("\"") ? v.Substring(1, Math.Max(0, v.IndexOf('"', 1) - 1)) : v.Split(' ')[0];
                    if (File.Exists(path) || string.Equals(path, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return;
                    k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --silent");
                    Log.Info("자동 실행 경로를 새 위치로 고침");
                }
            }
            catch { }
        }

        // ---------------- 업데이트 ----------------

        public bool HasPendingUpdate { get { return pendingUpdate != null; } }
        public string UpdateStatus { get { return updateStatus; } }

        public void CheckForUpdate(bool manual)
        {
            if (checking || pendingUpdate != null) return;
            checking = true;
            if (manual) { updateStatus = "확인 중…"; RefreshMain(); }
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
                        updateStatus = "새 버전 " + r.Version + "을(를) 설치할 수 있어요";
                        Log.Info("새 버전 발견: " + r.Version);
                        RefreshMain();
                        if (AutoApplyForTest) { ApplyUpdate(); return; }
                        balloonIsUpdate = true;
                        tray.ShowBalloonTip(8000, "새 버전 " + r.Version + "이(가) 있어요", "여기를 누르면 업데이트할 수 있어요.", ToolTipIcon.Info);
                        return;
                    }
                    if (err != null)
                    {
                        Log.Error("업데이트 확인 실패: " + err);
                        updateStatus = "확인 실패: " + err;
                    }
                    else updateStatus = "최신 버전이에요 (" + AppVersion.Current + ") · 마지막 확인 " + DateTime.Now.ToString("HH:mm");
                    RefreshMain();
                    if (manual && (main == null || !main.Visible))
                        Toast(err != null ? "업데이트 확인 실패" : "최신 버전이에요", err ?? ("지금 버전: " + AppVersion.Current));
                }, null);
            });
        }

        public void PromptUpdate()
        {
            Updater.Ready r = pendingUpdate;
            if (r == null) return;
            if (popup.IsBusy) { MessageBox.Show("지금 변환 중이에요. 끝난 뒤에 다시 눌러 주세요.", "끌어서 변환"); return; }
            string notes = r.Notes.Length > 600 ? r.Notes.Substring(0, 600) + "…" : r.Notes;
            var answer = MessageBox.Show(
                "새 버전 " + r.Version + "이(가) 있어요. (지금 버전 " + AppVersion.Current + ")\n\n" +
                (notes.Length > 0 ? "바뀐 점:\n" + notes + "\n\n" : "") +
                "지금 업데이트하고 다시 시작할까요?",
                "끌어서 변환 업데이트", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes) ApplyUpdate();
        }

        // 실행 중인 exe는 지울 수 없지만 이름은 바꿀 수 있다 → 지금 exe를 .old로, 새 exe를 원래 이름으로 놓고 새 버전 실행.
        // 새 버전이 20초 안에 "정상 시작" 신호를 안 보내면 원래대로 되돌린다.
        void ApplyUpdate()
        {
            Updater.Ready r = pendingUpdate;
            string cur = Application.ExecutablePath, old = cur + ".old";
            Log.Info("업데이트 적용: " + AppVersion.Current + " → " + r.Version);
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            try { File.Move(cur, old); }
            catch (Exception ex) { Fail("이 위치에서는 프로그램을 바꿀 수 없어요 (" + ex.Message + ")"); return; }
            try { File.Copy(r.ExePath, cur); }
            catch (Exception ex) { try { File.Move(old, cur); } catch { } Fail("새 버전을 복사하지 못했어요 (" + ex.Message + ")"); return; }

            tray.Visible = false;
            timer.Stop();
            updateTimer.Stop();
            if (main != null && !main.IsDisposed) main.Hide();
            using (var healthy = new EventWaitHandle(false, EventResetMode.ManualReset, Program.HealthyEventName))
            {
                healthy.Reset();
                Program.ReleaseInstance();
                Process p = null;
                try { p = Process.Start(cur, "--updated --silent"); } catch { }
                if (p != null && healthy.WaitOne(20000))
                {
                    Exiting = true;
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
            Updater.CleanDownloads();
            updateTimer.Interval = 24 * 60 * 60 * 1000;
            updateTimer.Start();
            updateStatus = "업데이트에 실패해서 이전 버전으로 되돌렸어요";
            Fail("새 버전이 제대로 시작되지 않아서 이전 버전으로 되돌렸어요.");
        }

        // 새 버전 쪽: 이전 버전에게 "잘 떴다"고 알리고, 잠시 뒤 .old 정리
        void FinishUpdate()
        {
            try { using (var ev = EventWaitHandle.OpenExisting(Program.HealthyEventName)) ev.Set(); } catch { }
            Log.Info("업데이트 완료: " + AppVersion.Current);
            Toast("업데이트 완료", "버전 " + AppVersion.Current + "(으)로 바뀌었어요.");
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
            Log.Error("업데이트 실패: " + message);
            Toast("업데이트 실패", message, ToolTipIcon.Warning);
            RefreshMain();
        }

        // ---------------- 기타 ----------------

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

        static Icon MakeIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var path = PopupForm.RoundRect(new RectangleF(1, 1, 30, 30), 8))
                    using (var br = new SolidBrush(Color.FromArgb(76, 125, 255)))
                        g.FillPath(br, path);
                }
                IntPtr hi = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(hi).Clone();
                Native.DestroyIcon(hi);
                return icon;
            }
        }

        protected override void ExitThreadCore()
        {
            Exiting = true;
            hook.Stop();
            timer.Stop();
            updateTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            if (main != null && !main.IsDisposed) main.Close();
            popup.Close();
            base.ExitThreadCore();
        }
    }
}
