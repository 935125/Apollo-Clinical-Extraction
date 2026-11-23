-- Run as the migration administrator after V0006. Contains no passwords.
-- Switch the application connection to cx_runtime and Persistence:AutoMigrate=false
-- after this succeeds. Existing object ownership remains with the migration role.
BEGIN;
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cx_runtime') THEN
    CREATE ROLE cx_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
  END IF;
  EXECUTE format('GRANT CONNECT ON DATABASE %I TO cx_runtime', current_database());
END $$;
GRANT USAGE ON SCHEMA public TO cx_runtime;
GRANT SELECT, INSERT, UPDATE ON public.extraction_runs, public.extracted_medications,
  public.extracted_investigations, public.oov_quarantine_items TO cx_runtime;
-- Existing repository replaces children when a run is re-saved, even for a first insert.
GRANT DELETE ON public.extracted_medications, public.extracted_investigations TO cx_runtime;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO cx_runtime;
-- No parent/quarantine DELETE, DDL, ownership, role administration, or database provisioning.
COMMIT;
