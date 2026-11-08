namespace CodexSessionPulse.Infrastructure.Settings;

/// <summary>The verdict on a rollouts root override.</summary>
public enum SessionRootVerdict
{
    /// <summary>An existing directory inside the user profile with no reparse point on its path.</summary>
    Ok,

    /// <summary>The path is not absolute.</summary>
    NotAbsolute,

    /// <summary>The directory does not exist.</summary>
    NotFound,

    /// <summary>The directory or one of its parents is a reparse point (symlink, junction, mount point).</summary>
    ReparsePoint,

    /// <summary>Valid, but outside the user profile: accepted only after the user confirms it.</summary>
    OutsideProfile,
}

/// <summary>Checks a rollouts root override (spec §8.1).</summary>
public static class SessionRootCheck
{
    /// <summary>Checks a candidate rollouts root.</summary>
    /// <param name="path">The candidate directory.</param>
    /// <param name="userProfile">The user profile directory.</param>
    /// <returns>The verdict; <see cref="SessionRootVerdict.OutsideProfile"/> is the only one a confirmation can override.</returns>
    public static SessionRootVerdict Check(string path, string userProfile)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return SessionRootVerdict.NotAbsolute;
        }

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        if (!Directory.Exists(full))
        {
            return SessionRootVerdict.NotFound;
        }

        for (DirectoryInfo? directory = new(full); directory is not null; directory = directory.Parent)
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return SessionRootVerdict.ReparsePoint;
            }
        }

        string profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userProfile));

        return full.Equals(profile, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? SessionRootVerdict.Ok
            : SessionRootVerdict.OutsideProfile;
    }
}
