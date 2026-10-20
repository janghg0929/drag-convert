using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ShiftConvert
{
    // 커서를 중심으로 뜨는 원형(도넛) 메뉴. 가운데 = 끌고 온 파일, 바깥 고리 = 바꿀 형식들.
    // 원 바깥이 투명해야 해서 레이어드 창(UpdateLayeredWindow)으로 직접 그린다.
    // 포커스를 뺏지 않아야 진행 중인 드래그가 끊기지 않는다.
    class PopupForm : Form
    {
        enum State { Waiting, Ready, NoTargets, Busy, Done, Error, Canceled }

        const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8, WS_EX_LAYERED = 0x80000;

        static readonly Color Backdrop = Color.FromArgb(22, 23, 27);   // 칸 사이 선 색
        static readonly Color CenterBg = Color.FromArgb(34, 35, 41);
        static readonly Color Border = Color.FromArgb(70, 72, 84);
        static readonly Color SegBg = Color.FromArgb(48, 50, 59);
        static readonly Color SegDim = Color.FromArgb(37, 38, 45);
        static readonly Color Accent = Color.FromArgb(76, 125, 255);
        static readonly Color Muted = Color.FromArgb(160, 163, 175);
        static readonly Color Good = Color.FromArgb(90, 205, 130);
        static readonly Color Bad = Color.FromArgb(240, 100, 100);

        readonly float S, R, Rin, Grow, Gap;
        readonly int Dim;
        readonly Font titleFont, subFont, extFont, extFontMid, extFontSmall, segSubFont, markFont;
        readonly System.Windows.Forms.Timer hideTimer = new System.Windows.Forms.Timer();
        int opacity = 255;

        // ---------------- 애니메이션 ----------------
        // 움직일 때만 타이머가 돌며 다시 그린다. 칸 판정(HitTest)은 애니메이션과 무관하게 원래 위치 기준.
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 15 };
        double tShown = -10, tState = -10, tClose = -10, lastTick;
        double? fakeNow;                       // 미리보기용 가짜 시각
        bool closing;
        float[] hoverAmt = new float[0];       // 칸마다 "마우스 올림" 정도 (0~1, 부드럽게 따라감)
        State lastState = State.Waiting;
        readonly List<Particle> confetti = new List<Particle>();
        readonly Random rng = new Random();
        static readonly bool Motion = AnimationsOn();
        static readonly Color[] ConfettiColors =
        {
            Color.FromArgb(76, 125, 255), Color.FromArgb(90, 205, 130), Color.FromArgb(255, 200, 70),
            Color.FromArgb(240, 100, 160), Color.FromArgb(160, 110, 255), Color.FromArgb(80, 210, 230)
        };

        class Particle { public float X, Y, VX, VY, Rot, VR, W, H; public Color Col; public double Born, Life; }

        double Now { get { return fakeNow ?? clock.Elapsed.TotalSeconds; } }

        [DllImport("user32.dll")] static extern bool SystemParametersInfo(int action, int param, ref bool value, int ini);

        // Windows 설정 > 접근성 > 시각 효과 > "애니메이션 효과"가 꺼져 있으면 애니메이션 없이
        static bool AnimationsOn()
        {
            bool on = true;
            try { SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, ref on, 0); } catch { }
            return on;
        }

        static float Clamp01(double v) { return (float)Math.Max(0, Math.Min(1, v)); }
        static float EaseOutCubic(float t) { float u = 1 - t; return 1 - u * u * u; }
        static float EaseInCubic(float t) { return t * t * t; }
        static float EaseOutBack(float t) { const float c1 = 1.9f, c3 = c1 + 1; float u = t - 1; return 1 + c3 * u * u * u + c1 * u * u; }
        static Color Lerp(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        void StartAnim()
        {
            if (!Motion || fakeNow != null) return;
            if (!anim.Enabled) { lastTick = Now; anim.Start(); }
        }

        void AnimTick()
        {
            double now = Now, dt = Math.Min(0.05, now - lastTick);
            lastTick = now;
            StepAnim(dt);
            if (closing && now - tClose > 0.16) { closing = false; anim.Stop(); base.Hide(); return; }
            Redraw();
            if (!NeedsAnim()) anim.Stop();
        }

        // 시간 dt(초)만큼 진행: 마우스 올림 정도, 색종이 물리, 투명도
        void StepAnim(double dt)
        {
            double now = Now;
            for (int i = 0; i < hoverAmt.Length; i++)
            {
                float target = i == hover ? 1 : 0;
                hoverAmt[i] += (target - hoverAmt[i]) * Clamp01(dt * 16);
            }
            float g = 1100 * S;
            for (int k = confetti.Count - 1; k >= 0; k--)
            {
                Particle p = confetti[k];
                if (now - p.Born > p.Life) { confetti.RemoveAt(k); continue; }
                p.VY += (float)(g * dt);
                p.VX *= (float)Math.Pow(0.5, dt);   // 공기 저항
                p.X += (float)(p.VX * dt);
                p.Y += (float)(p.VY * dt);
                p.Rot += (float)(p.VR * dt);
            }
            opacity = closing ? (int)(255 * (1 - Clamp01((now - tClose) / 0.16))) : (int)(255 * EaseOutCubic(Clamp01((now - tShown) / 0.14)));
        }

        bool NeedsAnim()
        {
            if (!Visible) return false;
            double now = Now;
            if (closing || now - tShown < 0.7 || confetti.Count > 0) return true;
            for (int i = 0; i < hoverAmt.Length; i++) if (Math.Abs(hoverAmt[i] - (i == hover ? 1 : 0)) > 0.01f) return true;
            if (state == State.Busy || state == State.Waiting || state == State.Ready) return true; // 회전 스피너 / 테두리 빛줄기
            if (state == State.Done && now - tState < 0.8) return true;
            if (state == State.Error && now - tState < 0.5) return true;
            return false;
        }

        // 상태가 바뀌는 순간: 완료면 색종이 터뜨리기
        void OnStateChanged()
        {
            tState = Now;
            if (state == State.Done && Motion) Burst();
        }

        void Burst()
        {
            float c = Dim / 2f;
            for (int k = 0; k < 56; k++)
            {
                double a = rng.NextDouble() * Math.PI * 2;
                float sp = (float)(260 + rng.NextDouble() * 420) * S;
                confetti.Add(new Particle
                {
                    X = c, Y = c, VX = (float)Math.Cos(a) * sp, VY = (float)Math.Sin(a) * sp - 260 * S,
                    Rot = (float)(rng.NextDouble() * 360), VR = (float)(rng.NextDouble() * 900 - 450),
                    W = (float)(4 + rng.NextDouble() * 5) * S, H = (float)(2.5 + rng.NextDouble() * 3) * S,
                    Col = ConfettiColors[rng.Next(ConfettiColors.Length)], Born = Now, Life = 1.1 + rng.NextDouble() * 0.5
                });
            }
        }

        // 닫힐 때 쏙 작아지며 사라짐
        new void Hide()
        {
            if (!Motion || !Visible || fakeNow != null) { closing = false; base.Hide(); return; }
            if (closing) return;
            closing = true;
            tClose = Now;
            StartAnim();
        }

        // 변환 감시: Office/한글이 질문창 등으로 멈추면 일정 시간 뒤 포기하고 다음 변환을 받을 수 있게
        const int StallSeconds = 180;
        readonly System.Windows.Forms.Timer watchdog = new System.Windows.Forms.Timer();
        int batchId;
        DateTime lastProgress;
        Converter.Job job;
        string doneFile;   // 완료 표시를 누르면 탐색기에서 이 파일을 선택

        State state = State.Waiting;
        List<string> files = new List<string>();
        List<string> targets = new List<string>();
        Kind kind;                                                          // 첫 파일 종류 (안내 문구용)
        Dictionary<string, int> support = new Dictionary<string, int>();     // 형식별로 바꿀 수 있는 파일 수
        Dictionary<string, string> labelFile = new Dictionary<string, string>(); // 형식별 설명 기준 파일
        int hover = -1;
        string msg = "";        // 상태별 보조 문구 (오류 내용, 진행률 등)
        Icon fileIcon;

        public PopupForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AllowDrop = true;
            Text = "끌어서 변환 메뉴";
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f;

            R = 150 * S;      // 바깥 고리 반지름
            Rin = 60 * S;     // 가운데 원 반지름
            Grow = 7 * S;     // 마우스 올린 칸이 바깥으로 튀어나오는 정도
            Gap = 3 * S;      // 칸 사이 선 굵기
            Dim = (int)Math.Ceiling(2 * (R + Grow + 16 * S)); // 그림자 여백 포함
            Size = new Size(Dim, Dim);

            titleFont = new Font("Malgun Gothic", 9.5f, FontStyle.Bold);
            subFont = new Font("Malgun Gothic", 8f);
            extFont = new Font("Segoe UI", 13f, FontStyle.Bold);
            extFontMid = new Font("Segoe UI", 11f, FontStyle.Bold);   // 칸이 많을 때 (종류가 섞인 파일)
            extFontSmall = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            segSubFont = new Font("Malgun Gothic", 7.5f);
            markFont = new Font("Segoe UI Symbol", 22f, FontStyle.Bold);

            hideTimer.Tick += (s, e) =>
            {
                hideTimer.Stop();
                if (state == State.Busy) return;
                Hide();
            };
            watchdog.Interval = 1000;
            watchdog.Tick += (s, e) =>
            {
                if (state != State.Busy) { watchdog.Stop(); return; }
                DateTime last = Converter.LastActivity > lastProgress ? Converter.LastActivity : lastProgress;
                if ((DateTime.Now - last).TotalSeconds < StallSeconds) return;
                watchdog.Stop();
                if (job != null) job.Cancel = true; // 나중에 Office가 풀려도 결과를 저장하지 않게
                Log.Error("변환이 " + StallSeconds + "초 동안 응답이 없어 중단");
                Converter.KillHidden(job);
                batchId++; // 멈춘 변환의 결과는 나중에 와도 무시
                state = State.Error;
                msg = "응답이 없어 중단했어요. Office·한글 창에 질문이 떠 있는지 확인해 주세요";
                if (!Visible) { opacity = 255; Show(); }
                Redraw();
                ScheduleHide(6000);
            };
            anim.Tick += (s, e) => AnimTick();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public bool IsBusy { get { return state == State.Busy; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e) { }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        // ---------------- 표시/숨김 ----------------

        public void ShowAt(Point cursor)
        {
            hideTimer.Stop();
            if (state == State.Busy) { closing = false; opacity = 255; Show(); BringToTop(); Redraw(); StartAnim(); return; }
            state = State.Waiting;
            files.Clear(); targets.Clear(); hover = -1; msg = "";
            if (fileIcon != null) { fileIcon.Dispose(); fileIcon = null; }
            // 원의 중심이 커서 → 살짝만 움직여도 DragEnter가 와서 파일 종류를 알 수 있고, 바깥 칸으로 밀어 놓으면 끝
            Location = Clamp(new Point(cursor.X - Dim / 2, cursor.Y - Dim / 2), cursor);
            closing = false;
            confetti.Clear();
            tShown = Now;
            opacity = Motion ? 0 : 255;
            Redraw();
            Show();
            BringToTop();
            StartAnim();
        }

        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, int flags);

        // 다시 보일 때 "항상 위" 창들 사이에서도 맨 앞으로 (안 하면 동영상 PIP·항상 위 고정 앱 밑에 깔릴 수 있음)
        void BringToTop()
        {
            const int SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
            SetWindowPos(Handle, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }

        public void MouseReleased()
        {
            if (state == State.Busy || state == State.Done || state == State.Error || state == State.Canceled) return;
            ScheduleHide(300);
        }

        void ScheduleHide(int ms)
        {
            hideTimer.Stop();
            hideTimer.Interval = ms;
            hideTimer.Start();
        }

        Point Clamp(Point p, Point near)
        {
            Rectangle wa = Screen.FromPoint(near).WorkingArea;
            int m = (int)(16 * S); // 그림자 부분은 화면 밖으로 나가도 됨
            p.X = Math.Max(wa.Left - m, Math.Min(p.X, wa.Right - Dim + m));
            p.Y = Math.Max(wa.Top - m, Math.Min(p.Y, wa.Bottom - Dim + m));
            return p;
        }

        // 변환 중 가운데 원 클릭 = 취소. 그 밖의 클릭 = 창만 숨김 (변환은 뒤에서 계속되고 끝나면 결과를 다시 보여줌)
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            float dx = e.X - Dim / 2f, dy = e.Y - Dim / 2f;
            bool center = dx * dx + dy * dy < Rin * Rin;
            if (state == State.Busy && center)
            {
                CancelJob();
                return;
            }
            if (state == State.Done && center && doneFile != null && File.Exists(doneFile))
            {
                // 탐색기에서 새 파일을 선택한 상태로 열기
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + doneFile + "\""); } catch { }
            }
            hideTimer.Stop();
            Hide();
        }

        void CancelJob()
        {
            Converter.Job canceled = job;
            if (canceled != null)
            {
                var reaper = new System.Windows.Forms.Timer { Interval = 20000 };
                reaper.Tick += (s, e) => { reaper.Stop(); reaper.Dispose(); if (!canceled.Finished) Converter.KillHidden(canceled); };
                reaper.Start();
            }
            if (job != null) job.Cancel = true;
            batchId++; // 이후에 오는 결과는 무시
            watchdog.Stop();
            int done = job != null ? job.Done : 0;
            state = State.Canceled;
            msg = done > 0 ? done + "개는 저장됨" : "저장 안 됨";
            Redraw();
            ScheduleHide(2000);
        }

        // ---------------- 히트 테스트 ----------------

        // 바깥 고리의 몇 번째 칸인지 (가운데·바깥이면 -1). 0번 칸은 12시 방향에서 시계방향으로 시작
        int HitTest(Point client)
        {
            if (targets.Count == 0) return -1;
            float dx = client.X - Dim / 2f, dy = client.Y - Dim / 2f;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < Rin || d > R + Grow) return -1;
            double a = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + 360) % 360;
            return Math.Min(targets.Count - 1, (int)(a / (360.0 / targets.Count)));
        }

        // ---------------- 드래그 앤 드롭 ----------------

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            hideTimer.Stop();
            if (state == State.Busy) { e.Effect = DragDropEffects.None; return; }

            var dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
            files = dropped == null ? new List<string>() : dropped.Where(File.Exists).ToList();
            if (fileIcon != null) { fileIcon.Dispose(); fileIcon = null; }

            if (files.Count == 0)
            {
                state = State.NoTargets; targets.Clear();
                msg = "폴더나 다른 항목은 변환할 수 없어요";
            }
            else
            {
                LoadFiles(files);
                if (targets.Count > 0) state = State.Ready;
                else
                {
                    state = State.NoTargets;
                    if (kind == Kind.Unknown) msg = "아직 지원하지 않는 형식이에요";
                    else if (kind == Kind.Document) msg = Docs.MissingAppHint(files[0]);
                    else msg = "ffmpeg가 필요해요 (winget install ffmpeg)";
                }
            }
            hover = -1;
            e.Effect = DragDropEffects.None;
            Redraw();
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            int h = state == State.Ready ? HitTest(PointToClient(new Point(e.X, e.Y))) : -1;
            if (h != hover) { hover = h; Redraw(); StartAnim(); }
            // 항상 "복사"로만 받는다. Shift 때문에 "이동"이 되면 탐색기가 원본을 지울 수 있음
            e.Effect = (h >= 0 && (e.AllowedEffect & DragDropEffects.Copy) != 0) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            if (hover != -1) { hover = -1; Redraw(); StartAnim(); }
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            int h = state == State.Ready ? HitTest(PointToClient(new Point(e.X, e.Y))) : -1;
            if (h < 0) { ScheduleHide(200); return; }
            e.Effect = DragDropEffects.Copy;
            StartConvert(files.ToList(), targets[h]);
        }

        void StartConvert(List<string> list, string fmt)
        {
            state = State.Busy;
            hover = targets.IndexOf(fmt);
            msg = fmt.ToUpperInvariant() + "(으)로";
            int id = ++batchId;
            lastProgress = DateTime.Now;
            doneFile = null;
            Log.Info("변환 시작: " + list.Count + "개 → " + fmt);
            var j = new Converter.Job();
            j.Report = detail => BeginInvoke((Action)(() =>
            {
                if (id != batchId) return;
                lastProgress = DateTime.Now;
                msg = fmt.ToUpperInvariant() + " · " + detail;
                Redraw();
            }));
            job = j;
            watchdog.Start();
            Redraw();

            var t = new Thread(() =>
            {
                Converter.BatchResult r;
                try
                {
                    r = Converter.RunBatch(list, fmt, j);
                }
                catch (Exception ex)
                {
                    r = new Converter.BatchResult { Total = list.Count, Error = ex.Message };
                }
                BeginInvoke((Action)(() => { if (id == batchId) Finish(r); }));
            });
            t.SetApartmentState(ApartmentState.STA); // WPF 이미징·Office 자동화용
            t.IsBackground = true;
            t.Start();
        }

        void Finish(Converter.BatchResult r)
        {
            watchdog.Stop();
            if (r.Canceled)
            {
                state = State.Canceled;
                msg = r.Ok > 0 ? r.Ok + "개는 저장됨" : "저장 안 됨";
                ScheduleHide(2000);
            }
            else if (r.Error == null)
            {
                state = State.Done;
                doneFile = r.LastOut;
                if (r.Merged && r.Ok == r.Total) msg = r.Total + "장 → " + Path.GetFileName(r.LastOut);
                else if (r.Ok == 1 && r.Skipped == 0) msg = Path.GetFileName(r.LastOut);
                else msg = r.Ok + "개 변환" + (r.Skipped > 0 ? " · " + r.Skipped + "개 건너뜀" : "");
                ScheduleHide(1800);
            }
            else
            {
                state = State.Error;
                msg = (r.Ok > 0 ? (r.Total - r.Ok) + "개 실패 · " : "") + r.Error;
                ScheduleHide(5000);
            }
            if (!Visible || closing) { closing = false; tShown = Now; opacity = Motion ? 0 : 255; Show(); }
            Redraw();
            StartAnim();
        }

        // ---------------- 레이어드 창 출력 ----------------

        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Native.POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref Native.POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        void Redraw()
        {
            if (state != lastState) { lastState = state; OnStateChanged(); }
            if (!Motion) opacity = 255;
            if (!IsHandleCreated) return;
            var frameClock = System.Diagnostics.Stopwatch.StartNew();
            EnsureSurface();
            using (var g = Graphics.FromImage(surface)) RenderTo(g);
            IntPtr screen = GetDC(IntPtr.Zero);
            try
            {
                var size = new SIZE { cx = Dim, cy = Dim };
                var src = new Native.POINT();
                var dst = new Native.POINT { X = Left, Y = Top };
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = (byte)opacity, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
                UpdateLayeredWindow(Handle, screen, ref dst, ref size, surfDC, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
                if (measureFrames) FrameMs.Add(frameClock.Elapsed.TotalMilliseconds);
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
        }

        // 화면용 그림판: 한 번 만든 DIB 메모리에 GDI+가 바로 그리고, Windows 창도 그 메모리를 바로 읽음 (매 장면 복사 없음)
        Bitmap surface, shadow;
        IntPtr surfDC, surfDib, surfOld;
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        void EnsureSurface()
        {
            if (surface != null) return;
            IntPtr screen = GetDC(IntPtr.Zero);
            surfDC = CreateCompatibleDC(screen);
            ReleaseDC(IntPtr.Zero, screen);
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = Dim, biHeight = -Dim, biPlanes = 1, biBitCount = 32 };
            IntPtr bits;
            surfDib = CreateDIBSection(surfDC, ref bi, 0, out bits, IntPtr.Zero, 0);
            surfOld = SelectObject(surfDC, surfDib);
            surface = new Bitmap(Dim, Dim, Dim * 4, PixelFormat.Format32bppPArgb, bits);
        }

        Bitmap MakeShadow()
        {
            var b = new Bitmap(Dim, Dim, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            using (var path = new GraphicsPath())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float c = Dim / 2f, sr = Dim / 2f - 1;
                path.AddEllipse(c - sr, c - sr, 2 * sr, 2 * sr);
                using (var pg = new PathGradientBrush(path))
                {
                    pg.CenterPoint = new PointF(c, c + 3 * S);
                    pg.CenterColor = Color.FromArgb(120, 0, 0, 0);
                    pg.SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) };
                    float f = (R - 6 * S) / sr;
                    pg.FocusScales = new PointF(f, f);
                    g.FillPath(pg, path);
                }
            }
            return b;
        }

        // 첫 장면에서 그림판·그림자를 만드느라 끊기지 않게 창이 만들어질 때 미리 준비
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            EnsureSurface();
            if (shadow == null) shadow = MakeShadow();
        }

        protected override void Dispose(bool disposing)
        {
            if (surface != null)
            {
                surface.Dispose(); surface = null;
                SelectObject(surfDC, surfOld); DeleteObject(surfDib); DeleteDC(surfDC);
            }
            if (shadow != null) { shadow.Dispose(); shadow = null; }
            base.Dispose(disposing);
        }

        // ---------------- 그리기 ----------------

        public static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // 도넛의 한 조각 (칸 사이에 일정한 두께의 틈을 남김)
        static GraphicsPath Segment(float c, float r0, float r1, float a0, float sweep, float gap)
        {
            var p = new GraphicsPath();
            if (sweep >= 359.9f)
            {
                p.AddEllipse(c - r1, c - r1, 2 * r1, 2 * r1);
                p.AddEllipse(c - r0, c - r0, 2 * r0, 2 * r0); // Alternate 채우기라 가운데가 뚫림
                return p;
            }
            float go = (float)(gap / 2 / r1 * 180 / Math.PI), gi = (float)(gap / 2 / r0 * 180 / Math.PI);
            p.AddArc(c - r1, c - r1, 2 * r1, 2 * r1, a0 + go, sweep - 2 * go);
            p.AddArc(c - r0, c - r0, 2 * r0, 2 * r0, a0 + sweep - gi, -(sweep - 2 * gi));
            p.CloseFigure();
            return p;
        }

        static PointF Polar(float c, float rad, float deg)
        {
            double a = deg * Math.PI / 180;
            return new PointF(c + (float)(rad * Math.Cos(a)), c + (float)(rad * Math.Sin(a)));
        }

        // 미리보기용: 새 그림에 그려서 돌려줌
        Bitmap Render()
        {
            var bmp = new Bitmap(Dim, Dim, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp)) RenderTo(g);
            return bmp;
        }

        void RenderTo(Graphics g)
        {
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                float c = Dim / 2f;
                double now = Now;
                if (hoverAmt.Length != targets.Count) { hoverAmt = new float[targets.Count]; if (!Motion && hover >= 0 && hover < targets.Count) hoverAmt[hover] = 1; }
                if (!Motion) for (int i = 0; i < hoverAmt.Length; i++) hoverAmt[i] = i == hover ? 1 : 0;

                // 열릴 때 톡 튀어나옴(살짝 커졌다 돌아옴), 닫힐 때 쏙 작아짐, 실패하면 도리도리
                float openP = Motion ? Clamp01((now - tShown) / 0.34) : 1;
                float zoom = 0.55f + 0.45f * EaseOutBack(openP);
                if (closing) zoom *= 1 - 0.18f * EaseInCubic(Clamp01((now - tClose) / 0.16));
                float shake = 0;
                if (Motion && state == State.Error)
                {
                    float ep = Clamp01((now - tState) / 0.45);
                    shake = (float)Math.Sin((now - tState) * 55) * 8 * S * (1 - ep);
                }
                // 크기가 변하는 중(열림·닫힘)엔 글자를 그리지 않음: 배율이 매번 달라지면 글자 캐시를 못 써서 장면당 30ms 이상 걸림
                // → 원이 자리를 잡은 뒤 글자가 스르륵 나타남
                bool textOK = !Motion || (!closing && openP >= 1);
                float textA = Motion ? Clamp01((now - tShown - 0.34) / 0.14) : 1;
                // 그림자 (한 번만 그려 두고 재사용). 크기 변환 없이 그려야 빠름: 배율이 1이 아니면 그림 전체를 매번 다시 계산해서 장면당 30ms+
                // → 원이 거의 다 커졌을 때만, 닫히는 중엔 생략
                if (shadow == null) shadow = MakeShadow();
                if (!closing && zoom > 0.92f) g.DrawImageUnscaled(shadow, (int)shake, 0);
                g.TranslateTransform(c + shake, c);
                g.ScaleTransform(zoom, zoom);
                g.TranslateTransform(-c, -c);



                // 바탕 원 (칸 사이 틈으로 보이는 선 색)
                using (var b = new SolidBrush(Backdrop)) g.FillEllipse(b, c - R, c - R, 2 * R, 2 * R);

                // 바깥 테두리 (칸보다 먼저 그려서 튀어나온 칸이 덮도록)
                using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1))
                    g.DrawEllipse(pen, c - R, c - R, 2 * R, 2 * R);

                bool dim = state == State.Busy || state == State.Done || state == State.Error || state == State.Canceled;
                var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                int n = targets.Count;

                if (n == 0)
                {
                    using (var path = Segment(c, Rin, R, 0, 360, 0))
                    using (var b = new SolidBrush(state == State.Waiting ? SegBg : SegDim))
                        g.FillPath(b, path);
                }
                float step = n > 0 ? 360f / n : 360f;
                for (int i = 0; i < n; i++)
                {
                    bool hot = i == hover;
                    float amt = i < hoverAmt.Length ? hoverAmt[i] : (hot ? 1 : 0);
                    float a0 = -90 + i * step;
                    // 시계 방향으로 하나씩 꽃 피듯 펼쳐짐
                    float bloom = Motion ? EaseOutCubic(Clamp01((now - tShown - 0.04 - i * Math.Min(0.035, 0.32 / n)) / 0.24)) : 1;
                    if (bloom <= 0) continue;
                    float full = R + Grow * amt;
                    float r1 = Rin + (full - Rin) * bloom;
                    Color hotCol = state == State.Done ? Good : state == State.Error ? Bad : Accent;
                    Color bg = Lerp(dim ? SegDim : SegBg, hotCol, amt);
                    // 빛 번짐
                    if (amt > 0.02f)
                        for (int k = 2; k >= 1; k--)
                            using (var glow = Segment(c, Rin + Gap * 0.5f, r1 + k * 3.5f * S, a0 - k * 0.9f, step + k * 1.8f, Gap))
                            using (var gb = new SolidBrush(Color.FromArgb((int)(52 * amt / k), hotCol)))
                                g.FillPath(gb, glow);
                    using (var path = Segment(c, Rin + Gap * 0.5f, r1, a0, step, Gap))
                    using (var b = new SolidBrush(bg))
                        g.FillPath(b, path);
                    if (bloom < 0.35f || !textOK) continue;

                    float mid = (Rin + r1) / 2 + 4 * S;
                    PointF pt = Polar(c, mid, a0 + step / 2);
                    Color fg = dim && !hot ? Color.FromArgb(95, 97, 106) : Color.White;
                    float boxW = Math.Min(110 * S, (float)(2 * Math.PI * mid / n) - 8 * S);
                    // 칸이 많으면 글자를 줄이고, 줄바꿈 없이 전부 보이게 (안 하면 DOCX가 DOC로 잘림)
                    Font ef = n <= 8 ? extFont : n <= 10 ? extFontMid : extFontSmall;
                    var noWrap = new StringFormat(center) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip };
                    // 마우스 올린 칸 글자는 살짝 커짐
                    var saved = g.Save();
                    float ls = 1 + 0.12f * amt;
                    g.TranslateTransform(pt.X, pt.Y - 9 * S); g.ScaleTransform(ls, ls); g.TranslateTransform(-pt.X, -(pt.Y - 9 * S));
                    using (var b = new SolidBrush(Color.FromArgb((int)(255 * textA), fg)))
                        g.DrawString(targets[i].ToUpperInvariant(), ef, b, new RectangleF(pt.X - boxW / 2, pt.Y - 22 * S, boxW, 26 * S), noWrap);
                    g.Restore(saved);
                    Color sc = hot ? Color.FromArgb(228, 234, 255) : dim ? Color.FromArgb(80, 82, 90) : Muted;
                    var trim = new StringFormat(center) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                    using (var b = new SolidBrush(Color.FromArgb((int)(sc.A * textA), sc)))
                        g.DrawString(SubLabel(targets[i]), segSubFont, b, new RectangleF(pt.X - boxW / 2, pt.Y + 3 * S, boxW, 16 * S), trim);
                }

                // 가운데 원 = 파일
                float rc = Rin - Gap;
                using (var b = new SolidBrush(CenterBg)) g.FillEllipse(b, c - rc, c - rc, 2 * rc, 2 * rc);
                using (var pen = new Pen(Border, 1.2f * S)) g.DrawEllipse(pen, c - rc, c - rc, 2 * rc, 2 * rc);
                if (textOK) DrawCenter(g, c, rc, center);

                if (Motion)
                {
                    // 대기 중: 바깥 테두리를 따라 도는 빛줄기 (꼬리가 점점 흐려짐)
                    if (state == State.Waiting || state == State.Ready)
                    {
                        float head = (float)((now * 110) % 360);
                        for (int k = 0; k < 7; k++)
                            using (var pen = new Pen(Color.FromArgb((int)(150 * (1 - k / 7f) * openP), 170, 195, 255), 2.4f * S))
                            {
                                pen.StartCap = pen.EndCap = LineCap.Round;
                                g.DrawArc(pen, c - R + S, c - R + S, 2 * R - 2 * S, 2 * R - 2 * S, head - k * 9f, 9f);
                            }
                    }
                    // 변환 중: 가운데 원을 도는 스피너
                    if (state == State.Busy)
                    {
                        float start = (float)((now * 320) % 360);
                        float sweep = 60 + 45 * (float)Math.Sin(now * 3.2);
                        using (var pen = new Pen(Accent, 3.2f * S))
                        {
                            pen.StartCap = pen.EndCap = LineCap.Round;
                            g.DrawArc(pen, c - rc, c - rc, 2 * rc, 2 * rc, start, sweep);
                        }
                    }
                    // 완료: 초록 물결이 바깥으로 퍼짐
                    if (state == State.Done)
                    {
                        float rp = Clamp01((now - tState) / 0.75);
                        if (rp < 1)
                        {
                            float rr = rc + (R + 16 * S - rc) * EaseOutCubic(rp);
                            using (var pen = new Pen(Color.FromArgb((int)(220 * (1 - rp)), Good), (5 * (1 - rp) + 1) * S))
                                g.DrawEllipse(pen, c - rr, c - rr, 2 * rr, 2 * rr);
                        }
                    }
                }
                g.ResetTransform();

                // 색종이 (원 밖으로도 날아감)
                foreach (Particle p in confetti)
                {
                    double age = now - p.Born;
                    int alpha = (int)(255 * Clamp01((p.Life - age) / 0.4));
                    var st = g.Save();
                    g.TranslateTransform(p.X, p.Y);
                    g.RotateTransform(p.Rot);
                    using (var b = new SolidBrush(Color.FromArgb(alpha, p.Col)))
                        g.FillRectangle(b, -p.W / 2, -p.H / 2, p.W, p.H);
                    g.Restore(st);
                }
            }
        }

        void DrawCenter(Graphics g, float c, float rc, StringFormat center)
        {
            float w = rc * 2 - 18 * S;
            var one = new StringFormat(center) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            var wrap = new StringFormat(center) { Trimming = StringTrimming.EllipsisCharacter };

            Action<string, Font, Color, float, float> line = (text, font, color, y, h) =>
            {
                using (var b = new SolidBrush(color)) g.DrawString(text, font, b, new RectangleF(c - w / 2, y, w, h), one);
            };
            Action<string, Color> mark = (text, color) =>
            {
                using (var b = new SolidBrush(color)) g.DrawString(text, markFont, b, new RectangleF(c - w / 2, c - 44 * S, w, 44 * S), center);
            };
            Action<float> icon = (y) =>
            {
                if (fileIcon == null) return;
                float s = 32 * S;
                g.DrawIcon(fileIcon, new Rectangle((int)(c - s / 2), (int)y, (int)s, (int)s));
            };

            string name = files.Count == 0 ? "" : Path.GetFileName(files[0]);
            switch (state)
            {
                case State.Waiting:
                    line("여기로", titleFont, Color.White, c - 20 * S, 20 * S);
                    line("끌어오세요", subFont, Muted, c + 1 * S, 18 * S);
                    break;

                case State.Ready:
                    icon(c - 40 * S);
                    if (hover >= 0)
                    {
                        line("→ " + targets[hover].ToUpperInvariant(), titleFont, Accent, c - 4 * S, 20 * S);
                        line(CenterLabel(targets[hover]), subFont, Muted, c + 15 * S, 18 * S);
                    }
                    else
                    {
                        line(name, titleFont, Color.White, c - 4 * S, 20 * S);
                        line(files.Count > 1 ? "외 " + (files.Count - 1) + "개" : "형식 위에 놓기", subFont, Muted, c + 15 * S, 18 * S);
                    }
                    break;

                case State.NoTargets:
                    mark("✕", Muted);
                    using (var b = new SolidBrush(Muted))
                        g.DrawString(msg, subFont, b, new RectangleF(c - w / 2, c + 2 * S, w, 34 * S), wrap);
                    break;

                case State.Busy:
                    icon(c - 44 * S);
                    line("변환 중…", titleFont, Color.White, c - 9 * S, 20 * S);
                    line(msg, subFont, Muted, c + 9 * S, 18 * S);
                    line("눌러서 취소", segSubFont, Color.FromArgb(120, 150, 230), c + 27 * S, 16 * S);
                    break;

                case State.Canceled:
                    mark("✕", Muted);
                    line("취소됨", titleFont, Color.White, c + 0 * S, 20 * S);
                    line(msg, subFont, Muted, c + 19 * S, 18 * S);
                    break;

                case State.Done:
                    DrawCheck(g, c, Motion ? EaseOutCubic(Clamp01((Now - tState - 0.05) / 0.32)) : 1);
                    line("완료", titleFont, Good, c + 0 * S, 20 * S);
                    line(msg, subFont, Muted, c + 18 * S, 18 * S);
                    line("눌러서 열기", segSubFont, Color.FromArgb(120, 200, 150), c + 35 * S, 16 * S);
                    break;

                case State.Error:
                    mark("✕", Bad);
                    using (var b = new SolidBrush(Color.FromArgb(235, 200, 200)))
                        g.DrawString(msg, subFont, b, new RectangleF(c - w / 2, c + 1 * S, w, 44 * S), wrap);
                    break;
            }
        }

        // 체크 표시를 획 순서대로 그려 나감 (p = 0~1)
        void DrawCheck(Graphics g, float c, float p)
        {
            if (p <= 0) return;
            var a = new PointF(c - 13 * S, c - 22 * S);
            var b = new PointF(c - 4 * S, c - 13 * S);
            var d = new PointF(c + 14 * S, c - 33 * S);
            float l1 = 9 * 1.414f, l2 = (float)Math.Sqrt(18 * 18 + 20 * 20), total = l1 + l2, len = total * p;
            using (var pen = new Pen(Good, 4.2f * S))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                if (len <= l1)
                {
                    float t = len / l1;
                    g.DrawLine(pen, a, new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
                }
                else
                {
                    float t = (len - l1) / l2;
                    g.DrawLines(pen, new[] { a, b, new PointF(b.X + (d.X - b.X) * t, b.Y + (d.Y - b.Y) * t) });
                }
            }
        }

        // ---------------- 애니메이션 미리보기 (테스트용) ----------------

        // DragConvert.exe --preview-anim <폴더> <파일...> : 열림·마우스 올림·완료 장면을 시간대별로 저장
        public static void SaveAnimFrames(string dir, string[] paths)
        {
            Directory.CreateDirectory(dir);
            using (var f = new PopupForm())
            {
                f.LoadFiles(paths.Where(File.Exists).ToList());
                f.state = f.lastState = State.Ready;
                f.fakeNow = 0; f.tShown = 0;
                int n = 0;
                Action<string> save = name =>
                {
                    using (Bitmap b = f.Render())
                    using (var bg = new Bitmap(b.Width, b.Height))
                    using (var g = Graphics.FromImage(bg))
                    {
                        g.Clear(Color.FromArgb(200, 210, 225));
                        g.DrawImage(b, 0, 0);
                        bg.Save(Path.Combine(dir, (n++).ToString("D2") + "_" + name + ".png"), ImageFormat.Png);
                    }
                };
                Action<double> advance = sec => { for (double t = 0; t < sec - 1e-9; t += 1 / 60.0) { f.fakeNow += 1 / 60.0; f.StepAnim(1 / 60.0); } };
                foreach (double t in new[] { 0.05, 0.12, 0.2, 0.3, 0.45 }) { advance(t - f.Now); save("open_" + (int)(t * 1000) + "ms"); }
                f.hover = Math.Min(2, f.targets.Count - 1);
                advance(0.06); save("hover_60ms");
                advance(0.3); save("hover_settled");
                f.state = State.Busy; f.msg = "MP4 · 장면 12/40"; f.Redraw_StateOnly();
                advance(0.2); save("busy");
                f.state = State.Done; f.msg = "anim.mp4"; f.Redraw_StateOnly();
                foreach (double t in new[] { 0.08, 0.2, 0.4, 0.75 }) { advance(t - (f.Now - f.tState)); save("done_" + (int)(t * 1000) + "ms"); }
                f.state = State.Error; f.msg = "테스트 오류 메시지"; f.Redraw_StateOnly();
                advance(0.03); save("error_shake");
            }
        }

        void Redraw_StateOnly() { if (state != lastState) { lastState = state; OnStateChanged(); } }

        // DragConvert.exe --demo <파일> : 실제 타이머로 화면 가운데에서 열림→칸 훑기→변환 중→완료→닫힘을 재생 (입력 없음)
        public static readonly List<double> FrameMs = new List<double>();
        static bool measureFrames; // 데모 모드에서만 장면 시간 기록 (평소에 쌓이면 메모리가 계속 늘어남)
        public static void RunDemo(string file)
        {
            measureFrames = true;
            var f = new PopupForm();
            var h = f.Handle;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            f.ShowAt(new Point(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2));
            f.LoadFiles(new List<string> { file });
            f.state = State.Ready;
            f.Redraw();
            var steps = new List<KeyValuePair<int, Action>>();
            int at = 600;
            for (int i = 0; i < f.targets.Count; i++) { int k = i; steps.Add(new KeyValuePair<int, Action>(at, () => { f.hover = k; f.StartAnim(); })); at += 170; }
            steps.Add(new KeyValuePair<int, Action>(at, () => { f.hover = 2; f.state = State.Busy; f.msg = "MP4 · 장면 5/40"; f.Redraw(); f.StartAnim(); })); at += 900;
            steps.Add(new KeyValuePair<int, Action>(at, () => { f.state = State.Done; f.msg = "데모.mp4"; f.Redraw(); f.StartAnim(); })); at += 1500;
            steps.Add(new KeyValuePair<int, Action>(at, () => f.Hide())); at += 400;
            steps.Add(new KeyValuePair<int, Action>(at, () => Application.ExitThread()));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tick = new System.Windows.Forms.Timer { Interval = 10 };
            int next = 0;
            tick.Tick += (s, e) => { while (next < steps.Count && sw.ElapsedMilliseconds >= steps[next].Key) steps[next++].Value(); };
            tick.Start();
            Application.Run();
        }

        // 놓은 파일들을 보고 칸(형식) 목록을 만든다. 종류가 섞여 있으면 파일마다 따로 계산해서 합침
        void LoadFiles(List<string> list)
        {
            files = list;
            hoverAmt = new float[0];
            if (fileIcon != null) { fileIcon.Dispose(); fileIcon = null; }
            try { fileIcon = Icon.ExtractAssociatedIcon(files[0]); } catch { }
            kind = Converter.KindOf(files[0]);
            targets = Converter.TargetsForAll(files, out support, out labelFile);
        }

        bool Partial(string fmt) { int n; return support.TryGetValue(fmt, out n) && n < files.Count; }

        // 칸 아래 작은 글씨: 일부 파일만 되는 형식이면 "2/3개"
        string SubLabel(string fmt)
        {
            if (Partial(fmt)) return support[fmt] + "/" + files.Count + "개";
            return BaseLabel(fmt);
        }

        // 가운데 원 아래 줄
        string CenterLabel(string fmt)
        {
            if (Partial(fmt)) return files.Count + "개 중 " + support[fmt] + "개만";
            return files.Count > 1 ? files.Count + "개 파일" : BaseLabel(fmt);
        }

        string BaseLabel(string fmt)
        {
            string ctx;
            if (!labelFile.TryGetValue(fmt, out ctx)) ctx = files.Count > 0 ? files[0] : null;
            Kind kind = ctx != null ? Converter.KindOf(ctx) : this.kind;
            if (kind == Kind.Image && fmt == "pdf") return files.Count(f => Converter.KindOf(f) == Kind.Image) > 1 ? "한 파일로 합치기" : "문서로";
            if (kind == Kind.Image && fmt == "mp4") return "영상 · 작은 용량";
            if (kind == Kind.Image && fmt == "webm") return "영상 · 웹용";
            if (kind == Kind.Document)
            {
                string grp = ctx != null ? Docs.GroupOf(ctx) : null;
                switch (fmt)
                {
                    case "pdf": return "공유 · 인쇄용";
                    case "docx": return "Word";
                    case "doc": return "Word 97-2003";
                    case "hwp": return "한글";
                    case "hwpx": return "한글 (신형식)";
                    case "odt": case "ods": case "odp": return "오픈오피스";
                    case "rtf": return "서식 텍스트";
                    case "txt": return "글자만";
                    case "xlsx": return "Excel";
                    case "xls": return "Excel 97-2003";
                    case "csv": return "첫 시트만";
                    case "pptx": return "PowerPoint";
                    case "ppt": return "PPT 97-2003";
                    case "png": case "jpg": return grp == "ppt" ? "슬라이드별" : "페이지별";
                }
            }
            if (kind == Kind.Video)
            {
                switch (fmt)
                {
                    case "mp4": return "범용";
                    case "mkv": return "빠름 · 무손실";
                    case "webm": return "웹용";
                    case "gif": return "움짤";
                    case "mp3": return "소리만";
                    case "wav": return "소리만";
                }
            }
            switch (fmt)
            {
                case "png": return "투명 유지";
                case "jpg": return "작은 용량";
                case "webp": return "웹용";
                case "bmp": return "무압축";
                case "gif": return "256색";
                case "tiff": return "인쇄용";
                case "ico": return "아이콘";
                case "mp3": return "범용";
                case "wav": return "무압축";
                case "flac": return "무손실";
                case "m4a": return "AAC";
                case "ogg": return "Vorbis";
            }
            return "";
        }

        // ---------------- 미리보기 (디자인 확인용) ----------------

        // DragConvert.exe --preview <out.png> <state> <hover> <파일...>
        public static void SavePreview(string outPng, string stateName, int hoverIndex, string[] paths)
        {
            using (var f = new PopupForm())
            {
                f.files = paths.Where(File.Exists).ToList();
                if (f.files.Count > 0)
                {
                    f.LoadFiles(f.files);
                }
                f.state = (State)Enum.Parse(typeof(State), stateName, true);
                f.hover = hoverIndex;
                if (f.state == State.Busy) f.msg = "PNG · 페이지 12/40";
                if (f.state == State.Canceled) f.msg = "저장 안 됨";
                if (f.state == State.Done) f.msg = "보고서.pdf";
                if (f.state == State.Error) f.msg = "Office가 '기본 프로그램으로 설정할까요?' 질문을 띄워서 변환이 취소됐어요";
                if (f.state == State.NoTargets) f.msg = "아직 지원하지 않는 형식이에요";
                using (Bitmap b = f.Render())
                {
                    // 투명 배경이 보이도록 바탕화면 비슷한 색 위에 합성해서 저장
                    using (var bg = new Bitmap(b.Width, b.Height))
                    using (var g = Graphics.FromImage(bg))
                    {
                        g.Clear(Color.FromArgb(200, 210, 225));
                        g.DrawImage(b, 0, 0);
                        bg.Save(outPng, ImageFormat.Png);
                    }
                }
            }
        }
    }
}
