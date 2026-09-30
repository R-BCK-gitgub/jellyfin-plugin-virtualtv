using System;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Resolves the administrator/owner used by personal watched-dependent channels.
/// </summary>
public sealed class VirtualTvUserContextService
{
    private readonly IUserManager _userManager;

    public VirtualTvUserContextService(IUserManager userManager)
    {
        _userManager = userManager;
    }

    public Guid ResolveOwnerUserId(ChannelConfiguration channel)
    {
        if (Guid.TryParse(channel.OwnerUserId, out var configured)
            && _userManager.GetUserById(configured) is not null)
        {
            return configured;
        }

        var admin = _userManager.GetUsers()
            .FirstOrDefault(user => user.HasPermission(PermissionKind.IsAdministrator));

        if (admin is null)
        {
            return Guid.Empty;
        }

        channel.OwnerUserId = admin.Id.ToString("N");
        return admin.Id;
    }
}
