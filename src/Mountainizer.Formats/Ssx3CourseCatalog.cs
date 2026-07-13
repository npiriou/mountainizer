namespace Mountainizer.Formats;

public sealed record Ssx3CourseDefinition(
    string Code,
    string Name,
    int Peak,
    string Discipline,
    IReadOnlyList<string> AreaNames)
{
    public string DisplayName => $"Peak {Peak}  •  {Name} — {Discipline}  [{Code}]";
}

/// <summary>
/// Maps the BAM.SDB streaming-location names to the playable SSX 3 courses.
/// A course is assembled from its event area plus the shared mountain and
/// connector areas that the game streams with it.
/// </summary>
public static class Ssx3CourseCatalog
{
    public static IReadOnlyList<Ssx3CourseDefinition> Courses { get; } =
    [
        Course("ARA1", "Snow Jam", 1, "Race", "A", "A_ARA1", "ARA1", "ARA1_B", "B", "ASKY", "BSKY"),
        Course("ASS1", "R&B", 1, "Slopestyle", "A", "A_ASS1", "ASS1", "ASKY"),
        Course("BRA2", "Metro-City", 1, "Race", "B", "B_BRA2", "BRA2", "BSKY"),
        Course("ABA1", "Crow's Nest", 1, "Big Air", "A", "A_ABA1", "ABA1", "ASKY"),
        Course("BHP1", "Disfunktion", 1, "Super Pipe", "B", "B_BHP1", "BHP1", "BSKY"),
        Course("ABC1", "Happiness", 1, "Backcountry", "ABC1", "ABC1_A", "A", "ASKY"),

        Course("CRA3", "Ruthless Ridge", 2, "Race", "C", "C_CRA3", "CRA3", "CRA3_D", "D", "CSKY", "DSKY"),
        Course("DRA4", "Intimidator", 2, "Race", "D", "D_DRA4", "DRA4", "DRA4_A", "DSKY"),
        Course("DSS2", "Style Mile", 2, "Slopestyle", "D", "D_DSS2", "DSS2", "DSKY"),
        Course("CBA2", "Launch Time", 2, "Big Air", "C", "C_CBA2", "CBA2", "CSKY"),
        Course("CHP2", "Schizophrenia", 2, "Super Pipe", "C", "C_CHP2", "CHP2", "CSKY"),
        Course("DBC2", "Ruthless", 2, "Backcountry", "DBC2", "DBC2_D", "D", "DSKY"),

        Course("ERA5", "Gravitude", 3, "Race", "E", "E_ERA5", "ERA5", "ERA5_C", "ESKY"),
        Course("ESS3", "Kick Doubt", 3, "Slopestyle", "E", "E_ESS3", "ESS3", "ESKY"),
        Course("EBA3", "Much-2-Much", 3, "Big Air", "E", "E_EBA3", "EBA3", "ESKY"),
        Course("EHP3", "Perpendiculous", 3, "Super Pipe", "E", "E_EHP3", "EHP3", "ESKY"),
        Course("EBC3", "The Throne", 3, "Backcountry", "EBC3", "EBC3_E", "E", "ESKY")
    ];

    public static Ssx3CourseDefinition? Find(string code) =>
        Courses.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<Ssx3LevelArea> ResolveAreas(Ssx3Sdb sdb, Ssx3CourseDefinition course)
    {
        var byName = sdb.Areas.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        return course.AreaNames.Where(byName.ContainsKey).Select(x => byName[x]).DistinctBy(x => x.OriginalIndex).ToArray();
    }

    private static Ssx3CourseDefinition Course(string code, string name, int peak, string discipline, params string[] areas) =>
        new(code, name, peak, discipline, areas);
}
