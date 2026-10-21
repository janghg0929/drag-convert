// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 janghg0929 — 끌어서 변환 (https://github.com/janghg0929/drag-convert)

using System.Reflection;
using System.Runtime.Versioning;

// .NET이 옛 4.0 호환 모드 대신 4.8 동작(접근성 개선, 긴 경로 처리 등)을 쓰게 하는 표시
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

// 새 버전을 낼 때는 여기 숫자만 올리고 release.ps1을 실행하면 된다
[assembly: AssemblyTitle("끌어서 변환")]
[assembly: AssemblyProduct("DragConvert")]
[assembly: AssemblyCopyright("Copyright (C) 2026 janghg0929 · GPL-3.0-or-later")]
[assembly: AssemblyVersion(ShiftConvert.AppVersion.Current)]
[assembly: AssemblyFileVersion(ShiftConvert.AppVersion.Current)]

namespace ShiftConvert
{
    static class AppVersion
    {
        public const string Current = "1.0.0";
    }
}
