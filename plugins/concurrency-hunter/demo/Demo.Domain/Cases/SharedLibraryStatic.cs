namespace Demo.Domain.Cases.SharedLibraryStatic;

/// <summary>Static state shared by applications that reference the same library.</summary>
public static class LastSync
{
    public static string? Source;
}
