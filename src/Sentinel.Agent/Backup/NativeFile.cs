using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Sentinel.Agent.Backup;

/// <summary>
/// Файловые операции через Win32 с префиксом «\\?\»: единственный способ работать с путями длиннее 260 символов
/// на .NET Framework (System.IO такие пути отвергает независимо от настроек) и на Windows Server 2008 R2/2012,
/// где длинные пути не поддерживаются вовсе. Используется только когда обычный путь не проходит по длине.
/// </summary>
internal static class NativeFile
{
    /// <summary>Порог, после которого переходим на Win32: MAX_PATH за вычетом запаса на имя файла.</summary>
    public const int Threshold = 240;

    public static bool IsLong(string path) => !string.IsNullOrEmpty(path) && path.Length >= Threshold;

    public static string Prefixed(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        // Префикс «\\?\» отключает нормализацию в Windows: прямые слеши и «.» в таком пути не разбираются.
        var full = path.Replace('/', '\\');
        if (!Path.IsPathRooted(full)) full = Path.GetFullPath(full);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
    }

    public struct Info
    {
        public bool Exists;
        public long Length;
        public DateTime LastWriteTimeUtc;
        public FileAttributes Attributes;
    }

    public static Info GetInfo(string path)
    {
        if (!GetFileAttributesEx(Prefixed(path), 0, out var data)) return new Info { Exists = false };
        return new Info
        {
            Exists = true,
            Length = ((long)data.nFileSizeHigh << 32) | (uint)data.nFileSizeLow,
            LastWriteTimeUtc = ToUtc(data.ftLastWriteTime),
            Attributes = (FileAttributes)data.dwFileAttributes,
        };
    }

    public static void CreateDirectory(string path)
    {
        var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
        var missing = new Stack<string>();
        for (var dir = full; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            var info = GetInfo(dir);
            if (info.Exists) break;
            missing.Push(dir);
        }
        while (missing.Count > 0)
        {
            var dir = missing.Pop();
            if (!CreateDirectoryW(Prefixed(dir), IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                if (err != 183) throw new IOException($"не удалось создать папку «{dir}»: {new Win32Exception(err).Message}");  // 183 — уже существует
            }
        }
    }

    public static void Copy(string source, string destination, bool overwrite)
    {
        if (!CopyFileW(Prefixed(source), Prefixed(destination), failIfExists: !overwrite))
            throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    public static void SetAttributes(string path, FileAttributes attributes)
    {
        if (!SetFileAttributesW(Prefixed(path), (uint)attributes))
            throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    public static void Delete(string path)
    {
        if (!DeleteFileW(Prefixed(path)))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != 2) throw new IOException(new Win32Exception(err).Message);   // 2 — файла и так нет
        }
    }

    public static void SetLastWriteTimeUtc(string path, DateTime utc)
    {
        // 0x40000000 = GENERIC_WRITE, 3 = OPEN_EXISTING, 0x02000000 = FILE_FLAG_BACKUP_SEMANTICS
        var handle = CreateFileW(Prefixed(path), 0x40000000, 0, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle == new IntPtr(-1)) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        try
        {
            var ft = utc.ToFileTimeUtc();
            if (!SetFileTime(handle, IntPtr.Zero, IntPtr.Zero, ref ft))
                throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>Файлы и подпапки каталога; работает и для путей длиннее MAX_PATH.</summary>
    public static void List(string directory, List<string> files, List<string> directories)
    {
        var pattern = Prefixed(directory).TrimEnd('\\') + @"\*";
        var handle = FindFirstFileW(pattern, out var found);
        if (handle == new IntPtr(-1))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == 2 || err == 3 || err == 18) return;   // нет файлов / нет пути
            throw new IOException(new Win32Exception(err).Message);
        }
        try
        {
            do
            {
                var name = found.cFileName;
                if (name == "." || name == "..") continue;
                var full = Path.Combine(directory, name);
                if ((found.dwFileAttributes & 0x10) != 0) directories.Add(full);   // FILE_ATTRIBUTE_DIRECTORY
                else files.Add(full);
            }
            while (FindNextFileW(handle, out found));
        }
        finally { FindClose(handle); }
    }

    private static DateTime ToUtc(FILETIME ft) => DateTime.FromFileTimeUtc(((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public int dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WIN32_FILE_ATTRIBUTE_DATA
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint nFileSizeHigh, nFileSizeLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFileAttributesExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileAttributesEx(string name, int infoLevel, out WIN32_FILE_ATTRIBUTE_DATA data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileW(string existing, string newFile, [MarshalAs(UnmanagedType.Bool)] bool failIfExists);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileAttributesW(string path, uint attributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(IntPtr handle, IntPtr creation, IntPtr lastAccess, ref long lastWrite);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string pattern, out WIN32_FIND_DATAW data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(IntPtr handle, out WIN32_FIND_DATAW data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);
}
