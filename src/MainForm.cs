using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ShiftConvert
{
    // 실행하면 뜨는 메인 창: 사용법, 단축키, 시작 설정, 업데이트, 기록(로그), 제거·종료.
    // X를 누르면 창만 숨고 트레이에서 계속 실행된다.
    class MainForm : Form
    {
        static readonly Color Bg = Color.FromArgb(24, 25, 30);
        static readonly Color Card = Color.FromArgb(34, 35, 42);
        static readonly Color ButtonBg = Color.FromArgb(52, 54, 64);
        static readonly Color Fg = Color.FromArgb(235, 237, 242);
        static readonly Color Muted = Color.FromArgb(150, 153, 166);
        static readonly Color Accent = Color.FromArgb(76, 125, 255);
        static readonly Color Good = Color.FromArgb(90, 205, 130);
        static readonly Color Warn = Color.FromArgb(240, 170, 80);
        static readonly Color Danger = Color.FromArgb(235, 110, 110);

        readonly TrayApp app;
        readonly float S;
        readonly int cardW;
        readonly Font bold, title, small, mono;

        Label statusText, howTo, updateText;
        CheckBox enabledBox, autoStartBox, showOnStartBox;
        Label keyLabel, keyNote;
        Button keyBtn;
        readonly Timer captureTimer = new Timer { Interval = 15 };
        bool capturing;
        bool[] downAtCaptureStart;
        Button updateBtn;
        TextBox logBox;
        bool loading;
        readonly Action<string> onLog;

        public MainForm(TrayApp app)
        {
            this.app = app;
            Text = "끌어서 변환";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Fg;
            Font = new Font("Malgun Gothic", 9.5f);
            bold = new Font("Malgun Gothic", 10.5f, FontStyle.Bold);
            title = new Font("Malgun Gothic", 17f, FontStyle.Bold);
            small = new Font("Malgun Gothic", 8.5f);
            mono = new Font("Consolas", 8.5f);
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f;

            int workH = Screen.FromPoint(Cursor.Position).WorkingArea.Height;
            ClientSize = new Size(Px(560), Math.Min(Px(720), workH - Px(80)));
            cardW = ClientSize.Width - Px(40) - SystemInformation.VerticalScrollBarWidth;

            var root = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
                Padding = new Padding(Px(20), Px(16), Px(10), Px(16)), BackColor = Bg
            };
            Controls.Add(root);

            // ---- 머리글
            var head = Stack(root, Bg, new Padding(0, 0, 0, Px(10)));
            var titleRow = Row(head);
            titleRow.Controls.Add(new Label { Text = "끌어서 변환", Font = title, AutoSize = true, ForeColor = Fg, Margin = new Padding(0, 0, Px(8), 0) });
            titleRow.Controls.Add(new Label { Text = "버전 " + AppVersion.Current, Font = small, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, Px(14), 0, 0) });
            statusText = Para(head, "", Good, Font);
            enabledBox = Check(head, "사용하기 (끄면 원형 메뉴가 뜨지 않아요)");
            enabledBox.CheckedChanged += (s, e) => { if (!loading) app.SetEnabled(enabledBox.Checked); };

            // ---- 관리자 권한 경고
            if (app.Elevated)
            {
                var warn = CardBox(root, "⚠ 관리자 권한으로 실행 중이에요");
                Para(warn, "이 상태에서는 Windows 보안 규칙 때문에 탐색기에서 원형 메뉴로 파일을 끌어 놓을 수 없어요. 일반 권한으로 다시 시작해 주세요.", Warn, Font);
                var b = Btn(warn, "일반 권한으로 다시 시작", Accent);
                b.Click += (s, e) => app.RelaunchUnelevated();
            }

            // ---- 사용법
            var how = CardBox(root, "사용법");
            howTo = Para(how, "", Fg, Font);
            Para(how, "· 원의 가운데에 놓으면 아무 일도 하지 않아요 (취소)\n· 변환 중에 가운데를 누르면 변환을 멈춰요\n· 완료 표시를 누르면 새 파일이 있는 폴더가 열려요\n· 원본 파일은 그대로 남고, 새 파일이 같은 폴더에 생겨요", Muted, small);

            // ---- 단축키: '키 변경'을 누르고 원하는 키(또는 마우스 가운데·옆 버튼)를 누르면 그 키로 바뀜
            var keys = CardBox(root, "원형 메뉴를 띄우는 키");
            Para(keys, "파일을 끄는 도중에 이 키를 누르면 원형 메뉴가 떠요. 키보드 키 하나나 마우스 가운데·옆 버튼을 쓸 수 있어요.", Muted, small);
            keyLabel = new Label
            {
                AutoSize = true, Font = new Font("Malgun Gothic", 13f, FontStyle.Bold), ForeColor = Fg, BackColor = ButtonBg,
                Padding = new Padding(Px(14), Px(5), Px(14), Px(5)), Margin = new Padding(0, Px(8), 0, Px(2))
            };
            keys.Controls.Add(keyLabel);
            var keyRow = Row(keys);
            keyBtn = Btn(keyRow, "키 변경", Accent);
            keyBtn.Click += (s, e) => { if (capturing) StopCapture("바꾸지 않았어요", Muted); else StartCapture(); };
            Btn(keyRow, "기본값(Shift)으로", ButtonBg).Click += (s, e) =>
            {
                StopCapture(null, Muted);
                app.SetTrigger(Settings.DefaultTriggerVk);
                ShowKeyNote("Shift 키로 되돌렸어요", Good);
            };
            keyNote = Para(keys, "", Muted, small);
            Para(keys, "끄는 동안에는 이 키를 이 프로그램이 먼저 받아서 탐색기에는 전달되지 않아요. 평소에는 원래대로 동작해요. (Esc·Delete·Enter·Windows 키는 지정할 수 없어요)", Muted, small);
            captureTimer.Tick += (s, e) => CaptureTick();
            KeyPreview = true;

            // ---- 시작 설정
            var start = CardBox(root, "시작 설정");
            autoStartBox = Check(start, "Windows를 켤 때 자동으로 실행");
            autoStartBox.CheckedChanged += (s, e) => { if (!loading) app.SetAutoStart(autoStartBox.Checked); };
            showOnStartBox = Check(start, "직접 실행할 때 이 창 열기 (자동 실행 때는 열리지 않아요)");
            showOnStartBox.CheckedChanged += (s, e) => { if (!loading) { Settings.ShowWindowOnStart = showOnStartBox.Checked; Settings.Save(); } };

            // ---- 업데이트
            var upd = CardBox(root, "업데이트");
            updateText = Para(upd, "", Muted, Font);
            updateBtn = Btn(upd, "업데이트 확인", ButtonBg);
            updateBtn.Click += (s, e) => { if (app.HasPendingUpdate) app.PromptUpdate(); else app.CheckForUpdate(true); };

            // ---- 기록
            var log = CardBox(root, "기록 (문제가 생기면 이 내용을 보내 주세요)");
            Para(log, "파일 이름은 기록하지 않고 종류와 크기만 남겨요.", Muted, small);
            logBox = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false,
                BackColor = Color.FromArgb(20, 21, 25), ForeColor = Color.FromArgb(200, 203, 214), BorderStyle = BorderStyle.None,
                Font = mono, Width = cardW - Px(32), Height = Px(150), Margin = new Padding(0, Px(6), 0, Px(6))
            };
            log.Controls.Add(logBox);
            var logRow = Row(log);
            Btn(logRow, "전체 복사", ButtonBg).Click += (s, e) =>
            {
                try { Clipboard.SetText(string.Join("\r\n", Log.Tail(500))); app.Toast("기록을 복사했어요", "메신저 등에 붙여넣어 보내 주세요."); } catch { }
            };
            Btn(logRow, "기록 폴더 열기", ButtonBg).Click += (s, e) =>
            {
                try { System.IO.Directory.CreateDirectory(Log.Folder); System.Diagnostics.Process.Start("explorer.exe", "\"" + Log.Folder + "\""); } catch { }
            };

            // ---- 아래 버튼
            var bottom = Row(root);
            bottom.Margin = new Padding(0, Px(4), 0, Px(28)); // 스크롤 끝에서 버튼이 잘리지 않게
            Btn(bottom, "프로그램 제거", Color.FromArgb(70, 40, 44)).Click += (s, e) => app.Uninstall();
            Btn(bottom, "종료", ButtonBg).Click += (s, e) => app.Quit();

            onLog = line =>
            {
                if (!IsHandleCreated || IsDisposed) return;
                try { BeginInvoke((Action)(() => { logBox.AppendText(line + "\r\n"); })); } catch { }
            };
            Log.Written += onLog;
            RefreshAll();
        }

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr h, string app, string idList);

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 스크롤바를 어두운 테마로 (Windows 10 1809+/11)
            foreach (Control c in new Control[] { Controls[0], logBox })
                try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
            // 기록은 최신 줄이 보이게
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 제목 표시줄도 어둡게 (Windows 10 2004+/11)
            try { int on = 1; Native.DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !app.Exiting)
            {
                e.Cancel = true;
                Hide();
                app.OnMainWindowHidden();
                return;
            }
            Log.Written -= onLog;
            base.OnFormClosing(e);
        }

        public void RefreshAll()
        {
            loading = true;
            string key = Settings.TriggerPhrase;
            statusText.Text = Settings.Enabled ? "● 켜져 있어요 — 탐색기에서 파일을 끌다가 " + key + " 누르세요" : "○ 꺼져 있어요";
            statusText.ForeColor = Settings.Enabled ? Good : Muted;
            enabledBox.Checked = Settings.Enabled;
            howTo.Text = "1. 탐색기나 바탕화면에서 파일을 마우스로 끌기 시작해요\n2. 끄는 도중에 " + key + " 누르면 커서 주위에 원형 메뉴가 떠요\n3. 원하는 형식 칸에 놓으면 변환돼요";
            keyLabel.Text = Settings.TriggerName;
            autoStartBox.Checked = app.IsAutoStart();
            showOnStartBox.Checked = Settings.ShowWindowOnStart;
            updateText.Text = app.UpdateStatus;
            updateBtn.Text = app.HasPendingUpdate ? "업데이트 설치" : "업데이트 확인";
            updateBtn.Enabled = Updater.Configured;
            updateBtn.BackColor = app.HasPendingUpdate ? Accent : ButtonBg;
            logBox.Text = string.Join("\r\n", Log.Tail(200)) + "\r\n";
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
            loading = false;
        }

        // ---------------- 키 변경 ----------------

        void StartCapture()
        {
            capturing = true;
            // 지금 이미 눌려 있는 키(방금 누른 마우스 버튼 등)는 무시하고, 새로 눌리는 것만 받음
            downAtCaptureStart = new bool[256];
            for (int v = 1; v < 256; v++) downAtCaptureStart[v] = KeyNames.IsDown(v);
            keyBtn.Text = "원하는 키를 누르세요… (Esc: 취소)";
            keyBtn.BackColor = Warn;
            keyNote.Text = "";
            ActiveControl = null; // 스페이스바·Enter가 버튼을 누르지 않게
            captureTimer.Start();
        }

        void CaptureTick()
        {
            for (int v = 3; v < 256; v++)
            {
                if (v >= 0xA0 && v <= 0xA5) continue; // 왼쪽/오른쪽 Shift·Ctrl·Alt는 0x10~0x12로 받음
                bool down = KeyNames.IsDown(v);
                if (!down) { downAtCaptureStart[v] = false; continue; }
                if (downAtCaptureStart[v]) continue;

                if (v == 0x1B) { StopCapture("바꾸지 않았어요", Muted); return; }
                string why = KeyNames.ForbiddenReason(v);
                if (why != null) { StopCapture(why, Warn); return; }
                StopCapture(null, Muted);
                app.SetTrigger(v);
                ShowKeyNote(KeyNames.Name(v) + (KeyNames.IsMouse(v) ? "으로" : " 키로") + " 바꿨어요", Good);
                return;
            }
        }

        void StopCapture(string message, Color color)
        {
            capturing = false;
            captureTimer.Stop();
            keyBtn.Text = "키 변경";
            keyBtn.BackColor = Accent;
            if (message != null) ShowKeyNote(message, color);
        }

        void ShowKeyNote(string message, Color color)
        {
            keyNote.Text = message;
            keyNote.ForeColor = color;
        }

        // 키를 고르는 동안 눌린 키가 창의 버튼 등을 작동시키지 않게
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (capturing) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (capturing) { e.Handled = true; e.SuppressKeyPress = true; return; }
            base.OnKeyDown(e);
        }

        // ---------------- 레이아웃 도우미 ----------------

        int Px(float v) { return (int)Math.Round(v * S); }

        FlowLayoutPanel Stack(Control parent, Color bg, Padding margin)
        {
            var p = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = bg, Margin = margin, MinimumSize = new Size(cardW, 0), MaximumSize = new Size(cardW, 0)
            };
            parent.Controls.Add(p);
            return p;
        }

        FlowLayoutPanel CardBox(Control parent, string heading)
        {
            var p = Stack(parent, Card, new Padding(0, 0, 0, Px(12)));
            p.Padding = new Padding(Px(16), Px(12), Px(16), Px(14));
            p.Controls.Add(new Label { Text = heading, Font = bold, AutoSize = true, ForeColor = Fg, Margin = new Padding(0, 0, 0, Px(4)) });
            return p;
        }

        FlowLayoutPanel Row(Control parent)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, BackColor = parent.BackColor, Margin = new Padding(0) };
            parent.Controls.Add(p);
            return p;
        }

        Label Para(Control parent, string text, Color color, Font font)
        {
            var l = new Label
            {
                Text = text, AutoSize = true, ForeColor = color, Font = font,
                MaximumSize = new Size(cardW - Px(32), 0), Margin = new Padding(0, Px(2), 0, Px(2))
            };
            parent.Controls.Add(l);
            return l;
        }

        CheckBox Check(Control parent, string text)
        {
            var c = new CheckBox { Text = text, AutoSize = true, ForeColor = Fg, Margin = new Padding(0, Px(6), 0, 0) };
            parent.Controls.Add(c);
            return c;
        }

        Button Btn(Control parent, string text, Color bg)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = Fg, Cursor = Cursors.Hand,
                Padding = new Padding(Px(10), Px(3), Px(10), Px(3)), Margin = new Padding(0, Px(6), Px(8), 0)
            };
            b.FlatAppearance.BorderSize = 0;
            parent.Controls.Add(b);
            return b;
        }
    }
}
