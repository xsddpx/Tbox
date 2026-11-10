#if WINDOWS
using System.Runtime.InteropServices;
using Tbox.Core;

namespace Tbox.Desktop.Platform;

internal static class WindowsFolderIndex
{
    internal static IReadOnlyList<string> Search(string query, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        object? connection = null, records = null, field = null;
        try
        {
            if (Type.GetTypeFromProgID("ADODB.Connection") is not { } type) return paths;
            connection = Activator.CreateInstance(type);
            if (connection is null) return paths;
            dynamic db = connection;
            db.ConnectionTimeout = 2;
            db.CommandTimeout = 2;
            db.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';", "", "", 0);
            var term = query.Replace("'", "''", StringComparison.Ordinal);
            var urlTerm = term.Replace('\\', '/');
            records = db.Execute($"SELECT TOP {FolderJumpList.SearchLimit} System.ItemUrl FROM SYSTEMINDEX " +
                $"WHERE System.ItemType = 'Directory' AND (System.ItemPathDisplay LIKE '%{term}%' " +
                $"OR System.ItemUrl LIKE '%{urlTerm}%') ORDER BY System.ItemNameDisplay");
            dynamic rows = records!;
            field = rows.Fields.Item(0);
            while (!(bool)rows.EOF)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (((dynamic)field!).Value is string value && Uri.TryCreate(value, UriKind.Absolute, out var url) &&
                    url.IsFile && Directory.Exists(url.LocalPath)) paths.Add(url.LocalPath);
                rows.MoveNext();
            }
        }
        catch (COMException error) { AppLog.Write("Windows folder index: " + error.GetType().Name); }
        finally
        {
            Close(records);
            Close(connection);
            if (field is not null) Marshal.ReleaseComObject(field);
        }
        return paths;
    }

    private static void Close(object? value)
    {
        if (value is null) return;
        try { ((dynamic)value).Close(); }
        catch (COMException) { }
        finally { Marshal.ReleaseComObject(value); }
    }
}
#endif
