using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ShiftConvert
{
    // 설정: %APPDATA%\DragConvert\settings.ini (한 줄에 키=값)
    static class Settings
    {
        // 드래그 중에 누르면 원형 메뉴가 뜨는 키
        public class TriggerKey
        {
            public string Id, Label, Note;
            public string Obj; // 목적격 조사: "Shift를", "Ctrl을"
            public int Vk;
            public string LabelObj { get { return Label + Obj; } }
        }

        public static readonly TriggerKey[] Triggers =
        {
            new TriggerKey { Id = "shift", Vk = 0x10, Label = "Shift", Obj = "를", Note = "원 바깥에 놓으면 파일이 이동돼요 (Windows 기본 동작)" },
            new TriggerKey { Id = "space", Vk = 0x20, Label = "스페이스바", Obj = "를", Note = "탐색기의 다른 기능과 겹치지 않아요" },
            new TriggerKey { Id = "ctrl", Vk = 0x11, Label = "Ctrl", Obj = "을", Note = "원 바깥에 놓으면 파일이 복사돼요 (Windows 기본 동작)" },
            new TriggerKey { Id = "alt", Vk = 0x12, Label = "Alt", Obj = "를", Note = "원 바깥에 놓으면 바로가기가 만들어져요 (Windows 기본 동작)" },
        };

        public static string Trigger = "shift";
        public static bool Enabled = true;
        public static bool ShowWindowOnStart = true;
        public static bool CloseHintShown;

        public static TriggerKey CurrentTrigger
        {
            get
            {
                foreach (TriggerKey t in Triggers) if (t.Id == Trigger) return t;
                return Triggers[0];
            }
        }

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
                if (d.TryGetValue("trigger", out v)) Trigger = v;
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
                    "trigger=" + Trigger + "\r\n" +
                    "enabled=" + (Enabled ? "1" : "0") + "\r\n" +
                    "showWindowOnStart=" + (ShowWindowOnStart ? "1" : "0") + "\r\n" +
                    "closeHintShown=" + (CloseHintShown ? "1" : "0") + "\r\n",
                    new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Error("설정 저장 실패: " + ex.Message); }
        }
    }
}
