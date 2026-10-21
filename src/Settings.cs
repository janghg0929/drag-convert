// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 janghg0929 — 끌어서 변환 (https://github.com/janghg0929/drag-convert)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ShiftConvert
{
    // 설정: %APPDATA%\DragConvert\settings.ini (한 줄에 키=값)
    static class Settings
    {
        // 드래그 중에 누르면 원형 메뉴가 뜨는 키 (가상 키 코드). 마우스 가운데·옆 버튼도 가능
        public const int DefaultTriggerVk = 0x10; // Shift
        public static int TriggerVk = DefaultTriggerVk;
        public static bool Enabled = true;
        public static bool ShowWindowOnStart = true;
        public static bool CloseHintShown;

        public static string TriggerName { get { return KeyNames.Name(TriggerVk); } }
        public static string TriggerPhrase { get { return KeyNames.Phrase(TriggerVk); } } // "Shift 키를", "마우스 뒤로 버튼을"

        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DragConvert", "settings.ini"); }
        }

        public static string Folder { get { return Path.GetDirectoryName(FilePath); } }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var d = new Dictionary<string, string>();
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                string v;
                int vk;
                if (d.TryGetValue("triggerVk", out v) && int.TryParse(v, out vk) && KeyNames.ForbiddenReason(vk) == null) TriggerVk = vk;
                else if (d.TryGetValue("trigger", out v))
                {
                    // 예전 형식(이름으로 저장) 호환
                    switch (v)
                    {
                        case "space": TriggerVk = 0x20; break;
                        case "ctrl": TriggerVk = 0x11; break;
                        case "alt": TriggerVk = 0x12; break;
                        default: TriggerVk = 0x10; break;
                    }
                }
                if (d.TryGetValue("enabled", out v)) Enabled = v != "0";
                if (d.TryGetValue("showWindowOnStart", out v)) ShowWindowOnStart = v != "0";
                if (d.TryGetValue("closeHintShown", out v)) CloseHintShown = v == "1";
            }
            catch (Exception ex) { Log.Error("설정 읽기 실패: " + ex.Message); }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath,
                    "triggerVk=" + TriggerVk + "\r\n" +
                    "enabled=" + (Enabled ? "1" : "0") + "\r\n" +
                    "showWindowOnStart=" + (ShowWindowOnStart ? "1" : "0") + "\r\n" +
                    "closeHintShown=" + (CloseHintShown ? "1" : "0") + "\r\n",
                    new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Error("설정 저장 실패: " + ex.Message); }
        }
    }
}
