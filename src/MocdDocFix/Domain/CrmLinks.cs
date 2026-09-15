namespace MocdDocFix.Domain;

/// <summary>
/// Deep links into CRM. Both live here rather than on the writers that print them, because
/// every mode prints at least one and two spellings of the same URL is one too many.
/// </summary>
public static class CrmLinks
{
    public static string Document(string crmUrl, Guid id) => Link(crmUrl, "mocd_document", id);

    public static string DocumentFile(string crmUrl, Guid id) => Link(crmUrl, "mocd_documentfile", id);

    private static string Link(string crmUrl, string entity, Guid id) =>
        $"{crmUrl.TrimEnd('/')}/main.aspx?etn={entity}&pagetype=entityrecord&id={id}";
}
