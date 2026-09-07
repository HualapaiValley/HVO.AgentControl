using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HVO.AgentControl.Infrastructure;

public sealed class DesignTimeDbFactory : IDesignTimeDbContextFactory<ControlDb>
{
    public ControlDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ControlDb>().UseSqlite("Data Source=data/agentcontrol.db").Options);
}
