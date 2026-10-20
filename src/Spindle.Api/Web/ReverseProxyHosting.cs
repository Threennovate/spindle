using Microsoft.AspNetCore.HttpOverrides;

namespace Spindle.Api.Web;

internal static class ReverseProxyHosting
{
    public static IServiceCollection AddReverseProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;

            var networks = configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [];
            if (networks.Length > 0)
            {
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
                foreach (var network in networks)
                {
                    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
                }
            }
        });
        return services;
    }
}
