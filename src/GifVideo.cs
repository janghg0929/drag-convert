using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShiftConvert
{
    // 움직이는 GIF → MP4(H.264). ffmpeg 없이 Windows에 들어 있는 Media Foundation 인코더를 쓴다.
    // GIF는 장면마다 일부분만 새로 그리고(부분 갱신), 장면마다 보여주는 시간이 다르고, 투명 배경이 있어서
    // 장면을 직접 겹쳐 그려 완성된 화면을 만든 다음 한 장씩 인코더에 넣는다.
    static class GifVideo
    {
        // ---------------- 사용 가능 여부 ----------------

        // Windows N 에디션 등은 H.264 인코더가 없음
        public static bool Available
        {
            get
            {
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                // H.264 인코더: Windows 10/11은 mfh264enc.dll, 예전 Windows는 msmpeg2venc.dll
                return File.Exists(Path.Combine(sys, "mfplat.dll")) && File.Exists(Path.Combine(sys, "mfreadwrite.dll")) &&
                       (File.Exists(Path.Combine(sys, "mfh264enc.dll")) || File.Exists(Path.Combine(sys, "msmpeg2venc.dll")));
            }
        }

        public static bool IsAnimated(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                    return new GifBitmapDecoder(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames.Count > 1;
            }
            catch { return false; }
        }

        // ---------------- 변환 ----------------

        public static void ToMp4(string src, string dst)
        {
            using (var fs = File.OpenRead(src))
            {
                var dec = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                int n = dec.Frames.Count;
                int w = Meta(dec.Metadata as BitmapMetadata, "/logscrdesc/Width", dec.Frames[0].PixelWidth);
                int h = Meta(dec.Metadata as BitmapMetadata, "/logscrdesc/Height", dec.Frames[0].PixelHeight);
                // H.264는 가로세로가 짝수여야 하고 너무 작으면 실패 → 흰 여백으로 맞춤
                int vw = Math.Max(16, (w + 1) & ~1), vh = Math.Max(16, (h + 1) & ~1);

                var canvas = new byte[w * h * 4];      // 겹쳐 그린 현재 화면 (투명 포함)
                var outFrame = new byte[vw * vh * 4];  // 인코더에 넣을 화면 (흰 배경에 합성)

                using (var enc = new Mp4Writer(dst, vw, vh))
                {
                    long t = 0;
                    for (int i = 0; i < n; i++)
                    {
                        Converter.Step("장면 " + (i + 1) + "/" + n);
                        BitmapFrame f = dec.Frames[i];
                        var md = f.Metadata as BitmapMetadata;
                        int left = Meta(md, "/imgdesc/Left", 0), top = Meta(md, "/imgdesc/Top", 0);
                        int delay = Meta(md, "/grctlext/Delay", 10) * 10; // 1/100초 → ms
                        if (delay < 20) delay = 100;                       // 브라우저처럼 0~10ms는 0.1초로
                        int disposal = Meta(md, "/grctlext/Disposal", 0);

                        byte[] saved = disposal == 3 ? (byte[])canvas.Clone() : null;

                        // 이번 장면을 현재 화면 위에 겹쳐 그림 (GIF 투명은 완전 투명/불투명 둘 중 하나)
                        var bgra = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
                        int fw = bgra.PixelWidth, fh = bgra.PixelHeight;
                        var px = new byte[fw * fh * 4];
                        bgra.CopyPixels(px, fw * 4, 0);
                        for (int y = 0; y < fh; y++)
                        {
                            int cy = top + y;
                            if (cy < 0 || cy >= h) continue;
                            for (int x = 0; x < fw; x++)
                            {
                                int cx = left + x;
                                if (cx < 0 || cx >= w) continue;
                                int si = (y * fw + x) * 4;
                                if (px[si + 3] == 0) continue;
                                Buffer.BlockCopy(px, si, canvas, (cy * w + cx) * 4, 4);
                            }
                        }

                        // 흰 배경에 합성해서 영상 한 장으로
                        for (int k = 0; k < outFrame.Length; k += 4) { outFrame[k] = 255; outFrame[k + 1] = 255; outFrame[k + 2] = 255; outFrame[k + 3] = 255; }
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                int ci = (y * w + x) * 4, oi = (y * vw + x) * 4;
                                int a = canvas[ci + 3], inv = 255 - a;
                                outFrame[oi] = (byte)((canvas[ci] * a + 255 * inv) / 255);
                                outFrame[oi + 1] = (byte)((canvas[ci + 1] * a + 255 * inv) / 255);
                                outFrame[oi + 2] = (byte)((canvas[ci + 2] * a + 255 * inv) / 255);
                            }
                        long dur = delay * 10000L; // ms → 100ns 단위
                        enc.Write(outFrame, t, dur);
                        t += dur;

                        // 다음 장면 전에 이번 장면 처리 방식(disposal) 적용
                        if (disposal == 2)
                        {
                            int x0 = Math.Max(0, left), x1 = Math.Min(w, left + fw);
                            if (x1 > x0)
                                for (int y = Math.Max(0, top); y < Math.Min(h, top + fh); y++)
                                    Array.Clear(canvas, (y * w + x0) * 4, (x1 - x0) * 4);
                        }
                        else if (disposal == 3 && saved != null) canvas = saved;
                    }
                    enc.Finish();
                }
            }
        }

        static int Meta(BitmapMetadata md, string query, int fallback)
        {
            try
            {
                if (md != null && md.ContainsQuery(query)) return System.Convert.ToInt32(md.GetQuery(query));
            }
            catch { }
            return fallback;
        }

        // ---------------- Media Foundation Sink Writer ----------------

        sealed class Mp4Writer : IDisposable
        {
            readonly IMFSinkWriter writer;
            readonly int stream, frameBytes, stride;
            bool started, finished;

            public Mp4Writer(string path, int w, int h)
            {
                Check(MFStartup(0x00020070, 0));
                started = true;
                stride = w * 4;
                frameBytes = stride * h;
                if (File.Exists(path)) File.Delete(path);
                Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out writer));

                IMFMediaType outType;
                Check(MFCreateMediaType(out outType));
                outType.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                outType.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
                outType.SetUINT32(MF_MT_AVG_BITRATE, Math.Max(800000, Math.Min(20000000, w * h * 8)));
                outType.SetUINT32(MF_MT_INTERLACE_MODE, 2); // progressive
                outType.SetUINT64(MF_MT_FRAME_SIZE, ((long)w << 32) | (uint)h);
                outType.SetUINT64(MF_MT_FRAME_RATE, (30L << 32) | 1);
                outType.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, (1L << 32) | 1);
                writer.AddStream(outType, out stream);

                IMFMediaType inType;
                Check(MFCreateMediaType(out inType));
                inType.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                inType.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
                inType.SetUINT32(MF_MT_INTERLACE_MODE, 2);
                inType.SetUINT32(MF_MT_DEFAULT_STRIDE, stride); // 양수 = 위에서 아래로 저장된 화면
                inType.SetUINT64(MF_MT_FRAME_SIZE, ((long)w << 32) | (uint)h);
                inType.SetUINT64(MF_MT_FRAME_RATE, (30L << 32) | 1);
                inType.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, (1L << 32) | 1);
                writer.SetInputMediaType(stream, inType, IntPtr.Zero);
                Marshal.ReleaseComObject(outType);
                Marshal.ReleaseComObject(inType);
                writer.BeginWriting();
            }

            public void Write(byte[] bgrx, long time, long duration)
            {
                IMFMediaBuffer buf;
                Check(MFCreateMemoryBuffer(frameBytes, out buf));
                IMFSample sample = null;
                try
                {
                    IntPtr p; int max, cur;
                    buf.Lock(out p, out max, out cur);
                    Marshal.Copy(bgrx, 0, p, frameBytes);
                    buf.Unlock();
                    buf.SetCurrentLength(frameBytes);
                    Check(MFCreateSample(out sample));
                    sample.AddBuffer(buf);
                    sample.SetSampleTime(time);
                    sample.SetSampleDuration(duration);
                    writer.WriteSample(stream, sample);
                }
                finally
                {
                    if (sample != null) Marshal.ReleaseComObject(sample);
                    Marshal.ReleaseComObject(buf);
                }
            }

            public void Finish()
            {
                writer.DoFinalize();
                finished = true;
            }

            public void Dispose()
            {
                if (writer != null) Marshal.ReleaseComObject(writer);
                if (started) MFShutdown();
                if (!finished) { } // 실패하면 호출한 쪽이 결과 파일을 지움
            }
        }

        static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

        [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] static extern int MFShutdown();
        [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfplat.dll")] static extern int MFCreateSample(out IMFSample sample);
        [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, out IMFSinkWriter writer);

        static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        static readonly Guid MF_MT_AVG_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        static readonly Guid MF_MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        static readonly Guid MF_MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        static readonly Guid MF_MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        static readonly Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        static readonly Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");

        // COM 인터페이스: 쓰는 메서드까지만 vtable 순서대로 선언 (a.. 는 순서를 맞추기 위한 자리)
        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFMediaType
        {
            void a00(); void a01(); void a02(); void a03(); void a04(); void a05(); void a06(); void a07(); void a08();
            void a09(); void a10(); void a11(); void a12(); void a13(); void a14(); void a15(); void a16(); void a17();
            void SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
            void SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, long value);
            void a20();
            void SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFSample
        {
            void a00(); void a01(); void a02(); void a03(); void a04(); void a05(); void a06(); void a07(); void a08(); void a09();
            void a10(); void a11(); void a12(); void a13(); void a14(); void a15(); void a16(); void a17(); void a18(); void a19();
            void a20(); void a21(); void a22(); void a23(); void a24(); void a25(); void a26(); void a27(); void a28(); void a29();
            void b30(); void b31(); void b32();
            void SetSampleTime(long time);
            void b34();
            void SetSampleDuration(long duration);
            void b36(); void b37(); void b38();
            void AddBuffer(IMFMediaBuffer buffer);
        }

        [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFMediaBuffer
        {
            void Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            void Unlock();
            void GetCurrentLength(out int length);
            void SetCurrentLength(int length);
        }

        [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFSinkWriter
        {
            void AddStream(IMFMediaType targetType, out int streamIndex);
            void SetInputMediaType(int streamIndex, IMFMediaType inputType, IntPtr encodingParameters);
            void BeginWriting();
            void WriteSample(int streamIndex, IMFSample sample);
            void SendStreamTick(int streamIndex, long timestamp);
            void PlaceMarker(int streamIndex, IntPtr context);
            void NotifyEndOfSegment(int streamIndex);
            void Flush(int streamIndex);
            void DoFinalize();
        }
    }
}
