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
        readonly System.Windows.Forms.Timer fadeTimer = new System.Windows.Forms.Timer();
        int opacity = 255;

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
            fadeTimer.Interval = 15;
            fadeTimer.Tick += (s, e) =>
            {
                opacity = Math.Min(255, opacity + 60);
                if (opacity >= 255) fadeTimer.Stop();
                Redraw();
            };
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
            if (state == State.Busy) { Show(); BringToTop(); Redraw(); return; }
            state = State.Waiting;
            files.Clear(); targets.Clear(); hover = -1; msg = "";
            if (fileIcon != null) { fileIcon.Dispose(); fileIcon = null; }
            // 원의 중심이 커서 → 살짝만 움직여도 DragEnter가 와서 파일 종류를 알 수 있고, 바깥 칸으로 밀어 놓으면 끝
            Location = Clamp(new Point(cursor.X - Dim / 2, cursor.Y - Dim / 2), cursor);
            opacity = 0;
            Redraw();
            Show();
            BringToTop();
            fadeTimer.Start();
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
            if (h != hover) { hover = h; Redraw(); }
            // 항상 "복사"로만 받는다. Shift 때문에 "이동"이 되면 탐색기가 원본을 지울 수 있음
            e.Effect = (h >= 0 && (e.AllowedEffect & DragDropEffects.Copy) != 0) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            if (hover != -1) { hover = -1; Redraw(); }
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
            if (!Visible) { opacity = 255; Show(); }
            Redraw();
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
            if (!IsHandleCreated) return;
            using (Bitmap bmp = Render())
            {
                IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
                IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
                try
                {
                    var size = new SIZE { cx = Dim, cy = Dim };
                    var src = new Native.POINT();
                    var dst = new Native.POINT { X = Left, Y = Top };
                    var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = (byte)opacity, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
                    UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
                }
                finally
                {
                    SelectObject(mem, old);
                    DeleteObject(hbmp);
                    DeleteDC(mem);
                    ReleaseDC(IntPtr.Zero, screen);
                }
            }
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

        Bitmap Render()
        {
            var bmp = new Bitmap(Dim, Dim, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                float c = Dim / 2f;

                // 그림자
                using (var path = new GraphicsPath())
                {
                    float sr = Dim / 2f - 1;
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
                    float a0 = -90 + i * step;
                    float r1 = hot ? R + Grow : R;
                    Color bg = hot ? (state == State.Done ? Good : state == State.Error ? Bad : Accent) : dim ? SegDim : SegBg;
                    using (var path = Segment(c, Rin + Gap * 0.5f, r1, a0, step, Gap))
                    using (var b = new SolidBrush(bg))
                        g.FillPath(b, path);

                    float mid = (Rin + r1) / 2 + 4 * S;
                    PointF pt = Polar(c, mid, a0 + step / 2);
                    Color fg = dim && !hot ? Color.FromArgb(95, 97, 106) : Color.White;
                    float boxW = Math.Min(110 * S, (float)(2 * Math.PI * mid / n) - 8 * S);
                    // 칸이 많으면 글자를 줄이고, 줄바꿈 없이 전부 보이게 (안 하면 DOCX가 DOC로 잘림)
                    Font ef = n <= 8 ? extFont : n <= 10 ? extFontMid : extFontSmall;
                    var noWrap = new StringFormat(center) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip };
                    using (var b = new SolidBrush(fg))
                        g.DrawString(targets[i].ToUpperInvariant(), ef, b, new RectangleF(pt.X - boxW / 2, pt.Y - 22 * S, boxW, 26 * S), noWrap);
                    Color sc = hot ? Color.FromArgb(228, 234, 255) : dim ? Color.FromArgb(80, 82, 90) : Muted;
                    var trim = new StringFormat(center) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                    using (var b = new SolidBrush(sc))
                        g.DrawString(SubLabel(targets[i]), segSubFont, b, new RectangleF(pt.X - boxW / 2, pt.Y + 3 * S, boxW, 16 * S), trim);
                }

                // 가운데 원 = 파일
                float rc = Rin - Gap;
                using (var b = new SolidBrush(CenterBg)) g.FillEllipse(b, c - rc, c - rc, 2 * rc, 2 * rc);
                using (var pen = new Pen(Border, 1.2f * S)) g.DrawEllipse(pen, c - rc, c - rc, 2 * rc, 2 * rc);
                DrawCenter(g, c, rc, center);
            }
            return bmp;
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
                    mark("✓", Good);
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

        // 놓은 파일들을 보고 칸(형식) 목록을 만든다. 종류가 섞여 있으면 파일마다 따로 계산해서 합침
        void LoadFiles(List<string> list)
        {
            files = list;
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
