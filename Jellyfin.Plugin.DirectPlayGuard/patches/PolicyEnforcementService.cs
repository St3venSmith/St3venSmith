using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.DirectPlayGuard.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DirectPlayGuard.Services;

/// <summary>
/// Keeps Jellyfin's per-user transcoding permissions aligned with the plugin settings.
/// </summary>
public sealed class PolicyEnforcementService : IHostedService, IDisposable
{
    private readonly IUserManager _userManager;
    private readonly ILogger<PolicyEnforcementService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private bool _started;

    public PolicyEnforcementService(
        IUserManager userManager,
        ILogger<PolicyEnforcementService> logger)
    {
        _userManager = userManager;
        _logger = logger;
        Current = this;
    }

    public static PolicyEnforcementService? Current { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        plugin.ConfigurationChanged += OnConfigurationChanged;
        _userManager.OnUserUpdated += OnUserUpdated;
        _started = true;

        await ReconcileAllAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        return Task.CompletedTask;
    }

    private async void OnConfigurationChanged(
        object? sender,
        BasePluginConfiguration args)
    {
        try
        {
            await ReconcileAllAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Direct Play Guard failed to apply updated configuration");
        }
    }

    private async void OnUserUpdated(
        object? sender,
        Jellyfin.Data.Events.GenericEventArgs<User> args)
    {
        try
        {
            await ReconcileUserAsync(
                args.Argument,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Direct Play Guard could not reconcile user {UserId}",
                args.Argument.Id);
        }
    }

    public async Task ReconcileAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plugin = Plugin.Instance;
            if (plugin is null)
            {
                return;
            }

            var config = plugin.Configuration;
            if (!config.Enabled)
            {
                await RestoreAllCoreAsync(
                    plugin,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var changedBackups = false;
            foreach (var user in _userManager.GetUsers().ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isAdmin = user.HasPermission(PermissionKind.IsAdministrator);
                if (isAdmin && !config.ApplyToAdministrators)
                {
                    changedBackups |= await RestoreOneCoreAsync(
                        plugin,
                        user,
                        removeBackup: true).ConfigureAwait(false);
                    continue;
                }

                var backup = config.PolicyBackups.FirstOrDefault(
                    item => item.UserId == user.Id);

                if (backup is null)
                {
                    backup = new UserPolicyBackup
                    {
                        UserId = user.Id,
                        VideoTranscoding = user.HasPermission(
                            PermissionKind.EnableVideoPlaybackTranscoding),
                        AudioTranscoding = user.HasPermission(
                            PermissionKind.EnableAudioPlaybackTranscoding),
                        Remuxing = user.HasPermission(
                            PermissionKind.EnablePlaybackRemuxing),
                    };
                    config.PolicyBackups.Add(backup);
                    changedBackups = true;
                }

                var changed = ApplyPolicy(user, config, backup);

                if (changed)
                {
                    await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Direct Play Guard updated playback policy for {User}",
                        user.Username);
                }
            }

            if (changedBackups)
            {
                plugin.SaveConfiguration();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReconcileUserAsync(
        User user,
        CancellationToken cancellationToken = default)
    {
        if (!_gate.Wait(0))
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var plugin = Plugin.Instance;
            if (plugin is null || !plugin.Configuration.Enabled)
            {
                return;
            }

            var config = plugin.Configuration;
            var isAdmin = user.HasPermission(PermissionKind.IsAdministrator);
            if (isAdmin && !config.ApplyToAdministrators)
            {
                return;
            }

            var backup = config.PolicyBackups.FirstOrDefault(
                item => item.UserId == user.Id);

            if (backup is null)
            {
                backup = new UserPolicyBackup
                {
                    UserId = user.Id,
                    VideoTranscoding = user.HasPermission(
                        PermissionKind.EnableVideoPlaybackTranscoding),
                    AudioTranscoding = user.HasPermission(
                        PermissionKind.EnableAudioPlaybackTranscoding),
                    Remuxing = user.HasPermission(
                        PermissionKind.EnablePlaybackRemuxing),
                };

                config.PolicyBackups.Add(backup);
                plugin.SaveConfiguration();
            }

            if (ApplyPolicy(user, config, backup))
            {
                await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool ApplyPolicy(
        User user,
        PluginConfiguration config,
        UserPolicyBackup backup)
    {
        var changed = false;

        changed |= SetIfDifferent(
            user,
            PermissionKind.EnableVideoPlaybackTranscoding,
            config.DisableVideoTranscoding ? false : backup.VideoTranscoding);

        changed |= SetIfDifferent(
            user,
            PermissionKind.EnableAudioPlaybackTranscoding,
            config.DisableAudioTranscoding ? false : backup.AudioTranscoding);

        // Important: when Strict Direct Play is OFF, keep remux/direct stream ON.
        // This is Jellyfin's "conversion without re-encoding" permission and does
        // not re-encode the video. It avoids unnecessary playback failures.
        changed |= SetIfDifferent(
            user,
            PermissionKind.EnablePlaybackRemuxing,
            !config.DisableRemuxing);

        return changed;
    }

    public async Task RestoreAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var plugin = Plugin.Instance;
            if (plugin is not null)
            {
                await RestoreAllCoreAsync(
                    plugin,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RestoreAllCoreAsync(
        Plugin plugin,
        CancellationToken cancellationToken)
    {
        var users = _userManager.GetUsers().ToDictionary(user => user.Id);

        foreach (var backup in plugin.Configuration.PolicyBackups.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!users.TryGetValue(backup.UserId, out var user))
            {
                continue;
            }

            var changed = false;
            changed |= RestoreIfStillManaged(
                user,
                PermissionKind.EnableVideoPlaybackTranscoding,
                backup.VideoTranscoding);
            changed |= RestoreIfStillManaged(
                user,
                PermissionKind.EnableAudioPlaybackTranscoding,
                backup.AudioTranscoding);
            changed |= RestoreRemuxing(
                user,
                backup.Remuxing);

            if (changed)
            {
                await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            }
        }

        if (plugin.Configuration.PolicyBackups.Count > 0)
        {
            plugin.Configuration.PolicyBackups.Clear();
            plugin.SaveConfiguration();
            _logger.LogInformation(
                "Direct Play Guard restored saved Jellyfin playback permissions");
        }
    }

    private async Task<bool> RestoreOneCoreAsync(
        Plugin plugin,
        User user,
        bool removeBackup)
    {
        var backup = plugin.Configuration.PolicyBackups.FirstOrDefault(
            item => item.UserId == user.Id);

        if (backup is null)
        {
            return false;
        }

        var changed = false;
        changed |= RestoreIfStillManaged(
            user,
            PermissionKind.EnableVideoPlaybackTranscoding,
            backup.VideoTranscoding);
        changed |= RestoreIfStillManaged(
            user,
            PermissionKind.EnableAudioPlaybackTranscoding,
            backup.AudioTranscoding);
        changed |= RestoreRemuxing(user, backup.Remuxing);

        if (changed)
        {
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        }

        if (removeBackup)
        {
            plugin.Configuration.PolicyBackups.Remove(backup);
            plugin.SaveConfiguration();
        }

        return removeBackup;
    }

    private static bool SetIfDifferent(
        User user,
        PermissionKind kind,
        bool desired)
    {
        if (user.HasPermission(kind) == desired)
        {
            return false;
        }

        user.SetPermission(kind, desired);
        return true;
    }

    private static bool RestoreIfStillManaged(
        User user,
        PermissionKind kind,
        bool original)
    {
        var current = user.HasPermission(kind);
        if (!current && original)
        {
            user.SetPermission(kind, true);
            return true;
        }

        return false;
    }

    private static bool RestoreRemuxing(User user, bool original)
    {
        var current = user.HasPermission(PermissionKind.EnablePlaybackRemuxing);
        if (current == original)
        {
            return false;
        }

        user.SetPermission(PermissionKind.EnablePlaybackRemuxing, original);
        return true;
    }

    private void Unsubscribe()
    {
        if (!_started)
        {
            return;
        }

        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        _userManager.OnUserUpdated -= OnUserUpdated;
        _started = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Unsubscribe();
        _gate.Dispose();
        _disposed = true;

        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }
}
