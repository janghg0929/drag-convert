// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 janghg0929 — 끌어서 변환 (https://github.com/janghg0929/drag-convert)

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Text;
using System.Xml;

namespace ShiftConvert
{
    // 마크다운(.md) 만들기.
    //  - docx: Office 없이 파일 구조(XML)를 직접 읽음 → 제목, 굵게/기울임/취소선, 목록, 표, 링크, 그림
    //  - 표 데이터(CSV·Excel 시트·슬라이드 표)는 마크다운 표로
    // 그림은 md 옆 "<이름>_images" 폴더에 저장하고 상대 경로로 연결한다.
    static class MarkdownExport
    {
        // ---------------- 공통 ----------------

        public static void Save(string path, string markdown)
        {
            File.WriteAllText(path, markdown.TrimEnd() + "\n", new UTF8Encoding(false));
        }

        // 마크다운 표. 첫 줄을 머리글로
        public static string Table(List<List<string>> rows)
        {
            rows = rows.Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
            if (rows.Count == 0) return "";
            int cols = rows.Max(r => r.Count);
            var sb = new StringBuilder();
            for (int i = 0; i < rows.Count; i++)
            {
                sb.Append('|');
                for (int c = 0; c < cols; c++) sb.Append(' ').Append(Cell(c < rows[i].Count ? rows[i][c] : "")).Append(" |");
                sb.Append('\n');
                if (i == 0)
                {
                    sb.Append('|');
                    for (int c = 0; c < cols; c++) sb.Append(" --- |");
                    sb.Append('\n');
                }
            }
            return sb.ToString();
        }

        static string Cell(string s)
        {
            if (s == null) return "";
            return s.Replace("\r\n", "\n").Replace("\r", "\n").Trim().Replace("|", "\\|").Replace("\n", "<br>");
        }

        // 본문 글자에서 마크다운 기호로 오해될 수 있는 것만 이스케이프
        public static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char ch in s)
            {
                if (ch == '\\' || ch == '*' || ch == '_' || ch == '`' || ch == '[' || ch == ']' || ch == '<') sb.Append('\\');
                sb.Append(ch);
            }
            return sb.ToString();
        }

        // 줄 맨 앞에서 제목·인용·목록으로 오해되는 것 방지 ("# 해시태그", "1. 숫자" 등)
        static string EscapeLineStart(string line)
        {
            if (line.Length == 0) return line;
            char ch = line[0];
            if (ch == '#' || ch == '>' || ch == '+' || ch == '-' || ch == '=' || ch == '|') return "\\" + line;
            int i = 0;
            while (i < line.Length && char.IsDigit(line[i])) i++;
            if (i > 0 && i < line.Length && (line[i] == '.' || line[i] == ')')) return line.Substring(0, i) + "\\" + line.Substring(i);
            return line;
        }

        // ---------------- CSV ----------------

        public static void CsvToMd(string csv, string md)
        {
            var rows = CsvXlsx.ReadRows(csv);
            Save(md, "# " + Escape(Path.GetFileNameWithoutExtension(csv)) + "\n\n" + Table(rows));
        }

        // ---------------- docx ----------------

        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string RelOfficeDoc = Rel + "/officeDocument";

        public static void DocxToMd(string docx, string md, string imagesDirName)
        {
            using (Package pkg = Package.Open(docx, FileMode.Open, FileAccess.Read))
            {
                var r = new DocxReader(pkg, Path.GetDirectoryName(md), imagesDirName);
                Save(md, r.Convert());
            }
        }

        class StyleInfo { public string Name, BasedOn, NumId, Ilvl; public int Outline = -1; }

        class DocxReader
        {
            readonly Package pkg;
            readonly PackagePart doc;
            readonly XmlNamespaceManager ns;
            readonly Dictionary<string, PackageRelationship> rels = new Dictionary<string, PackageRelationship>();
            readonly Dictionary<string, StyleInfo> styles = new Dictionary<string, StyleInfo>();
            readonly Dictionary<string, string> numToAbstract = new Dictionary<string, string>();
            readonly Dictionary<string, Dictionary<string, string>> abstractFormats = new Dictionary<string, Dictionary<string, string>>(); // abstractId → ilvl → numFmt
            readonly Dictionary<string, int> listCounters = new Dictionary<string, int>();
            string lastListKind;
            readonly string outDir, imagesDirName;
            readonly Dictionary<string, string> savedImages = new Dictionary<string, string>();

            public DocxReader(Package pkg, string outDir, string imagesDirName)
            {
                this.pkg = pkg;
                this.outDir = outDir;
                this.imagesDirName = imagesDirName;
                PackageRelationship main = pkg.GetRelationshipsByType(RelOfficeDoc).FirstOrDefault();
                if (main == null) throw new Exception("Word 문서 구조가 아니에요");
                doc = pkg.GetPart(PackUriHelper.ResolvePartUri(new Uri("/", UriKind.Relative), main.TargetUri));
                foreach (PackageRelationship rel in doc.GetRelationships()) rels[rel.Id] = rel;

                ns = new XmlNamespaceManager(new NameTable());
                ns.AddNamespace("w", W);
                ns.AddNamespace("r", Rel);
                ns.AddNamespace("a", "http://schemas.openxmlformats.org/drawingml/2006/main");
                ns.AddNamespace("v", "urn:schemas-microsoft-com:vml");

                LoadStyles();
                LoadNumbering();
            }

            XmlDocument LoadPart(string relType)
            {
                PackageRelationship rel = doc.GetRelationshipsByType(Rel + "/" + relType).FirstOrDefault();
                if (rel == null) return null;
                PackagePart part = pkg.GetPart(PackUriHelper.ResolvePartUri(doc.Uri, rel.TargetUri));
                var x = new XmlDocument();
                using (Stream s = part.GetStream()) x.Load(s);
                return x;
            }

            static string Val(XmlNode n) { return n == null ? null : ((XmlElement)n).GetAttribute("val", W); }

            void LoadStyles()
            {
                XmlDocument x = LoadPart("styles");
                if (x == null) return;
                foreach (XmlElement s in x.SelectNodes("//w:style", ns))
                {
                    var info = new StyleInfo
                    {
                        Name = (Val(s.SelectSingleNode("w:name", ns)) ?? "").ToLowerInvariant(),
                        BasedOn = Val(s.SelectSingleNode("w:basedOn", ns)),
                        NumId = Val(s.SelectSingleNode("w:pPr/w:numPr/w:numId", ns)),
                        Ilvl = Val(s.SelectSingleNode("w:pPr/w:numPr/w:ilvl", ns))
                    };
                    int o;
                    if (int.TryParse(Val(s.SelectSingleNode("w:pPr/w:outlineLvl", ns)), out o)) info.Outline = o;
                    styles[s.GetAttribute("styleId", W)] = info;
                }
            }

            void LoadNumbering()
            {
                XmlDocument x = LoadPart("numbering");
                if (x == null) return;
                foreach (XmlElement a in x.SelectNodes("//w:abstractNum", ns))
                {
                    var lv = new Dictionary<string, string>();
                    foreach (XmlElement l in a.SelectNodes("w:lvl", ns)) lv[l.GetAttribute("ilvl", W)] = Val(l.SelectSingleNode("w:numFmt", ns)) ?? "decimal";
                    abstractFormats[a.GetAttribute("abstractNumId", W)] = lv;
                }
                foreach (XmlElement n in x.SelectNodes("//w:num", ns))
                    numToAbstract[n.GetAttribute("numId", W)] = Val(n.SelectSingleNode("w:abstractNumId", ns));
            }

            // 스타일을 기반 스타일까지 따라가며 값 찾기
            T FromStyle<T>(string styleId, Func<StyleInfo, T> pick, T none) where T : class
            {
                for (int guard = 0; styleId != null && guard < 20; guard++)
                {
                    StyleInfo s;
                    if (!styles.TryGetValue(styleId, out s)) break;
                    T v = pick(s);
                    if (v != null) return v;
                    styleId = s.BasedOn;
                }
                return none;
            }

            int HeadingLevel(XmlElement p, string styleId)
            {
                int o;
                if (int.TryParse(Val(p.SelectSingleNode("w:pPr/w:outlineLvl", ns)), out o) && o < 9) return o + 1;
                for (int guard = 0; styleId != null && guard < 20; guard++)
                {
                    StyleInfo s;
                    if (!styles.TryGetValue(styleId, out s)) break;
                    if (s.Name == "title") return 1;
                    if (s.Name.StartsWith("heading ")) { int h; if (int.TryParse(s.Name.Substring(8), out h)) return Math.Min(6, h); }
                    if (s.Outline >= 0 && s.Outline < 9) return Math.Min(6, s.Outline + 1);
                    styleId = s.BasedOn;
                }
                return 0;
            }

            public string Convert()
            {
                var x = new XmlDocument();
                using (Stream s = doc.GetStream()) x.Load(s);
                XmlNode body = x.SelectSingleNode("/w:document/w:body", ns);
                var blocks = new List<string>();
                bool prevList = false;
                lastListKind = null;
                Block(body, blocks, ref prevList);
                var sb = new StringBuilder();
                foreach (string b in blocks) sb.Append(b);
                return sb.ToString();
            }

            void Block(XmlNode parent, List<string> blocks, ref bool prevList)
            {
                foreach (XmlNode n in parent.ChildNodes)
                {
                    if (n.NamespaceURI != W) continue;
                    if (n.LocalName == "p")
                    {
                        Converter.Step(null);
                        bool isList;
                        string md = Paragraph((XmlElement)n, out isList);
                        if (md == null) continue;
                        if (md.Length == 0) { if (prevList) { blocks.Add("\n"); prevList = false; } continue; }
                        if (isList)
                        {
                            // 글머리표 ↔ 번호 목록이 바뀌면 빈 줄로 나눔
                            string kind = md.TrimStart().StartsWith("-") ? "bullet" : "number";
                            if (prevList && md.Length == md.TrimStart().Length && kind != lastListKind) blocks.Add("\n");
                            if (md.Length == md.TrimStart().Length) lastListKind = kind;
                            blocks.Add(md + "\n");
                        }
                        else { if (prevList) blocks.Add("\n"); blocks.Add(md + "\n\n"); }
                        prevList = isList;
                    }
                    else if (n.LocalName == "tbl")
                    {
                        if (prevList) { blocks.Add("\n"); prevList = false; }
                        string t = TableBlock((XmlElement)n);
                        if (t.Length > 0) blocks.Add(t + "\n");
                    }
                    else if (n.LocalName == "sdt")
                    {
                        XmlNode content = n.SelectSingleNode("w:sdtContent", ns);
                        if (content != null) Block(content, blocks, ref prevList);
                    }
                }
            }

            // 문단 하나. 빈 문단이면 "", 목록이면 isList
            string Paragraph(XmlElement p, out bool isList)
            {
                isList = false;
                string styleId = Val(p.SelectSingleNode("w:pPr/w:pStyle", ns));
                string text = Inline(p).Trim();
                if (text.Length == 0) return "";

                int h = HeadingLevel(p, styleId);
                if (h > 0) return new string('#', h) + " " + text.Replace("  \n", " ");

                string numId = Val(p.SelectSingleNode("w:pPr/w:numPr/w:numId", ns)) ?? FromStyle(styleId, s => s.NumId, null);
                string ilvl = Val(p.SelectSingleNode("w:pPr/w:numPr/w:ilvl", ns)) ?? FromStyle(styleId, s => s.Ilvl, null) ?? "0";
                string styleName = FromStyle(styleId, s => s.Name, "") ?? "";
                if ((numId != null && numId != "0") || styleName.StartsWith("list bullet") || styleName.StartsWith("list number"))
                {
                    isList = true;
                    int level; int.TryParse(ilvl, out level);
                    if (level == 0)
                    {
                        // "list bullet 2", "list number 3": 수준이 스타일 이름에만 있는 경우
                        string[] parts = styleName.Split(' ');
                        int nlv;
                        if (parts.Length == 3 && int.TryParse(parts[2], out nlv) && nlv > 1) level = nlv - 1;
                    }
                    string fmt = "bullet";
                    string abs;
                    Dictionary<string, string> lv;
                    if (numId != null && numToAbstract.TryGetValue(numId, out abs) && abs != null && abstractFormats.TryGetValue(abs, out lv)) lv.TryGetValue(ilvl, out fmt);
                    else if (styleName.StartsWith("list number")) fmt = "decimal";
                    string marker = "-";
                    if (fmt != null && fmt != "bullet" && fmt != "none")
                    {
                        string key = (numId ?? styleName) + "/" + level;
                        int c; listCounters.TryGetValue(key, out c);
                        listCounters[key] = ++c;
                        marker = c + ".";
                    }
                    return new string(' ', level * 2) + marker + " " + text.Replace("  \n", " ");
                }
                if (styleName == "quote" || styleName == "intense quote") return "> " + text;
                return EscapeLineStart(text);
            }

            // ---- 문단 안 글자: 굵게·기울임·취소선·링크·그림 ----

            class Seg { public string Text, Link; public bool B, I, S, Raw; }

            string Inline(XmlNode p)
            {
                var segs = new List<Seg>();
                Collect(p, segs, null);
                // 같은 서식이 연달아 나오면 합침 (안 하면 **가****나** 같은 결과)
                var merged = new List<Seg>();
                foreach (Seg s in segs)
                {
                    Seg last = merged.Count > 0 ? merged[merged.Count - 1] : null;
                    if (last != null && !s.Raw && !last.Raw && last.B == s.B && last.I == s.I && last.S == s.S && last.Link == s.Link) last.Text += s.Text;
                    else merged.Add(s);
                }
                var sb = new StringBuilder();
                foreach (Seg s in merged)
                {
                    if (s.Raw) { sb.Append(s.Text); continue; }
                    string t = s.Text;
                    if (t.Length == 0) continue;
                    if (s.B || s.I || s.S)
                    {
                        // 앞뒤 공백은 기호 바깥으로 (안 하면 "** 굵게**"가 굵게 안 됨)
                        string core = t.Trim(' ');
                        if (core.Length == 0) { sb.Append(t); continue; }
                        string lead = t.Substring(0, t.Length - t.TrimStart(' ').Length), trail = t.Substring(t.TrimEnd(' ').Length);
                        string mark = (s.S ? "~~" : "") + (s.B ? "**" : "") + (s.I ? "*" : "");
                        string close = new string(mark.Reverse().ToArray());
                        t = lead + mark + core + close + trail;
                    }
                    if (s.Link != null) t = "[" + t + "](" + s.Link.Replace(" ", "%20").Replace(")", "%29") + ")";
                    sb.Append(t);
                }
                return sb.ToString();
            }

            void Collect(XmlNode parent, List<Seg> segs, string link)
            {
                foreach (XmlNode n in parent.ChildNodes)
                {
                    if (n.NamespaceURI != W) continue;
                    switch (n.LocalName)
                    {
                        case "r": Run((XmlElement)n, segs, link); break;
                        case "hyperlink":
                            string id = ((XmlElement)n).GetAttribute("id", Rel);
                            PackageRelationship rel;
                            string url = id.Length > 0 && rels.TryGetValue(id, out rel) && rel.TargetMode == TargetMode.External ? rel.TargetUri.OriginalString : null;
                            Collect(n, segs, url ?? link);
                            break;
                        case "ins": case "smartTag": case "fldSimple": case "customXml": case "sdt": case "sdtContent":
                            Collect(n, segs, link);
                            break;
                        // del(삭제 표시), proofErr, bookmark 등은 무시
                    }
                }
            }

            void Run(XmlElement r, List<Seg> segs, string link)
            {
                XmlNode pr = r.SelectSingleNode("w:rPr", ns);
                bool b = On(pr, "w:b"), i = On(pr, "w:i"), s = On(pr, "w:strike") || On(pr, "w:dstrike");
                foreach (XmlNode n in r.ChildNodes)
                {
                    if (n.NamespaceURI == W)
                    {
                        string t = null;
                        switch (n.LocalName)
                        {
                            case "t": t = Escape(n.InnerText); break;
                            case "tab": t = " "; break;
                            case "br":
                                string type = ((XmlElement)n).GetAttribute("type", W);
                                if (type == "page") continue;
                                segs.Add(new Seg { Text = "  \n", Raw = true });
                                continue;
                            case "noBreakHyphen": t = "-"; break;
                            case "drawing": case "pict": case "object":
                                string img = Image(n);
                                if (img != null) segs.Add(new Seg { Text = img, Raw = true });
                                continue;
                        }
                        if (t != null) segs.Add(new Seg { Text = t, B = b, I = i, S = s, Link = link });
                    }
                    else if (n.LocalName == "AlternateContent")
                    {
                        // 도형 안 그림 등: 대체 콘텐츠의 그림만
                        string img = Image(n);
                        if (img != null) segs.Add(new Seg { Text = img, Raw = true });
                    }
                }
            }

            bool On(XmlNode pr, string xpath)
            {
                if (pr == null) return false;
                XmlNode n = pr.SelectSingleNode(xpath, ns);
                if (n == null) return false;
                string v = ((XmlElement)n).GetAttribute("val", W);
                return v != "0" && v != "false" && v != "none";
            }

            // 그림을 _images 폴더에 저장하고 ![](...) 문자열을 돌려줌
            string Image(XmlNode node)
            {
                XmlNode blip = node.SelectSingleNode(".//a:blip", ns);
                string id = blip != null ? ((XmlElement)blip).GetAttribute("embed", Rel) : null;
                if (string.IsNullOrEmpty(id))
                {
                    XmlNode vml = node.SelectSingleNode(".//v:imagedata", ns);
                    id = vml != null ? ((XmlElement)vml).GetAttribute("id", Rel) : null;
                }
                PackageRelationship rel;
                if (string.IsNullOrEmpty(id) || !rels.TryGetValue(id, out rel) || rel.TargetMode == TargetMode.External) return null;
                string name;
                if (!savedImages.TryGetValue(id, out name))
                {
                    try
                    {
                        PackagePart part = pkg.GetPart(PackUriHelper.ResolvePartUri(doc.Uri, rel.TargetUri));
                        string dir = Path.Combine(outDir, imagesDirName);
                        Directory.CreateDirectory(dir);
                        name = Path.GetFileName(Uri.UnescapeDataString(part.Uri.OriginalString));
                        string path = Path.Combine(dir, name);
                        for (int k = 1; File.Exists(path); k++) path = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + "_" + k + Path.GetExtension(name));
                        name = Path.GetFileName(path);
                        using (Stream s = part.GetStream())
                        using (FileStream f = File.Create(path)) s.CopyTo(f);
                        savedImages[id] = name;
                    }
                    catch { return null; }
                }
                // 이름에 공백·한글이 있어도 되게 <> 로 감쌈 (CommonMark)
                return "![](<" + imagesDirName + "/" + name + ">)";
            }

            // ---- 표 ----

            string TableBlock(XmlElement tbl)
            {
                var rows = new List<List<string>>();
                foreach (XmlElement tr in tbl.SelectNodes("w:tr", ns))
                {
                    var row = new List<string>();
                    foreach (XmlElement tc in tr.SelectNodes("w:tc", ns))
                    {
                        // 세로 병합의 이어지는 칸은 빈칸
                        XmlNode vm = tc.SelectSingleNode("w:tcPr/w:vMerge", ns);
                        bool cont = vm != null && ((XmlElement)vm).GetAttribute("val", W) != "restart";
                        var parts = new List<string>();
                        if (!cont)
                            foreach (XmlElement p in tc.SelectNodes(".//w:p", ns))
                            {
                                string t = Inline(p).Replace("  \n", " ").Trim();
                                if (t.Length > 0) parts.Add(t);
                            }
                        row.Add(string.Join("\n", parts));
                        // 가로 병합은 빈칸으로 채워 열 맞춤
                        int span;
                        if (int.TryParse(Val(tc.SelectSingleNode("w:tcPr/w:gridSpan", ns)), out span))
                            for (int k = 1; k < span; k++) row.Add("");
                    }
                    rows.Add(row);
                }
                // 표 칸 내용은 이미 마크다운이라 Cell()에서 | 와 줄바꿈만 처리
                return Table(rows);
            }
        }

    }
}
