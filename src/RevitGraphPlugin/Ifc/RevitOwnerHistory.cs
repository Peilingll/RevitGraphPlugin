using System.Reflection;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using GeometryGym.Ifc;

namespace RevitGraphPlugin.Ifc;

/// <summary>
/// Overrides the OwnerHistory chain that <c>new IfcProject(db, name)</c>
/// auto-creates with values that match Revit's own IFC exporter output.
///
/// All hard-coded values are sourced from Autodesk/revit-ifc
/// (<see href="https://github.com/Autodesk/revit-ifc"/>) master branch.
/// See <c>doc_process/2026-05-20-revit-ifc-source-survey.md</c> for the
/// permalinks and the rationale for every constant in this file.
/// </summary>
public static class RevitOwnerHistory
{
    /// <summary>
    /// IfcApplication.ApplicationIdentifier — the IFC "identifier token" Revit
    /// stamps onto every exported IFC application entity.
    ///
    /// Source: revit-ifc <c>Exporter.cs</c> L2882
    /// (<c>string productIdentifier = "Revit";</c>).
    /// Re-verify against a fresh baseline if Revit's IFC exporter is upgraded.
    /// </summary>
    private const string ApplicationIdentifierToken = "Revit";

    /// <summary>
    /// POCO with the Revit-derived strings needed to override the chain.
    /// Lets the helper be unit-tested without a live <see cref="Document"/>.
    /// </summary>
    /// <param name="ProductFullName">
    /// e.g. <c>"Autodesk Revit 2025 (ENG)"</c>. Built from
    /// <c>Application.VersionName + GetLanguageExtension(LanguageType)</c>
    /// — Revit already prefixes "Autodesk Revit " inside <c>VersionName</c>.
    /// </param>
    /// <param name="ProductVersion">e.g. <c>"2025"</c> from <see cref="Application.VersionNumber"/>.</param>
    /// <param name="Author">
    /// Revit project's Author field (preferred) falling back to
    /// <see cref="Application.Username"/>. Empty string is acceptable.
    /// </param>
    /// <param name="OrganizationName">
    /// <c>ProjectInformation.OrganizationName</c>. Null is the typical value
    /// for a fresh Architectural template; Revit serialises null as STEP <c>$</c>.
    /// </param>
    /// <param name="OrganizationDescription">
    /// <c>ProjectInformation.OrganizationDescription</c>. Same null-as-default
    /// rule as above.
    /// </param>
    public sealed record Source(
        string ProductFullName,
        string ProductVersion,
        string Author,
        string? OrganizationName,
        string? OrganizationDescription);

    /// <summary>
    /// Revit-side entry point. Builds a <see cref="Source"/> from the live
    /// document and delegates to the testable overload.
    /// </summary>
    public static void Override(IfcProject project, Document doc)
    {
        var app = doc.Application;
        var projInfo = doc.ProjectInformation;

        // Author resolution mirrors revit-ifc Exporter.cs L3175-3191:
        // projectInfo.Author first, falling back to Application.Username.
        var author = projInfo?.Author;
        if (string.IsNullOrEmpty(author))
            author = app.Username ?? string.Empty;

        // ApplicationFullName template — revit-ifc Exporter.cs L2880.
        // VersionName already contains "Autodesk Revit 2025"; we only append the
        // language extension.
        var productFullName = app.VersionName + GetLanguageExtension(app.Language);

        var source = new Source(
            ProductFullName: productFullName,
            ProductVersion: app.VersionNumber,
            Author: author,
            OrganizationName: SafeRead(() => projInfo?.OrganizationName),
            OrganizationDescription: SafeRead(() => projInfo?.OrganizationDescription));

        Override(project, source);
    }

    /// <summary>
    /// Testable overload. Walks the ggifc OwnerHistory chain and writes the
    /// values from <paramref name="src"/> onto the existing entities.
    /// </summary>
    public static void Override(IfcProject project, Source src)
    {
        var ownerHistory = project.OwnerHistory
            ?? throw new InvalidOperationException(
                "IfcProject.OwnerHistory is null; expected ggifc to auto-create it.");

        // ChangeAction — revit-ifc Exporter.cs L3245 (IFCChangeAction.NoChange).
        ownerHistory.ChangeAction = IfcChangeActionEnum.NOCHANGE;

        // State — Revit passes null literal to CreateOwnerHistory (Exporter.cs L3244).
        // ggifc exposes State as a non-nullable IfcStateEnum, so try reflection to
        // clear the backing field. Fallback (kept = NOTDEFINED) is captured in the
        // log; not a blocking issue for the rest of the chain.
        StateWasClearedToNull = TrySetStateNull(ownerHistory);

        // Person — revit-ifc Exporter.cs L3231. Identification = null, FamilyName +
        // GivenName parsed from author string.
        var person = ownerHistory.OwningUser.ThePerson;
        var (familyName, givenName) = ParseAuthor(src.Author);
        person.GivenName = givenName;
        person.FamilyName = familyName;
        TrySetToNull(person, "Identification");

        // User Organization — revit-ifc Exporter.cs L3233. Name and Description are
        // null for an Architectural template with no OrganizationName configured.
        var userOrg = ownerHistory.OwningUser.TheOrganization;
        TrySetNullableString(userOrg, "Name", src.OrganizationName);
        TrySetNullableString(userOrg, "Description", src.OrganizationDescription);

        // Application — revit-ifc Exporter.cs L2884-L2886.
        var application = ownerHistory.OwningApplication;
        application.ApplicationFullName = src.ProductFullName;
        application.ApplicationIdentifier = ApplicationIdentifierToken;
        application.Version = src.ProductVersion;

        // Developer Organization (attached to the Application) — Exporter.cs L2884.
        // CreateOrganization(file, null, productFullName, null, null, null): only Name
        // is set, every other field is null.
        var developerOrg = application.ApplicationDeveloper;
        developerOrg.Name = src.ProductFullName;
    }

    /// <summary>
    /// True when <see cref="Override(IfcProject, Source)"/> last succeeded in
    /// clearing <c>IfcOwnerHistory.State</c> via reflection (option A); false
    /// when ggifc forced the fallback path (option C, state stays NOTDEFINED).
    /// Diagnostic only — exposed so the step's research log can record which
    /// path was taken on a given ggifc build.
    /// </summary>
    public static bool StateWasClearedToNull { get; private set; }

    /// <summary>
    /// Mapping copied verbatim from revit-ifc <c>Exporter.cs</c> L2806-L2845.
    /// Returns the leading-space + parenthesised language code that Revit's IFC
    /// exporter appends to <c>VersionName</c>. Unknown languages return an empty
    /// string, matching Autodesk's <c>default</c> branch.
    /// </summary>
    public static string GetLanguageExtension(LanguageType langType) =>
        langType switch
        {
            LanguageType.English_USA          => " (ENU)",
            LanguageType.German               => " (DEU)",
            LanguageType.Spanish              => " (ESP)",
            LanguageType.French               => " (FRA)",
            LanguageType.Italian              => " (ITA)",
            LanguageType.Dutch                => " (NLD)",
            LanguageType.Chinese_Simplified   => " (CHS)",
            LanguageType.Chinese_Traditional  => " (CHT)",
            LanguageType.Japanese             => " (JPN)",
            LanguageType.Korean               => " (KOR)",
            LanguageType.Russian              => " (RUS)",
            LanguageType.Czech                => " (CSY)",
            LanguageType.Polish               => " (PLK)",
            LanguageType.Hungarian            => " (HUN)",
            LanguageType.Brazilian_Portuguese => " (PTB)",
            LanguageType.English_GB           => " (ENG)",
            _ => "",
        };

    /// <summary>
    /// Simplified port of revit-ifc's <c>NamingUtil.ParseName</c>. The real Revit
    /// utility splits comma/space-delimited strings into family/given/middle/etc.
    /// For the empty Architectural template our author resolves to
    /// <see cref="Application.Username"/>, which has no comma, so the simplified
    /// behaviour ("whole string is givenName, familyName empty") matches the
    /// baseline. Extend when a richer Author value appears in practice.
    /// </summary>
    private static (string familyName, string givenName) ParseAuthor(string author)
    {
        if (string.IsNullOrEmpty(author))
            return ("", "");
        return ("", author);
    }

    /// <summary>
    /// Try to clear <c>IfcOwnerHistory.State</c> by writing null to its non-public
    /// backing field. Returns true on success, false if ggifc's representation is
    /// a non-nullable value-type field that rejects null (option C fallback).
    /// </summary>
    private static bool TrySetStateNull(IfcOwnerHistory history)
    {
        var type = typeof(IfcOwnerHistory);
        // ggifc usually names backing fields with an "m" prefix, e.g. mState.
        foreach (var candidate in new[] { "mState", "_State", "_state", "state" })
        {
            var field = type.GetField(candidate, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field is null) continue;
            try
            {
                field.SetValue(history, null);
                return true;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Try to write null to a string-valued property by name. Useful for
    /// <c>IfcPerson.Identification</c> and friends where ggifc may have either
    /// a nullable-string setter (works) or a typed value struct (rejects null).
    /// </summary>
    private static void TrySetToNull(object target, string propertyName)
    {
        var prop = target.GetType().GetProperty(propertyName);
        if (prop is null || !prop.CanWrite) return;
        try { prop.SetValue(target, null); }
        catch { /* leave whatever ggifc had as the default */ }
    }

    /// <summary>
    /// Set a nullable string property: prefer null when the input is null;
    /// otherwise write the supplied value. ggifc occasionally rejects null on
    /// fields it treats as required — fall back to empty string in that case.
    /// </summary>
    private static void TrySetNullableString(object target, string propertyName, string? value)
    {
        var prop = target.GetType().GetProperty(propertyName);
        if (prop is null || !prop.CanWrite) return;
        try { prop.SetValue(target, value); }
        catch
        {
            try { prop.SetValue(target, ""); } catch { /* give up silently */ }
        }
    }

    /// <summary>
    /// Some Revit project-information properties throw
    /// <see cref="Autodesk.Revit.Exceptions.InvalidOperationException"/> when read
    /// on a transient document. Wrap reads in a try/catch so the helper does not
    /// abort the whole sync.
    /// </summary>
    private static string? SafeRead(Func<string?> read)
    {
        try { return read(); }
        catch { return null; }
    }
}
