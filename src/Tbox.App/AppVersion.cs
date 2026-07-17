using System.Reflection;
using Windows.ApplicationModel;

namespace Tbox.App;

internal static class AppVersion
{
    public static string Current
    {
        get
        {
            try
            {
                var version = Package.Current.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch
            {
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString(4) ?? "0.0.0.0";
            }
        }
    }
}
