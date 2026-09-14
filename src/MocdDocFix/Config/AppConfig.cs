namespace MocdDocFix.Config;

public sealed class AppConfig
{
    public Dictionary<string, EnvironmentConfig> Environments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The seven in-scope services (spec 2026-09-13 section 2). Configuration, not code —
    /// this list only seeds config.json.
    /// </summary>
    public List<Guid> ServiceCatalogues { get; set; } = new();

    /// <summary>Root for downloads, reports, state and logs. Outside the repo by design.</summary>
    public string DataRoot { get; set; } = @"D:\mocd-docfix-data";

    /// <summary>The DevOps backlog, consulted during the scan. Read-only.</summary>
    public AdoConfig Ado { get; set; } = new();

    public static AppConfig Default() => new()
    {
        ServiceCatalogues = new List<Guid>
        {
            // Membership Managment (6bcb221c-6c2b-f111-b119-005056010908) was removed from scope
            // on 2026-09-13 at the operator's instruction. Do not add it back without asking.
            Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"), // Employee Appointment Request
            Guid.Parse("3ff27d73-653e-f111-b119-005056010908"), // General Assembly Meeting Request
            Guid.Parse("d2744b68-aa50-f111-b119-005056010908"), // GAM - Nomination List Request
            Guid.Parse("24db2387-c15d-f111-b119-005056010908"), // GAM - Attendance
            Guid.Parse("35105602-2b5f-f111-b119-005056010908"), // GAM - Update (Reschedule)
            Guid.Parse("d8155dcc-635e-f111-b119-005056010908"), // GAM - Minutes of Meeting
            Guid.Parse("930f636a-077a-f111-b119-005056010908"), // By-Laws Amendment Requests
        }
    };
}

/// <summary>
/// The Azure DevOps backlog, read during the scan to ask which service a document type belongs
/// to. Read-only: the tool runs WIQL queries and fetches titles, and writes nothing back.
/// The password is not here — it lives in the same encrypted store as the CRM password.
/// </summary>
public sealed class AdoConfig
{
    public string CollectionUrl { get; set; } = "https://devops.mocd.gov.ae/MOCD";

    public string Project { get; set; } = "NPO - Phase 2";

    public string User { get; set; } = "";

    /// <summary>Blank unless the account needs one — the workstation domain differs from MOCD's.</summary>
    public string Domain { get; set; } = "";

    /// <summary>
    /// A local copy of the backlog: the synced user stories and the files attached to them.
    /// The live search can only see work item titles, because this server refuses a full-text
    /// query, and the document lists are in the story bodies and in the attached spreadsheets.
    /// Blank turns it off.
    /// </summary>
    public string LocalCopy { get; set; } =
        @"D:\Claude code for mocd\mocd-knowledge-base\kb\user-stories";

    /// <summary>
    /// Where workbooks fetched from the backlog are saved, and where a file downloaded by hand
    /// can be dropped for the search to read.
    /// </summary>
    public string DropFolder { get; set; } = @"D:\mocd-docfix-data\backlog-files";

    /// <summary>False turns the cross-check off without forgetting the settings.</summary>
    public bool Enabled { get; set; } = true;
}
