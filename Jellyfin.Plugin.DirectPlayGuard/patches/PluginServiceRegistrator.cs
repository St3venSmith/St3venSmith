using Jellyfin.Plugin.DirectPlayGuard.Services;
using Jellyfin.Plugin.DirectPlayGuard.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Plugins;
using Jellyfin.Data.Events.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DirectPlayGuard;

/// <summary>Registers plugin services.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(
        IServiceCollection serviceCollection,
        IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PolicyEnforcementService>();
        serviceCollection.AddHostedService<PolicyEnforcementService>(
            provider => provider.GetRequiredService<PolicyEnforcementService>());
        serviceCollection.AddScoped<IEventConsumer<UserCreatedEventArgs>, UserCreatedConsumer>();

        serviceCollection.AddHostedService<FileTransformationRegistrationService>();

        // Fallback for installations where File Transformation is not present.
        // When File Transformation is loaded this filter defers to it.
        serviceCollection.AddSingleton<IStartupFilter, WebInjectionStartupFilter>();
    }
}
