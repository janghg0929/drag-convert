using System.Reflection;

// 새 버전을 낼 때는 여기 숫자만 올리고 release.ps1을 실행하면 된다
[assembly: AssemblyTitle("Shift 변환기")]
[assembly: AssemblyProduct("ShiftConvert")]
[assembly: AssemblyVersion(ShiftConvert.AppVersion.Current)]
[assembly: AssemblyFileVersion(ShiftConvert.AppVersion.Current)]

namespace ShiftConvert
{
    static class AppVersion
    {
        public const string Current = "1.0.0";
    }
}
