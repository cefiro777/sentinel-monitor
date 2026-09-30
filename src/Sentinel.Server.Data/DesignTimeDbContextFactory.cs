using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Sentinel.Server.Data;

/// <summary>Для `dotnet ef migrations add` без запуска сервера.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SentinelDbContext>
{
    public SentinelDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("SENTINEL_DB")
                 ?? "Host=localhost;Port=5432;Database=sentinel;Username=sentinel;Password=sentinel";
        var options = new DbContextOptionsBuilder<SentinelDbContext>()
            .UseNpgsql(cs)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new SentinelDbContext(options);
    }
}
