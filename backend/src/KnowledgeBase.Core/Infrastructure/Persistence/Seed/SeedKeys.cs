namespace KnowledgeBase.Core.Infrastructure.Persistence.Seed;

/// <summary>
/// Natural keys of the default settings the seed creates. They mirror the frontend's codes
/// (documentTypes.ts, requirements.ts) so both sides use the same names.
/// </summary>
public static class SeedKeys
{
    public static class Years
    {
        public const string Ay2025 = "2025-26";    // 2025-08-11 → 2026-07-31
        public const string Ay2026 = "2026-27";    // 2026-08-10 → 2027-07-23
    }

    public static class Terms
    {
        public const string Ay2025First = "2025-1";    // Closed
        public const string Ay2025Second = "2025-2";   // Closed
        public const string Ay2025Summer = "2025-S";   // Closed
        public const string Ay2026First = "2026-1";    // Active
        public const string Ay2026Second = "2026-2";   // Planned
        public const string Ay2026Summer = "2026-S";   // Planned
    }

    /// <summary>DocumentType.Code values (frontend documentTypes.ts).</summary>
    public static class DocumentTypes
    {
        public const string Syllabus = "SYL";
        public const string OutcomesMatrix = "COM";
        public const string Rubric = "RUB";
        public const string ReadingList = "RDL";
        public const string LabManual = "LAB";
        public const string ResearchProposal = "RPR";
        public const string ThesisGuidelines = "THG";
        public const string Memorandum = "MEMO";
        public const string Accreditation = "ACC";
        public const string Policy = "AP";
        public const string BoardResolution = "BR";
        public const string FacultyLoad = "FLA";
        public const string AccomplishmentReport = "ACR";
        public const string WorkPlan = "WPL";
        public const string Inventory = "INV";
        public const string Incident = "INC";
        public const string Form = "FRM";
        public const string CurriculumVitae = "CV";
    }

    /// <summary>SubmissionType.Code values (frontend requirements.ts / scopedRequirements.ts).</summary>
    public static class SubmissionTypes
    {
        public const string Syllabus = "SYLLABUS";             // program-scoped, one per course, AS only
        public const string Proposal = "PROPOSAL";             // program-scoped, AS only
        public const string Thesis = "THESIS";                 // program-scoped, AS only
        public const string LabManual = "LAB";                 // program-scoped, one per course, AS only
        public const string Accreditation = "ACCREDITATION";   // program-scoped, AS then Dean
        public const string Policy = "POLICY";                 // not program-scoped, unit head then Dean
        public const string Incident = "INCIDENT";             // non-academic, Head only
        public const string Inventory = "INVENTORY";           // non-academic, Head only
        public const string Semestral = "SEMESTRAL";           // "Semestral report" (admin college/department rules)
        public const string WorkPlan = "WORKPLAN";             // "Annual work plan"
        public const string Project = "PROJECT";               // "Project docs"
        public const string Portfolio = "PORTFOLIO";           // "Course portfolio"
    }
}
