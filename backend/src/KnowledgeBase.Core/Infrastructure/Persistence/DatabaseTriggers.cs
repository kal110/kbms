namespace KnowledgeBase.Core.Infrastructure.Persistence;

/// <summary>
/// Invariants the database enforces even if a bug or a manual query bypasses the services
/// (frame 11): review actions and audit events are append-only, a stored version's bytes never
/// change, and a document's year/term tags match its type's AcademicTag. Applied by the initial
/// migration.
/// </summary>
public static class DatabaseTriggers
{
    public const string Create = """
        CREATE OR REPLACE FUNCTION kb_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION '% rows are append-only', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
        END $$;

        CREATE TRIGGER trg_review_actions_append_only
            BEFORE UPDATE OR DELETE ON review_actions
            FOR EACH ROW EXECUTE FUNCTION kb_append_only();

        CREATE TRIGGER trg_audit_events_append_only
            BEFORE UPDATE OR DELETE ON audit_events
            FOR EACH ROW EXECUTE FUNCTION kb_append_only();

        CREATE OR REPLACE FUNCTION kb_document_versions_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.document_id IS DISTINCT FROM OLD.document_id
               OR NEW.version_number IS DISTINCT FROM OLD.version_number
               OR NEW.object_key IS DISTINCT FROM OLD.object_key
               OR NEW.sha256 IS DISTINCT FROM OLD.sha256
               OR NEW.file_size_bytes IS DISTINCT FROM OLD.file_size_bytes THEN
                RAISE EXCEPTION 'A stored document version cannot change its file' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END $$;

        CREATE TRIGGER trg_document_versions_immutable
            BEFORE UPDATE ON document_versions
            FOR EACH ROW EXECUTE FUNCTION kb_document_versions_immutable();

        CREATE OR REPLACE FUNCTION kb_documents_period() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            tag text;
            term_year uuid;
        BEGIN
            SELECT academic_tag INTO tag FROM document_types WHERE id = NEW.document_type_id;
            IF tag = 'Term' THEN
                IF NEW.academic_year_id IS NULL OR NEW.academic_term_id IS NULL THEN
                    RAISE EXCEPTION 'This document type needs an academic year and term' USING ERRCODE = 'check_violation';
                END IF;
                SELECT academic_year_id INTO term_year FROM academic_terms WHERE id = NEW.academic_term_id;
                IF term_year IS DISTINCT FROM NEW.academic_year_id THEN
                    RAISE EXCEPTION 'The term does not belong to the academic year' USING ERRCODE = 'check_violation';
                END IF;
            ELSIF tag = 'Year' THEN
                IF NEW.academic_year_id IS NULL OR NEW.academic_term_id IS NOT NULL THEN
                    RAISE EXCEPTION 'This document type needs an academic year and no term' USING ERRCODE = 'check_violation';
                END IF;
            ELSIF NEW.academic_year_id IS NOT NULL OR NEW.academic_term_id IS NOT NULL THEN
                RAISE EXCEPTION 'This document type takes no academic year or term' USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END $$;

        CREATE TRIGGER trg_documents_period
            BEFORE INSERT OR UPDATE OF document_type_id, academic_year_id, academic_term_id ON documents
            FOR EACH ROW EXECUTE FUNCTION kb_documents_period();
        """;

    public const string Drop = """
        DROP TRIGGER IF EXISTS trg_documents_period ON documents;
        DROP TRIGGER IF EXISTS trg_document_versions_immutable ON document_versions;
        DROP TRIGGER IF EXISTS trg_audit_events_append_only ON audit_events;
        DROP TRIGGER IF EXISTS trg_review_actions_append_only ON review_actions;
        DROP FUNCTION IF EXISTS kb_documents_period();
        DROP FUNCTION IF EXISTS kb_document_versions_immutable();
        DROP FUNCTION IF EXISTS kb_append_only();
        """;
}
