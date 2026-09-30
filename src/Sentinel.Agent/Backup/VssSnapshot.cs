using System;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;

namespace Sentinel.Agent.Backup;

/// <summary>
/// Теневая копия тома через WMI Win32_ShadowCopy (без нативных библиотек — работает на Server 2008 R2+).
/// Снимок монтируется символической ссылкой, чтобы обычный File API мог читать открытые файлы (1Cv8.1CD, .mdf и т.п.).
/// </summary>
public sealed class VssSnapshot : IDisposable
{
    private readonly string _shadowId;
    private readonly string _mountPoint;

    /// <summary>Буква тома, для которого сделан снимок: "C:".</summary>
    public string Volume { get; }

    /// <summary>Путь, по которому доступно содержимое тома в снимке.</summary>
    public string MountPoint => _mountPoint;

    private VssSnapshot(string volume, string shadowId, string mountPoint)
    {
        Volume = volume;
        _shadowId = shadowId;
        _mountPoint = mountPoint;
    }

    public static VssSnapshot Create(string volume, BackupLog log)
    {
        volume = volume.TrimEnd('\\') + "\\";
        var sc = new ManagementClass("Win32_ShadowCopy");
        var inParams = sc.GetMethodParameters("Create");
        inParams["Volume"] = volume;
        inParams["Context"] = "ClientAccessible";
        var outParams = sc.InvokeMethod("Create", inParams, null);
        var rc = Convert.ToInt32(outParams["ReturnValue"]);
        if (rc != 0) throw new InvalidOperationException($"VSS: не удалось создать снимок тома {volume}: код {rc} ({Describe(rc)})");
        var shadowId = (string)outParams["ShadowID"];

        string? device = null;
        using (var searcher = new ManagementObjectSearcher($"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID='{shadowId}'"))
        foreach (ManagementObject o in searcher.Get()) device = (string)o["DeviceObject"];
        if (device is null) { TryDelete(shadowId); throw new InvalidOperationException("VSS: снимок создан, но не найден его DeviceObject"); }

        var mount = Path.Combine(Path.GetTempPath(), "sentinel-vss-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        // Ссылка на \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\ — обязателен завершающий слэш.
        if (!CreateSymbolicLink(mount, device + "\\", 1))
        {
            var err = Marshal.GetLastWin32Error();
            TryDelete(shadowId);
            throw new InvalidOperationException($"VSS: не удалось смонтировать снимок (CreateSymbolicLink, ошибка {err})");
        }
        log.Info($"VSS: снимок тома {volume} создан ({shadowId}), смонтирован в {mount}");
        return new VssSnapshot(volume.TrimEnd('\\'), shadowId, mount);
    }

    /// <summary>Переводит путь на живом томе в путь внутри снимка.</summary>
    public string Translate(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Volume, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{path} не на томе {Volume}");
        return Path.Combine(_mountPoint, full.Substring(Volume.Length).TrimStart('\\'));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_mountPoint)) Directory.Delete(_mountPoint); } catch { }
        TryDelete(_shadowId);
    }

    private static void TryDelete(string shadowId)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_ShadowCopy WHERE ID='{shadowId}'"))
            foreach (ManagementObject o in searcher.Get()) o.Delete();
        }
        catch { }
    }

    private static string Describe(int rc) => rc switch
    {
        1 => "доступ запрещён", 2 => "неверный аргумент", 3 => "том не найден", 4 => "том не поддерживает теневые копии",
        5 => "неподдерживаемый контекст", 6 => "недостаточно места", 7 => "другой снимок в процессе создания", 8 => "провайдер занят",
        9 => "ошибка провайдера", 10 => "неизвестная ошибка", 11 => "таймаут", 12 => "провайдер не найден", 13 => "ошибка провайдера",
        _ => "см. документацию Win32_ShadowCopy",
    };

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, int dwFlags);
}
