namespace Sentinel.Server.Core.Entities;

/// <summary>Клиент IT-аутсорсинга.</summary>
public class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Короткий код для URL и имён папок в облаке: "romashka".</summary>
    public string Slug { get; set; } = "";
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    public List<Site> Sites { get; set; } = new();
    public List<Host> Hosts { get; set; } = new();
}

/// <summary>Площадка клиента: офис, склад, магазин.</summary>
public class Site
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public string Name { get; set; } = "";
    public string? Address { get; set; }
}

public enum HostKind
{
    Server = 0,
    Workstation = 1,
    NetworkDevice = 2,
    Dvr = 3,
    Other = 9,
}

/// <summary>Объект мониторинга. Сервер с агентом, либо устройство, которое проверяет чужой агент.</summary>
public class Host
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public Guid? SiteId { get; set; }
    public Site? Site { get; set; }

    public string Name { get; set; } = "";
    public HostKind Kind { get; set; } = HostKind.Server;
    public string? Address { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Увеличивается при любом изменении проверок хоста; агент перечитывает конфиг.</summary>
    public int ConfigVersion { get; set; } = 1;

    public Agent? Agent { get; set; }
    public List<Check> Checks { get; set; } = new();
}
