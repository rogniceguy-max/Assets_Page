using MuYingDrive.NativeWpf.Services;

namespace MuYingDrive.NativeWpf.Models;

/// <summary>Authenticated application session; NAS access stays behind the file service.</summary>
public sealed record DriveSession(V2UserSession User, string SessionToken)
{
    public bool IsVisitor => User.Id == 0 || string.Equals(User.GroupType, "visitor", StringComparison.OrdinalIgnoreCase);
    public bool IsReadOnly => IsVisitor;
}
