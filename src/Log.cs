using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ShiftConvert
{
    // 문제 신고용 기록: %LOCALAPPDATA%\DragConvert\logs\dragconvert.log (1MB 넘으면 .old로 넘김)
    // 친구가 그대로 보내줄 수 있게 파일 이름은 기록하지 않는다 (확장자·크기만)
    static class Log
    {
        static readonly object gate = new object();
        const long MaxBytes = 1024 * 1024;

        public static event Action<string> Written;
        public static volatile bool Disabled; // 제거 중에는 기록 폴더를 다시 만들지 않게

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DragConvert", "logs"); }
        }

        public static string FilePath { get { return Path.Combine(Folder, "dragconvert.log"); } }

        public static void Info(string message) { Write("INFO ", message); }
        public static void Error(string message) { Write("ERROR", message); }

        static void Write(string level, string message)
        {
            if (Disabled) return;
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + level + " " + message.Replace("\r", " ").Replace("\n", " ");
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string old = FilePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, line + "\r\n", new UTF8Encoding(false));
                }
                catch { }
            }
            var h = Written;
            if (h != null) try { h(line); } catch { }
        }

        public static string[] Tail(int count)
        {
            lock (gate)
            {
                try
                {
                    if (!File.Exists(FilePath)) return new string[0];
                    string[] all = File.ReadAllLines(FilePath, Encoding.UTF8);
                    if (all.Length <= count) return all;
                    var tail = new string[count];
                    Array.Copy(all, all.Length - count, tail, 0, count);
                    return tail;
                }
                catch { return new string[0]; }
            }
        }

        // 기록용 파일 설명: "docx 12KB"
        public static string Describe(string path)
        {
            string ext = Converter.NormExt(path);
            try
            {
                long b = new FileInfo(path).Length;
                string size = b >= 1024 * 1024 ? (b / (1024 * 1024)) + "MB" : Math.Max(1, b / 1024) + "KB";
                return (ext.Length > 0 ? ext : "(확장자 없음)") + " " + size;
            }
            catch { return ext; }
        }
    }
}
