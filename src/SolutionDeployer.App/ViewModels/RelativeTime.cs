namespace SolutionDeployer.App.ViewModels;

/// <summary>Short "how long ago" text for the target rows, e.g. "just now", "5 min ago", "3 days ago".</summary>
public static class RelativeTime
{
    public static string Describe(DateTimeOffset when, DateTimeOffset now)
    {
        var age = now - when;
        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1))
            return $"{(int)age.TotalHours} h ago";
        if (age < TimeSpan.FromDays(2))
            return "yesterday";
        if (age < TimeSpan.FromDays(30))
            return $"{(int)age.TotalDays} days ago";
        return when.ToLocalTime().ToString("yyyy-MM-dd");
    }
}
