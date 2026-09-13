using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.AgentControl.Runtime;

/// <summary>
/// Registration seam for the V2 control runtime. The host application is
/// responsible for calling this; the runtime stays inert unless
/// <c>Control:Enabled</c> is true.
/// </summary>
public static class ControlHostExtensions
{
    public static IServiceCollection AddAcpControlHost(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configuration is not null)
        {
            services.Configure<ControlOptions>(configuration.GetSection(ControlOptions.SectionName));
        }
        else
        {
            services.AddOptions<ControlOptions>();
        }

        services.TryAddSingleton<AcpControlHost>();
        services.AddHostedService(provider => provider.GetRequiredService<AcpControlHost>());
        return services;
    }
}
