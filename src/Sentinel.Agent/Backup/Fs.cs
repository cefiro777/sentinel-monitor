using System;
using System.Collections.Generic;
using System.IO;

namespace Sentinel.Agent.Backup;

/// <summary>
/// Файловые операции зеркала: обычный System.IO для нормальных путей и Win32 (NativeFile) для длиннее MAX_PATH.
/// Так одинаково работает и на Windows Server 2008 R2, где длинных путей нет вовсе, и на современных системах.
/// </summary>
internal static class Fs
{
    public static NativeFile.Info GetInfo(string path)
    {
        if (NativeFile.IsLong(path)) return NativeFile.GetInfo(path);
        var fi = new FileInfo(path);
        if (!fi.Exists) return new NativeFile.Info { Exists = false };
        return new NativeFile.Info { Exists = true, Length = fi.Length, LastWriteTimeUtc = fi.LastWriteTimeUtc, Attributes = fi.Attributes };
    }

    public static void CreateDirectory(string path)
    {
        if (NativeFile.IsLong(path)) NativeFile.CreateDirectory(path);
        else Directory.CreateDirectory(path);
    }

    public static void Copy(string source, string destination, bool overwrite)
    {
        if (NativeFile.IsLong(source) || NativeFile.IsLong(destination)) NativeFile.Copy(source, destination, overwrite);
        else File.Copy(source, destination, overwrite);
    }

    public static void SetNormal(string path) => SetAttributes(path, FileAttributes.Normal);

    public static void SetAttributes(string path, FileAttributes attributes)
    {
        if (NativeFile.IsLong(path)) NativeFile.SetAttributes(path, attributes);
        else File.SetAttributes(path, attributes);
    }

    public static void Delete(string path)
    {
        if (NativeFile.IsLong(path)) NativeFile.Delete(path);
        else File.Delete(path);
    }

    public static void SetLastWriteTimeUtc(string path, DateTime utc)
    {
        if (NativeFile.IsLong(path)) NativeFile.SetLastWriteTimeUtc(path, utc);
        else File.SetLastWriteTimeUtc(path, utc);
    }

    /// <summary>Содержимое каталога. Длинные пути и каталоги с «неудобными» именами обходим через Win32.</summary>
    public static void List(string directory, List<string> files, List<string> directories)
    {
        if (NativeFile.IsLong(directory)) { NativeFile.List(directory, files, directories); return; }
        try
        {
            files.AddRange(Directory.GetFiles(directory));
            directories.AddRange(Directory.GetDirectories(directory));
        }
        catch (PathTooLongException)
        {
            files.Clear(); directories.Clear();
            NativeFile.List(directory, files, directories);
        }
    }
}
