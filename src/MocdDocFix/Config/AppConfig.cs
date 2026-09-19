namespace MocdDocFix.Config;

public sealed class AppConfig
{
    public Dictionary<string, EnvironmentConfig> Environments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The services this run works on. Seeded from <see cref="InScopeServices"/> and stored in
    /// config.json, where a service may be added by hand.
    ///
    /// Choosing "every service catalogue" at startup sets this aside in favour of asking CRM
    /// what there actually is.
    /// </summary>
    public List<Guid> ServiceCatalogues { get; set; } = new();

    /// <summary>Root for downloads, reports, state and logs. Outside the repo by design.</summary>
    public string DataRoot { get; set; } = @"D:\mocd-docfix-data";

    /// <summary>
    /// The services this tool is for, with the names the operator knows them by.
    ///
    /// The names are data rather than comments because the scope question lists them: being
    /// asked to choose between "8 services" and "every service catalogue" without being told
    /// which eight is not a choice anybody can make.
    /// </summary>
    public static readonly IReadOnlyList<(Guid Id, string Name)> InScopeServices = new[]
    {
        (Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"), "Employee Appointment Request"),
        (Guid.Parse("3ff27d73-653e-f111-b119-005056010908"), "General Assembly Meeting Request"),
        (Guid.Parse("d2744b68-aa50-f111-b119-005056010908"), "GAM - Nomination List Request"),
        (Guid.Parse("24db2387-c15d-f111-b119-005056010908"), "GAM - Attendance"),
        (Guid.Parse("35105602-2b5f-f111-b119-005056010908"), "GAM - Update (Reschedule)"),
        (Guid.Parse("d8155dcc-635e-f111-b119-005056010908"), "GAM - Minutes of Meeting"),
        (Guid.Parse("930f636a-077a-f111-b119-005056010908"), "By-Laws Amendment Requests"),

        // Taken out of scope on 2026-09-13 and put back on 2026-09-18, both at the operator's
        // instruction. Seven became eight.
        (Guid.Parse("6bcb221c-6c2b-f111-b119-005056010908"), "Membership Managment"),
    };

    /// <summary>
    /// The name of a service this tool knows, or null for one somebody added by hand — those
    /// are shown by their id, which is the only thing we can honestly say about them.
    /// </summary>
    public static string? NameOf(Guid catalogue)
    {
        foreach (var (id, name) in InScopeServices)
            if (id == catalogue) return name;

        return null;
    }

    public static AppConfig Default() => new()
    {
        ServiceCatalogues = InScopeServices.Select(s => s.Id).ToList()
    };
}
