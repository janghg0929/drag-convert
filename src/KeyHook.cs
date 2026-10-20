using System;
using System.Runtime.InteropServices;

namespace ShiftConvert
{
    // 키·마우스 버튼 이름과 지정 가능 여부
    static class KeyNames
    {
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        public static bool IsMouse(int vk) { return vk == 0x04 || vk == 0x05 || vk == 0x06; }
        public static bool IsDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        // 왼쪽/오른쪽 Shift·Ctrl·Alt를 하나로
        public static int Normalize(int vk)
        {
            switch (vk)
            {
                case 0xA0: case 0xA1: return 0x10;
                case 0xA2: case 0xA3: return 0x11;
                case 0xA4: case 0xA5: return 0x12;
            }
            return vk;
        }

        // 지정할 수 없으면 이유, 괜찮으면 null
        public static string ForbiddenReason(int vk)
        {
            switch (vk)
            {
                case 0x01: case 0x02: return "마우스 왼쪽·오른쪽 버튼은 끌기에 쓰여서 지정할 수 없어요";
                case 0x1B: return "Esc는 끌기를 취소하는 키라서 지정할 수 없어요";
                case 0x2E: return "Delete는 만에 하나 탐색기에 전달되면 파일이 지워질 수 있어서 지정할 수 없어요";
                case 0x0D: return "Enter는 만에 하나 탐색기에 전달되면 파일이 열릴 수 있어서 지정할 수 없어요";
                case 0x5B: case 0x5C: return "Windows 키는 지정할 수 없어요";
                case 0x5D: return "메뉴 키는 지정할 수 없어요";
                case 0x2C: return "Print Screen은 지정할 수 없어요";
            }
            if (vk <= 0 || vk > 0xFE) return "지정할 수 없는 키예요";
            return null;
        }

        public static string Name(int vk)
        {
            switch (vk)
            {
                case 0x10: return "Shift";
                case 0x11: return "Ctrl";
                case 0x12: return "Alt";
                case 0x20: return "스페이스바";
                case 0x14: return "Caps Lock";
                case 0x09: return "Tab";
                case 0x08: return "Backspace";
                case 0x04: return "마우스 가운데 버튼";
                case 0x05: return "마우스 뒤로 버튼";
                case 0x06: return "마우스 앞으로 버튼";
                case 0x2D: return "Insert";
                case 0x24: return "Home";
                case 0x23: return "End";
                case 0x21: return "Page Up";
                case 0x22: return "Page Down";
                case 0x25: return "←";
                case 0x26: return "↑";
                case 0x27: return "→";
                case 0x28: return "↓";
                case 0x13: return "Pause";
                case 0x91: return "Scroll Lock";
                case 0x90: return "Num Lock";
                case 0x15: return "한/영";
                case 0x19: return "한자";
                case 0x6A: return "숫자패드 *";
                case 0x6B: return "숫자패드 +";
                case 0x6D: return "숫자패드 -";
                case 0x6E: return "숫자패드 .";
                case 0x6F: return "숫자패드 /";
            }
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            if (vk >= 0x60 && vk <= 0x69) return "숫자패드 " + (vk - 0x60);
            if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A)) return ((char)vk).ToString();
            uint ch = MapVirtualKey((uint)vk, 2) & 0x7FFF; // 그 밖의 기호 키는 자판 글자로
            if (ch > 32) return ((char)ch).ToString();
            return "키 " + vk.ToString("X2");
        }

        // 문장에 넣을 형태: "Shift 키를", "마우스 뒤로 버튼을"
        public static string Phrase(int vk) { return IsMouse(vk) ? Name(vk) + "을" : Name(vk) + " 키를"; }
    }

    // 탐색기에서 파일을 끄는 동안에만 지정한 키(또는 마우스 버튼)를 가로채서 탐색기에 전달되지 않게 한다.
    // → 글자 키로 선택이 바뀌거나, Shift 때문에 원 바깥에 놓을 때 파일이 "이동"되는 일이 없다.
    // 끌기를 시작할 때 켜고 마우스를 떼면 바로 끈다 (평소에는 키 입력에 전혀 관여하지 않음).
    sealed class DragHook
    {
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }

        const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        const int WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C;

        readonly HookProc proc; // 콜백이 GC로 사라지지 않게 붙잡아 둠
        IntPtr handle = IntPtr.Zero;
        int vk;
        bool swallowedDown;  // 이번 눌림을 우리가 가로챘는지 (뗌도 짝으로 가로채야 키가 "눌린 채"로 남지 않음)
        bool passThrough;    // 가로채기 시작 때 이미 눌려 있던 키는 뗄 때까지 그대로 통과

        public bool TriggerDown { get; private set; }
        public bool Active { get { return handle != IntPtr.Zero; } }

        public DragHook() { proc = Callback; }

        public bool Start(int triggerVk)
        {
            Stop();
            vk = triggerVk;
            passThrough = KeyNames.IsDown(vk);
            TriggerDown = passThrough;
            handle = SetWindowsHookEx(KeyNames.IsMouse(vk) ? WH_MOUSE_LL : WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            if (handle == IntPtr.Zero) Log.Error("키 가로채기를 시작하지 못했어요 (오류 " + Marshal.GetLastWin32Error() + ") — 키는 탐색기에도 전달돼요");
            return Active;
        }

        public void Stop()
        {
            if (handle != IntPtr.Zero) { UnhookWindowsHookEx(handle); handle = IntPtr.Zero; }
            TriggerDown = false;
            swallowedDown = false;
            passThrough = false;
        }

        IntPtr Callback(int code, IntPtr w, IntPtr l)
        {
            try
            {
                if (code >= 0)
                {
                    int msg = w.ToInt32();
                    if (KeyNames.IsMouse(vk))
                    {
                        int btn = 0;
                        bool down = false;
                        if (msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP) { btn = 0x04; down = msg == WM_MBUTTONDOWN; }
                        else if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
                        {
                            var m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(MSLLHOOKSTRUCT));
                            btn = (m.mouseData >> 16) == 1 ? 0x05 : 0x06;
                            down = msg == WM_XBUTTONDOWN;
                        }
                        if (btn == vk && Eat(down)) return (IntPtr)1;
                    }
                    else
                    {
                        var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(KBDLLHOOKSTRUCT));
                        if (KeyNames.Normalize((int)k.vkCode) == vk && Eat(msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)) return (IntPtr)1;
                    }
                }
            }
            catch { }
            return CallNextHookEx(handle, code, w, l);
        }

        // true = 이 입력을 삼킴
        bool Eat(bool down)
        {
            if (passThrough)
            {
                TriggerDown = down;
                if (!down) passThrough = false;
                return false;
            }
            if (down) { TriggerDown = true; swallowedDown = true; return true; }
            TriggerDown = false;
            if (swallowedDown) { swallowedDown = false; return true; }
            return false;
        }
    }
}
