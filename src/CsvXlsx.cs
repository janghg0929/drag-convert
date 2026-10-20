using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Text;
using System.Xml;

namespace ShiftConvert
{
    // CSV를 Excel에 맡기지 않고 직접 읽어서, 모든 칸이 "텍스트"인 xlsx를 만든다.
    // Excel이 CSV를 열면 010 → 10, 1-2 → 1월 2일, 16자리 넘는 숫자 → 뒷자리 0 처럼 데이터를 몰래 바꾸기 때문.
    // 칸이 전부 글자로 들어가니 =HYPERLINK(...) 같은 "CSV 수식 공격"도 실행되지 않는다.
    static class CsvXlsx
    {
        const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const int MaxRows = 1048576, MaxCols = 16384, MaxCellChars = 32767;

        public static void Convert(string csvPath, string xlsxPath)
        {
            List<List<string>> rows = Parse(ReadText(csvPath));
            if (rows.Count > MaxRows) throw new Exception("행이 너무 많아요 (Excel은 최대 1,048,576행)");
            Write(rows, xlsxPath, Path.GetFileNameWithoutExtension(csvPath));
        }

        // BOM이 있으면 그대로, 없으면 UTF-8로 읽어 보고 안 되면 시스템 기본(한국어 Windows는 CP949)
        static string ReadText(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return new UTF8Encoding(false).GetString(b, 3, b.Length - 3);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch (DecoderFallbackException) { return Encoding.Default.GetString(b); }
        }

        // RFC 4180: 따옴표로 감싼 칸 안의 쉼표·줄바꿈 허용, "" → "
        static List<List<string>> Parse(string s)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var f = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quoted)
                {
                    if (c != '"') { f.Append(c); continue; }
                    if (i + 1 < s.Length && s[i + 1] == '"') { f.Append('"'); i++; continue; }
                    quoted = false;
                    continue;
                }
                if (c == '"' && f.Length == 0) { quoted = true; continue; } // 칸 중간의 "는 그냥 글자로
                if (c == ',') { row.Add(f.ToString()); f.Clear(); continue; }
                if (c == '\r' || c == '\n')
                {
                    row.Add(f.ToString()); f.Clear();
                    rows.Add(row); row = new List<string>();
                    if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                    if (rows.Count % 20000 == 0) Converter.Step(null);
                    continue;
                }
                f.Append(c);
            }
            if (f.Length > 0 || row.Count > 0) { row.Add(f.ToString()); rows.Add(row); }
            return rows;
        }

        static void Write(List<List<string>> rows, string path, string sheetName)
        {
            int cols = 0;
            foreach (var r in rows) cols = Math.Max(cols, r.Count);
            if (cols > MaxCols) throw new Exception("열이 너무 많아요 (Excel은 최대 16,384열)");

            // 열 너비: 가장 긴 글자 기준 (한글 등 넓은 글자는 2칸으로 계산), 8~60
            var widths = new int[cols];
            foreach (var r in rows)
                for (int c = 0; c < r.Count; c++)
                {
                    int w = 0;
                    foreach (char ch in r[c]) { if (ch == '\n') break; w += ch > 0x2E80 ? 2 : 1; }
                    widths[c] = Math.Max(widths[c], w);
                }

            if (File.Exists(path)) File.Delete(path);
            using (Package pkg = Package.Open(path, FileMode.Create))
            {
                var wbUri = new Uri("/xl/workbook.xml", UriKind.Relative);
                var shUri = new Uri("/xl/worksheets/sheet1.xml", UriKind.Relative);
                var stUri = new Uri("/xl/styles.xml", UriKind.Relative);
                PackagePart wb = pkg.CreatePart(wbUri, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml", CompressionOption.Normal);
                PackagePart sh = pkg.CreatePart(shUri, "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml", CompressionOption.Normal);
                PackagePart st = pkg.CreatePart(stUri, "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml", CompressionOption.Normal);
                pkg.CreateRelationship(wbUri, TargetMode.Internal, NsRel + "/officeDocument", "rId1");
                wb.CreateRelationship(new Uri("worksheets/sheet1.xml", UriKind.Relative), TargetMode.Internal, NsRel + "/worksheet", "rId1");
                wb.CreateRelationship(new Uri("styles.xml", UriKind.Relative), TargetMode.Internal, NsRel + "/styles", "rId2");

                using (XmlWriter w = XmlWriter.Create(wb.GetStream(), Settings()))
                {
                    w.WriteStartElement("workbook", NsMain);
                    w.WriteAttributeString("xmlns", "r", null, NsRel);
                    w.WriteStartElement("sheets", NsMain);
                    w.WriteStartElement("sheet", NsMain);
                    w.WriteAttributeString("name", SafeSheetName(sheetName));
                    w.WriteAttributeString("sheetId", "1");
                    w.WriteAttributeString("id", NsRel, "rId1");
                    w.WriteEndElement(); w.WriteEndElement(); w.WriteEndElement();
                }

                // 스타일 0 = 기본, 1 = 텍스트 서식(@). 열 전체를 1로 지정해서 나중에 새로 입력한 값도 글자로 남게
                using (var sw = new StreamWriter(st.GetStream(), new UTF8Encoding(false)))
                    sw.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<styleSheet xmlns=\"" + NsMain + "\">" +
                        "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"맑은 고딕\"/><family val=\"3\"/></font></fonts>" +
                        "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                        "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                        "<xf numFmtId=\"49\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs>" +
                        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
                        "</styleSheet>");

                using (XmlWriter w = XmlWriter.Create(sh.GetStream(), Settings()))
                {
                    w.WriteStartElement("worksheet", NsMain);
                    if (cols > 0)
                    {
                        w.WriteStartElement("cols", NsMain);
                        for (int c = 0; c < cols; c++)
                        {
                            w.WriteStartElement("col", NsMain);
                            w.WriteAttributeString("min", (c + 1).ToString());
                            w.WriteAttributeString("max", (c + 1).ToString());
                            w.WriteAttributeString("width", Math.Max(8, Math.Min(60, widths[c] + 2)).ToString());
                            w.WriteAttributeString("style", "1");
                            w.WriteAttributeString("customWidth", "1");
                            w.WriteEndElement();
                        }
                        w.WriteEndElement();
                    }
                    w.WriteStartElement("sheetData", NsMain);
                    for (int r = 0; r < rows.Count; r++)
                    {
                        if (r % 20000 == 0 && r > 0) Converter.Step("행 " + r + "/" + rows.Count);
                        w.WriteStartElement("row", NsMain);
                        w.WriteAttributeString("r", (r + 1).ToString());
                        List<string> row = rows[r];
                        for (int c = 0; c < row.Count; c++)
                        {
                            if (row[c].Length == 0) continue;
                            if (row[c].Length > MaxCellChars) throw new Exception((r + 1) + "행: 한 칸에 글자가 너무 많아요 (최대 32,767자)");
                            w.WriteStartElement("c", NsMain);
                            w.WriteAttributeString("r", ColName(c) + (r + 1));
                            w.WriteAttributeString("s", "1");
                            w.WriteAttributeString("t", "inlineStr");
                            w.WriteStartElement("is", NsMain);
                            w.WriteStartElement("t", NsMain);
                            w.WriteAttributeString("xml", "space", null, "preserve");
                            w.WriteString(XmlSafe(row[c]));
                            w.WriteEndElement(); w.WriteEndElement(); w.WriteEndElement();
                        }
                        w.WriteEndElement();
                    }
                    w.WriteEndElement(); w.WriteEndElement();
                }
            }
        }

        static XmlWriterSettings Settings()
        {
            // NewLineHandling.None: 칸 안의 줄바꿈(\n)을 \r\n으로 바꾸지 않고 그대로
            return new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = true, NewLineHandling = NewLineHandling.None };
        }

        static string ColName(int c)
        {
            string s = "";
            for (c++; c > 0; c = (c - 1) / 26) s = (char)('A' + (c - 1) % 26) + s;
            return s;
        }

        // XML에 넣을 수 없는 제어 문자는 뺀다 (탭·줄바꿈은 유지)
        static string XmlSafe(string s)
        {
            StringBuilder b = null;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                bool ok = ch == '\t' || ch == '\n' || ch == '\r' || (ch >= 0x20 && ch != 0xFFFE && ch != 0xFFFF);
                if (char.IsHighSurrogate(ch)) ok = i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
                else if (char.IsLowSurrogate(ch)) ok = i > 0 && char.IsHighSurrogate(s[i - 1]);
                if (ok) { if (b != null) b.Append(ch); }
                else if (b == null) b = new StringBuilder(s, 0, i, s.Length);
            }
            return b == null ? s : b.ToString();
        }

        // 시트 이름: 31자 이내, : \ / ? * [ ] 금지
        static string SafeSheetName(string name)
        {
            var b = new StringBuilder();
            foreach (char ch in name) b.Append(":\\/?*[]".IndexOf(ch) >= 0 ? '_' : ch);
            string s = b.ToString().Trim('\'', ' ');
            if (s.Length > 31) s = s.Substring(0, 31);
            return s.Length == 0 ? "Sheet1" : s;
        }
    }
}
