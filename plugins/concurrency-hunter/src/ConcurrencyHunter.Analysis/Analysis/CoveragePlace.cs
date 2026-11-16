using System.Globalization;
using ConcurrencyHunter.CallGraph;

namespace ConcurrencyHunter.Analysis;

/// <summary>A coverage location, either a program path and line or an admitted library's assembly and root member.</summary>
/// <param name="Path">The target-relative path or assembly-qualified declaration id.</param>
/// <param name="Line">The program line, absent for a library member.</param>
public sealed record CoveragePlace(string Path, int? Line)
{
    /// <summary>Renders a site's location independently of the machine's library source paths.</summary>
    /// <param name="bodyId">The body containing the site.</param>
    /// <param name="span">The site's already normalized source span.</param>
    /// <param name="program">The program index, including nested bodies' root members.</param>
    /// <param name="admission">The library admission of this scope analysis.</param>
    public static CoveragePlace Of(string bodyId, SourceSpan span, ProgramIndex? program = null, CallSiteAdmission? admission = null)
    {
        if (admission?.IsLibraryBody(bodyId) == true)
        {
            var member = program!.MemberOfNestedBody(bodyId)?.MethodId ?? bodyId;
            return new(member["body:".Length..], null);
        }
        return new(span.Path, span.StartLine);
    }

    // bounded: formats one path and at most one line number.
    public override string ToString() => Line is { } line ? $"{Path}:{line.ToString(CultureInfo.InvariantCulture)}" : Path;
}
