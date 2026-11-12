using System.Globalization;

namespace RunnerManager;

/// <summary>
/// Checks the GitHub names that become part of a gh command line. Every PC reports the repository and scope of its
/// runners, and a PC that is managed one way is not trusted, so these values are checked before gh is started and
/// wherever else they are accepted.
/// </summary>
internal static class GitHubNames
{
    private const int MaxOwnerLength = 39;
    private const int MaxRepositoryLength = 100;

    /// <summary>
    /// A user or organization name as GitHub allows it: letters, digits and '-', plus '_' (managed user accounts),
    /// starting with a letter or digit.
    /// </summary>
    internal static bool IsOwner(string? name)
        => name is { Length: > 0 and <= MaxOwnerLength } && char.IsAsciiLetterOrDigit(name[0])
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>A repository name as GitHub allows it: letters, digits, '-', '_' and '.', but not "." or "..".</summary>
    internal static bool IsRepositoryName(string? name)
        => name is { Length: > 0 and <= MaxRepositoryLength } and not ("." or "..")
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>True for exactly "owner/repo": no URL, no spaces, no further segments.</summary>
    internal static bool IsRepositoryPath(string? path)
    {
        if (path == null) return false;
        var slash = path.IndexOf('/');
        return slash > 0 && IsOwner(path[..slash]) && IsRepositoryName(path[(slash + 1)..]);
    }

    /// <summary>True for exactly "repos/owner/repo" or "orgs/name", as RunnerService.ApiScope writes them.</summary>
    internal static bool IsScope(string? scope)
    {
        if (scope == null) return false;
        if (scope.StartsWith(RepositoryScope, StringComparison.Ordinal)) return IsRepositoryPath(scope[RepositoryScope.Length..]);
        return scope.StartsWith(OrganizationScope, StringComparison.Ordinal) && IsOwner(scope[OrganizationScope.Length..]);
    }

    private const string RepositoryScope = "repos/";
    private const string OrganizationScope = "orgs/";
    private const string EndpointCharacters = "-._~/%?&=";

    /// <summary>
    /// True when the text can be the endpoint argument of gh api: a relative path under repos/ or orgs/ (or "user")
    /// made of plain URL characters, without "." or ".." segments, also when percent-encoded. A second check behind
    /// the name checks above: such an endpoint cannot be read as a flag or a gh placeholder, and cannot walk to
    /// another part of the API.
    /// </summary>
    internal static bool IsApiEndpoint(string? endpoint)
    {
        if (endpoint is not { Length: > 0 and <= 2000 }) return false;
        if (endpoint != "user" && !endpoint.StartsWith(RepositoryScope, StringComparison.Ordinal)
                               && !endpoint.StartsWith(OrganizationScope, StringComparison.Ordinal)) return false;
        if (!endpoint.All(c => char.IsAsciiLetterOrDigit(c) || EndpointCharacters.Contains(c))) return false;
        var query = endpoint.IndexOf('?');
        var path = query < 0 ? endpoint : endpoint[..query];
        return path.Split('/').All(segment =>
        {
            var plain = Uri.UnescapeDataString(segment);
            return plain.Length > 0 && plain is not ("." or "..") && !plain.Contains('/') && !plain.Contains('\\');
        });
    }

    /// <summary>
    /// True for an ETag as GitHub sends it (W/"..." or "..."): printable ASCII only, so it cannot end the header line
    /// it is sent back in.
    /// </summary>
    internal static bool IsETag(string? etag)
        => etag is { Length: > 0 and <= 200 } && etag.All(c => c is >= ' ' and <= '~');

    /// <summary>A run or runner ID as text: digits only, whatever the Windows language is.</summary>
    internal static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}
