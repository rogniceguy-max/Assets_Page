using MuYingDrive.NativeWpf.Services;

namespace MuYingDrive.NativeWpf.Models;

/// <summary>Authenticated V2 user session and the current native SMB connection result.</summary>
public sealed record DriveSession(V2UserSession User, string SessionToken, SmbConnectionResult SmbConnection);
