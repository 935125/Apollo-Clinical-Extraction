using Microsoft.AspNetCore.Identity;
using Apollo.Backend.Storage;

namespace Apollo.Backend.Emr;

public static class EmrAdmin
{
    public static async Task Run(WebApplicationBuilder builder, bool migrate, bool seed)
    {
        var store = new EmrStore(builder.Configuration);
        if(migrate)
        {
            // Existing platform schema must already be installed. No auto-DDL on app startup.
            await using var c=await store.Open(); await using var tx=await c.BeginTransactionAsync();
            string sql;
            await using (var stream = typeof(EmrAdmin).Assembly.GetManifestResourceStream("Apollo.Backend.db.platform.migrations.V0009__doctor_emr.sql"))
            {
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    sql = await reader.ReadToEndAsync();
                }
                else
                {
                    string diskPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "backend", "db", "platform", "migrations", "V0009__doctor_emr.sql"));
                    if (!File.Exists(diskPath)) diskPath = @"D:\AI Extraction\backend\db\platform\migrations\V0009__doctor_emr.sql";
                    sql = await File.ReadAllTextAsync(diskPath);
                }
            }
            await EmrStore.Execute(c, sql, default, tx); await tx.CommitAsync();
            Console.WriteLine("Stage 9 additive EMR migration applied.");
        }
        if(!seed) return;
        if(!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Synthetic seeding is Development-only.");
        string password=builder.Configuration["Emr:Seed:Password"] ?? "";
        if(password.Length<16 || password.Length>1024) throw new InvalidOperationException("Supply Emr:Seed:Password through protected local configuration (16–1024 characters).");
        string login=(builder.Configuration["Emr:Seed:Login"] ?? "doctor.krishnan@apollo.local").Trim().ToLowerInvariant();
        string name=builder.Configuration["Emr:Seed:DisplayName"] ?? "Dr. K. Krishnan";
        string role=builder.Configuration["Emr:Seed:Role"] ?? "Doctor";
        if(login.Length>256 || name.Length>100 || role is not ("Doctor" or "ClinicalReviewer" or "Administrator")) throw new InvalidOperationException("Invalid seed configuration.");
        await using var connection=await store.Open(); await using var transaction=await connection.BeginTransactionAsync();
        Guid doctor=Guid.NewGuid();
        await EmrStore.Execute(connection,"INSERT INTO emr_doctors(id,login,display_name,role,password_hash) VALUES(@id,@login,@name,@role,@hash) ON CONFLICT(login) DO NOTHING",default,transaction,
            ("id",doctor),("login",login),("name",name),("role",role),("hash",new PasswordHasher<string>().HashPassword(login,password)));
        await using(var cmd=EmrStore.Command(connection,"SELECT id FROM emr_doctors WHERE login=@login",transaction,("login",login))) doctor=(Guid)(await cmd.ExecuteScalarAsync())!;

        DateTimeOffset fixedDate = new DateTimeOffset(2026, 1, 28, 11, 24, 0, TimeSpan.FromHours(5.5)).ToUniversalTime();
        for(int i=0;i<4;i++)
        {
            Guid patient=Guid.NewGuid(),encounter=Guid.NewGuid();
            string pRef = $"P{100234+i}";
            await EmrStore.Execute(connection,"INSERT INTO emr_patients(id,reference,synthetic) VALUES(@id,@ref,true) ON CONFLICT(reference) DO NOTHING",default,transaction,("id",patient),("ref",pRef));
            await using(var cmd=EmrStore.Command(connection,"SELECT id FROM emr_patients WHERE reference=@ref",transaction,("ref",pRef))) patient=(Guid)(await cmd.ExecuteScalarAsync())!;

            string reference=$"ENC-{100567+i}";
            DateTimeOffset encDate = i < 2 ? fixedDate : fixedDate.AddDays(-1);
            string ctx = i==0?"General medicine · Follow-up consultation":i==1?"General medicine · Outpatient consultation":i==2?"Internal medicine · Review consultation":"Cardiology · Routine consultation";
            await EmrStore.Execute(connection,"INSERT INTO emr_encounters(id,reference,patient_id,encounter_date,context) VALUES(@id,@ref,@patient,@date,@context) ON CONFLICT(reference) DO NOTHING",default,transaction,
                ("id",encounter),("ref",reference),("patient",patient),("date",encDate),("context",ctx));
            await using(var cmd=EmrStore.Command(connection,"SELECT id FROM emr_encounters WHERE reference=@ref",transaction,("ref",reference))) encounter=(Guid)(await cmd.ExecuteScalarAsync())!;
            await EmrStore.Execute(connection,"INSERT INTO emr_encounter_assignments(encounter_id,doctor_id) VALUES(@encounter,@doctor) ON CONFLICT DO NOTHING",default,transaction,("encounter",encounter),("doctor",doctor));

            // Seed initial note states matching the visual baseline
            if (i == 0) // Encounter 0: Signed & Completed
            {
                Guid noteId = Guid.NewGuid();
                string noteText = "Follow-up for hypertension.\nContinue amlodipine 5 mg orally once daily for 7 days.\nOrder complete blood count tomorrow.\nReview in 1 week.";
                string hash = EmrHash.Text(noteText);
                await EmrStore.Execute(connection, """
                    INSERT INTO emr_notes(id,encounter_id,author_id,current_version,status,draft_text,created_at,updated_at)
                    VALUES(@note,@enc,@doc,1,'Signed',@text,@date,@date) ON CONFLICT DO NOTHING;
                    INSERT INTO emr_note_versions(note_id,version,author_id,encounter_id,source_text,source_hash,signed_at,created_at)
                    VALUES(@note,1,@doc,@enc,@text,@hash,@date,@date) ON CONFLICT DO NOTHING;
                    INSERT INTO emr_outbox_events(event_id,event_type,schema_version,note_id,note_version,correlation_id,occurred_at,publication_status,published_at,processing_status)
                    VALUES(@event,'ClinicalNoteSigned',1,@note,1,@corr,@date,'Published',@date,'Completed') ON CONFLICT DO NOTHING;
                    """, default, transaction, ("note", noteId), ("enc", encounter), ("doc", doctor), ("text", noteText), ("hash", hash), ("date", fixedDate),
                    ("event", Guid.NewGuid()), ("corr", Guid.NewGuid()));
            }
            else if (i == 1) // Encounter 1: Draft Note
            {
                Guid noteId = Guid.NewGuid();
                string noteText = "Patient presents with persistent cough for 2 weeks.\nPrescribe azithromycin 500 mg once daily for 3 days.\nAdvise chest X-ray if symptoms persist.";
                string hash = EmrHash.Text(noteText);
                await EmrStore.Execute(connection, """
                    INSERT INTO emr_notes(id,encounter_id,author_id,current_version,status,draft_text,created_at,updated_at)
                    VALUES(@note,@enc,@doc,1,'Draft',@text,@date,@date) ON CONFLICT DO NOTHING;
                    INSERT INTO emr_note_versions(note_id,version,author_id,encounter_id,source_text,source_hash,signed_at,created_at)
                    VALUES(@note,1,@doc,@enc,@text,@hash,NULL,@date) ON CONFLICT DO NOTHING;
                    """, default, transaction, ("note", noteId), ("enc", encounter), ("doc", doctor), ("text", noteText), ("hash", hash), ("date", fixedDate));
            }
            else if (i == 2) // Encounter 2: Signed & Processing
            {
                Guid noteId = Guid.NewGuid();
                string noteText = "Routine diabetic review. Continue metformin 500 mg twice daily. HbA1c test scheduled.";
                string hash = EmrHash.Text(noteText);
                await EmrStore.Execute(connection, """
                    INSERT INTO emr_notes(id,encounter_id,author_id,current_version,status,draft_text,created_at,updated_at)
                    VALUES(@note,@enc,@doc,1,'Signed',@text,@date,@date) ON CONFLICT DO NOTHING;
                    INSERT INTO emr_note_versions(note_id,version,author_id,encounter_id,source_text,source_hash,signed_at,created_at)
                    VALUES(@note,1,@doc,@enc,@text,@hash,@date,@date) ON CONFLICT DO NOTHING;
                    INSERT INTO emr_outbox_events(event_id,event_type,schema_version,note_id,note_version,correlation_id,occurred_at,publication_status,published_at,processing_status)
                    VALUES(@event,'ClinicalNoteSigned',1,@note,1,@corr,@date,'Published',@date,'Processing') ON CONFLICT DO NOTHING;
                    """, default, transaction, ("note", noteId), ("enc", encounter), ("doc", doctor), ("text", noteText), ("hash", hash), ("date", encDate),
                    ("event", Guid.NewGuid()), ("corr", Guid.NewGuid()));
            }
        }
        await transaction.CommitAsync();
        Console.WriteLine("Synthetic doctor and assigned encounters provisioned. No credentials printed or written.");
    }
}
