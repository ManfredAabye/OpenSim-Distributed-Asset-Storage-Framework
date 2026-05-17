using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenMetaverse;

namespace OpenSim.DataS3.Security
{
    public sealed class DataS3RequestContext
    {
        private static readonly AsyncLocal<DataS3RequestContext?> s_current = new AsyncLocal<DataS3RequestContext?>();

        public static DataS3RequestContext? Current
        {
            get => s_current.Value;
            set => s_current.Value = value;
        }

        public UUID UserId { get; init; }

        public bool IsAuthenticated { get; init; }

        public string? RemoteIp { get; init; }

        public IReadOnlyCollection<DataS3Role> Roles { get; init; } = Array.Empty<DataS3Role>();

        public bool HasRole(DataS3Role role)
        {
            return Roles.Any(r => r == role);
        }
    }
}
