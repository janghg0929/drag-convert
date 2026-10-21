// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 janghg0929 — 끌어서 변환 (https://github.com/janghg0929/drag-convert)

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace ShiftConvert
{
    // 자동 업데이트.
    // GitHub 릴리스에서 최신 버전을 확인하고, 개발자 비밀키로 만든 서명이 맞을 때만 받아들인다.
    // → GitHub 계정이 털려도 비밀키 없이는 친구들 PC에 가짜 업데이트를 넣을 수 없다.
    // 서명 대상: "DragConvert-update|버전|exe의 SHA-256"  (옛 버전을 새 버전인 척 다시 올리는 것도 막음)
    static class Updater
    {
        // 업데이트를 받을 GitHub 저장소 ("아이디/저장소 이름"). 비어 있으면 업데이트 기능이 꺼진다
        public const string Repo = "janghg0929/drag-convert";

        // 서명 검증용 공개키. 짝이 되는 비밀키는 개발자 PC(%USERPROFILE%\.dragconvert-signing)에만 있다
        const string PublicKeyXml = "<RSAKeyValue><Modulus>uXfPKWQqp+06rSq3wtS4r7cBFB357ETkKe1FdjKKr6CVgQDDDjPkkzlNFji6Kgl6arHCFonL7tprxmmpOTuBY3eJP3cJide4m1wd6K19bFYdHqNtWFueUx3PERYgbCCIZvttKsYeh6TGSy4eMEAN3JQV3qPx68knRhZqmfOJ7IG9lDugv+jU29RdTxgzWhg5A/JeYXPq3CZkvvfEtJgHv+91bq2Hu++cIpggA9Lvc/PODXlO4ksKdmOGCHpv8n5mKD6CfT8UrWVz2E/VExXZMnaa6QRRMLPQ+gzjC1iMwc7gjQ1dxW0gMxa3nkyR2ZB2llFLWlIYW/h+XVxIvcUA+gfkUDb+fn/3NCXWoJQj4NJ/N3VoEdy9lhVzZwdo32CZm/QWIZA7BzOpfPChv/9TH9D9XKUT3UcDzcz11lVjIH8yl0YhgIV920IebGX7vvy1zVkIeWHwJcmV5yp8OkQxcsekJ2v0QcLz0bNy7/BqdY/85Zt0Wc512WjMnGKnM1n5</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        const string SignPrefix = "DragConvert-update|";

        public class Ready { public string Version, Notes, ExePath; }

        // 테스트용으로만 쓰는 피드 주소 바꾸기 (서명 검증은 그대로라 바꿔도 가짜 업데이트는 못 넣음)
        static string FeedUrl
        {
            get
            {
                string o = Environment.GetEnvironmentVariable("DRAGCONVERT_UPDATE_FEED");
                if (!string.IsNullOrEmpty(o)) return o;
                return Repo.Length == 0 ? null : "https://api.github.com/repos/" + Repo + "/releases/latest";
            }
        }

        public static bool Configured { get { return FeedUrl != null && PublicKeyXml.StartsWith("<RSAKeyValue>"); } }

        public static string DownloadDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DragConvert", "updates"); }
        }

        // 새 버전이 있으면 내려받아 서명까지 확인하고 돌려준다. 최신이면 null, 실패하면 null + err
        public static Ready CheckAndDownload(out string err)
        {
            err = null;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; // GitHub는 TLS 1.2 이상만 받음
                var js = new JavaScriptSerializer();
                var rel = js.DeserializeObject(Encoding.UTF8.GetString(Fetch(FeedUrl))) as Dictionary<string, object>;
                if (rel == null || !rel.ContainsKey("tag_name")) { err = "릴리스 정보를 읽지 못했어요"; return null; }

                string version = ((string)rel["tag_name"]).TrimStart('v', 'V');
                if (!IsNewer(version)) return null;

                string exeUrl = null, sigUrl = null;
                var assets = rel.ContainsKey("assets") ? rel["assets"] as object[] : null;
                if (assets != null)
                    foreach (object o in assets)
                    {
                        var a = o as Dictionary<string, object>;
                        if (a == null) continue;
                        string name = a["name"] as string, url = a["browser_download_url"] as string;
                        if (name == "DragConvert.exe") exeUrl = url;
                        if (name == "DragConvert.exe.sig") sigUrl = url;
                    }
                if (exeUrl == null || sigUrl == null) { err = "릴리스에 exe나 서명 파일이 없어요"; return null; }

                byte[] exe = Fetch(exeUrl);
                string sig = Encoding.UTF8.GetString(Fetch(sigUrl));
                Verify(exe, sig, version); // 서명이 틀리면 여기서 예외

                Directory.CreateDirectory(DownloadDir);
                string path = Path.Combine(DownloadDir, "DragConvert-" + version + ".exe");
                File.WriteAllBytes(path, exe);
                string notes = rel.ContainsKey("body") ? rel["body"] as string : null;
                return new Ready { Version = version, Notes = notes ?? "", ExePath = path };
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return null;
            }
        }

        public static bool IsNewer(string version)
        {
            Version v;
            return System.Version.TryParse(version, out v) && v > System.Version.Parse(AppVersion.Current);
        }

        // .sig 파일: 1행 = 버전, 2행 = base64 서명
        public static void Verify(byte[] exe, string sigText, string expectVersion)
        {
            string[] lines = sigText.Replace("\r", "").Split('\n');
            if (lines.Length < 2) throw new Exception("서명 파일 형식이 잘못됐어요");
            string version = lines[0].Trim();
            if (version != expectVersion) throw new Exception("서명된 버전(" + version + ")이 릴리스 버전(" + expectVersion + ")과 달라요");
            byte[] sig = Convert.FromBase64String(lines[1].Trim());

            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(exe)).Replace("-", "").ToLowerInvariant();
            byte[] payload = Encoding.UTF8.GetBytes(SignPrefix + version + "|" + hash);

            using (var rsa = new RSACng())
            {
                rsa.FromXmlString(PublicKeyXml);
                if (!rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    throw new Exception("서명이 맞지 않아서 업데이트를 거부했어요");
            }
        }

        static byte[] Fetch(string url)
        {
            using (var wc = new WebClient())
            {
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "DragConvert/" + AppVersion.Current; // GitHub API는 User-Agent 필수
                    wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json, application/octet-stream";
                }
                return wc.DownloadData(url);
            }
        }

        public static void CleanDownloads()
        {
            try { if (Directory.Exists(DownloadDir)) Directory.Delete(DownloadDir, true); } catch { }
        }
    }
}
