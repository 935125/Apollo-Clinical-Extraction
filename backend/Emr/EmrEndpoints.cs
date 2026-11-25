using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Apollo.Backend.Clinical;
using Apollo.Backend.Storage;

namespace Apollo.Backend.Emr;

public static class EmrEndpoints
{
    public static void MapDoctorEmr(this WebApplication app)
    {
        var auth=app.MapGroup("/api/v1/emr/auth");
        auth.MapGet("/csrf", (HttpContext http,IAntiforgery antiforgery) =>
        {
            var tokens=antiforgery.GetAndStoreTokens(http);
            http.Response.Cookies.Append("XSRF-TOKEN",tokens.RequestToken!,new CookieOptions { HttpOnly=false,Secure=http.Request.IsHttps,SameSite=SameSiteMode.Strict,Path="/" });
            return Results.Ok(new { token=tokens.RequestToken });
        }).AllowAnonymous();
        auth.MapPost("/login",async (LoginRequest request,HttpContext http,DoctorSessions sessions,CancellationToken ct) =>
        {
            if(!app.Environment.IsDevelopment()) return Results.Problem(statusCode:503,detail:"Local demonstration login is disabled. Configure institutional identity before deployment.");
            if(!await EmrAuth.CheckCsrf(http)) return Results.BadRequest(new {code="CSRF_INVALID",detail="Reload the sign-in page and try again."});
            if(string.IsNullOrWhiteSpace(request.Login) || request.Login.Length>256 || string.IsNullOrEmpty(request.Password) || request.Password.Length>1024)
                return Results.BadRequest(new {code="INVALID_LOGIN",detail="Enter your doctor ID and password."});
            var authenticated=await sessions.Login(request.Login.Trim().ToLowerInvariant(),request.Password,ct);
            if(authenticated is not { } account) return Results.Json(new {code="LOGIN_REJECTED",detail="Unable to sign in. Check your credentials or try again later."},statusCode:401);
            var principal=new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub",account.Profile.Id.ToString()),new Claim("sid",account.Session.ToString()),
                new Claim("iss","apollo-local-emr"),new Claim("name",account.Profile.DisplayName),
                new Claim("login",account.Profile.Login),new Claim("roles",account.Profile.Role)],EmrAuth.Scheme,"name","roles"));
            await http.SignInAsync(EmrAuth.Scheme,principal,new AuthenticationProperties { IsPersistent=false,ExpiresUtc=DateTimeOffset.UtcNow.AddMinutes(30),AllowRefresh=false });
            return Results.Ok(account.Profile);
        }).AllowAnonymous().RequireRateLimiting("doctor-login");

        auth.MapPost("/dev-session", async (HttpContext http, IAntiforgery antiforgery, DoctorSessions sessions, CancellationToken ct) =>
        {
            bool loopback = http.Connection.RemoteIpAddress is null || System.Net.IPAddress.IsLoopback(http.Connection.RemoteIpAddress);
            if (!app.Environment.IsDevelopment() || !Apollo.Backend.Security.ClinicalApiSecurity.IsDemo(app.Configuration, app.Environment) || !loopback)
                return Results.Json(new { code = "DEV_SESSION_DISABLED", detail = "Local demonstration auto-session is disabled outside Development loopback. Sign in via institutional identity." }, statusCode: 403);

            string? preferred = app.Configuration["Emr:Seed:Login"] ?? "doctor.krishnan@apollo.local";
            var created = await sessions.CreateDevelopmentLoopbackSession(preferred, ct);
            if (created is not { } account)
                return Results.Json(new { code = "DEMO_ACCOUNT_NOT_CONFIGURED", detail = "No enabled synthetic clinician account exists in PostgreSQL. Run --emr-seed first." }, statusCode: 503);

            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", account.Profile.Id.ToString()), new Claim("sid", account.Session.ToString()),
                new Claim("iss", "apollo-local-emr"), new Claim("name", account.Profile.DisplayName),
                new Claim("login", account.Profile.Login), new Claim("roles", account.Profile.Role)], EmrAuth.Scheme, "name", "roles"));
            await http.SignInAsync(EmrAuth.Scheme, principal, new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30), AllowRefresh = false });
            http.User = principal;
            var tokens = antiforgery.GetAndStoreTokens(http);
            http.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions { HttpOnly = false, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/" });
            return Results.Ok(account.Profile);
        }).AllowAnonymous();

        var queue = app.MapGroup("/api/v1/queue").RequireAuthorization("DoctorEmr");
        queue.MapGet("", async (string? search, string? state, int? limit, int? offset, HttpContext http, EmrStore store, IConfiguration cfg, CancellationToken ct) =>
        {
            var (items, total) = await store.QueueItems(EmrAuth.Doctor(http.User), search, state, limit ?? 10, offset ?? 0, ct);
            var extractionOpts = cfg.GetSection("Extraction").Get<ExtractionOptions>() ?? new();
            return Results.Ok(new QueueListResponse(items, total, DateTimeOffset.UtcNow, extractionOpts.Provider, extractionOpts.FallbackProvider));
        });
        queue.MapGet("/{noteId:guid}", async (Guid noteId, int? version, string? patient, HttpContext http, EmrStore store, IExtractionRepository repository, CancellationToken ct) =>
        {
            Guid doctor = EmrAuth.Doctor(http.User);
            var header = await store.QueueEntryHeader(doctor, noteId, version, patient, ct);
            if (header is null) return Results.NotFound(new { code = "QUEUE_ENTRY_NOT_FOUND", detail = "Queue entry was not found or is not assigned to this account." });
            var extraction = header.ExtractionId is { } extId
                ? await repository.GetExtractionAsync(extId.ToString(), ct)
                : null;
            var patientNotes = await store.PatientNotesForPatient(doctor, header.PatientId, ct);
            return Results.Ok(new QueueEntryDetailDto(
                header.NoteId, header.EncounterId, header.NoteVersion, header.PatientReference, header.EncounterReference,
                header.EncounterDate, header.Context, header.NoteStatus, header.SourceText, header.SourceHash,
                header.SignedAt, header.UpdatedAt, header.QueueState, header.VerificationRecommended,
                header.RawProcessingStatus, header.Error, extraction, header.PatientId, header.EventId, header.ExtractionId, patientNotes));
        });
        queue.MapGet("/patients/{patientReference}", async (string patientReference, HttpContext http, EmrStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(patientReference) || patientReference.Length > 128)
                return Results.BadRequest(new { code = "INVALID_PATIENT_REFERENCE" });
            var scoped = await store.PatientScope(EmrAuth.Doctor(http.User), patientReference, ct);
            if (scoped is not { } result)
                return Results.NotFound(new { code = "PATIENT_NOT_FOUND", detail = "Patient reference was not found or is not assigned to this account." });
            return Results.Ok(new { patientId = result.PatientId, patientReference = result.PatientReference, notes = result.Notes });
        });

        var api=app.MapGroup("/api/v1/emr").RequireAuthorization("DoctorEmr");
        api.AddEndpointFilter(async (ctx,next) =>
        {
            if(ctx.HttpContext.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && !await EmrAuth.CheckCsrf(ctx.HttpContext))
                return Results.BadRequest(new {code="CSRF_INVALID",detail="Your security token has expired. Reload and try again."});
            try { return await next(ctx); }
            catch(EmrNotFoundException) { return Results.NotFound(new {code="RESOURCE_NOT_FOUND"}); }
            catch(EmrConflictException) { return Results.Conflict(new {code="NOTE_VERSION_CONFLICT",detail="This note has changed or is already signed. Reload the latest version before continuing."}); }
        });
        api.MapGet("/auth/me",(HttpContext http) => Results.Ok(new DoctorProfile(EmrAuth.Doctor(http.User),http.User.FindFirstValue("login")!,http.User.FindFirstValue("name")!,http.User.FindFirstValue("roles")!)));
        api.MapPost("/auth/logout",async (HttpContext http,DoctorSessions sessions,CancellationToken ct) =>
        {
            await sessions.Logout(EmrAuth.Doctor(http.User),Guid.Parse(http.User.FindFirstValue("sid")!),ct);
            await http.SignOutAsync(EmrAuth.Scheme); return Results.NoContent();
        });
        api.MapGet("/encounters",async (HttpContext http,EmrStore store,CancellationToken ct) => Results.Ok(await store.Encounters(EmrAuth.Doctor(http.User),ct)));
        api.MapGet("/encounters/{id:guid}",async (Guid id,HttpContext http,EmrStore store,CancellationToken ct) =>
        {
            var encounter=(await store.Encounters(EmrAuth.Doctor(http.User),ct)).FirstOrDefault(e=>e.Id==id);
            return encounter is null?Results.NotFound():Results.Ok(encounter);
        });
        api.MapGet("/encounters/{id:guid}/notes",async(Guid id,HttpContext http,EmrStore store,CancellationToken ct)=>
        {
            Guid doctor=EmrAuth.Doctor(http.User); if(!await store.HasEncounter(doctor,id,ct)) return Results.NotFound();
            await using var c=await store.Open(ct);
            var notes=await EmrStore.Query<System.Text.Json.JsonElement>(c,"SELECT jsonb_build_object('id',id,'version',current_version,'status',status,'updatedAt',updated_at) FROM emr_notes WHERE encounter_id=@id ORDER BY updated_at DESC",ct,null,("id",id));
            return Results.Ok(notes);
        });
        api.MapPost("/encounters/{id:guid}/notes",async (Guid id,DraftRequest request,HttpContext http,EmrStore store,CancellationToken ct) =>
        {
            if(!http.User.IsInRole("Doctor")) return Results.Forbid();
            if(!ValidText(request.Text)) return InvalidText();
            Guid doctor=EmrAuth.Doctor(http.User),note=await store.CreateDraft(doctor,id,request.Text!,ct);
            return Results.Created($"/api/v1/emr/notes/{note}",await store.Note(doctor,note,ct));
        });
        api.MapGet("/notes/{id:guid}",async(Guid id,HttpContext http,EmrStore store,CancellationToken ct)=>
        { var note=await store.Note(EmrAuth.Doctor(http.User),id,ct); return note is null?Results.NotFound():Results.Ok(note); });
        api.MapGet("/notes/{id:guid}/status",async(Guid id,HttpContext http,EmrStore store,CancellationToken ct)=>
        { var note=await store.Note(EmrAuth.Doctor(http.User),id,ct); return note is null?Results.NotFound():Results.Ok(note); });
        api.MapPut("/notes/{id:guid}",async(Guid id,DraftRequest request,HttpContext http,EmrStore store,CancellationToken ct)=>await Save(id,request,false,http,store,ct));
        api.MapPost("/notes/{id:guid}/corrections",async(Guid id,DraftRequest request,HttpContext http,EmrStore store,CancellationToken ct)=>await Save(id,request,true,http,store,ct));
        api.MapPost("/notes/{id:guid}/sign",async(Guid id,SignRequest request,HttpContext http,EmrStore store,CancellationToken ct)=>
        {
            if(!http.User.IsInRole("Doctor")) return Results.Forbid();
            Guid doctor=EmrAuth.Doctor(http.User);
            if(await store.Note(doctor,id,ct) is null) return Results.NotFound();
            await store.Sign(doctor,id,request.ExpectedVersion,ct);
            return Results.Ok(await store.Note(doctor,id,ct));
        });
        api.MapGet("/notes/{id:guid}/versions",async(Guid id,HttpContext http,EmrStore store,CancellationToken ct)=>
        {
            if(await store.Note(EmrAuth.Doctor(http.User),id,ct) is null) return Results.NotFound();
            await using var c=await store.Open(ct);
            return Results.Ok(await EmrStore.Query<System.Text.Json.JsonElement>(c,"SELECT jsonb_build_object('version',version,'sourceText',source_text,'sourceHash',source_hash,'signedAt',signed_at,'createdAt',created_at) FROM emr_note_versions WHERE note_id=@id ORDER BY version DESC",ct,null,("id",id)));
        });
        api.MapGet("/notes/{id:guid}/extraction",async(Guid id,HttpContext http,EmrStore store,IExtractionRepository repository,CancellationToken ct)=>
        {
            var note=await store.Note(EmrAuth.Doctor(http.User),id,ct); if(note is null) return Results.NotFound();
            if(note.ExtractionId is null) return Results.Conflict(new {code="EXTRACTION_NOT_READY"});
            return Results.Ok(await repository.GetExtractionAsync(note.ExtractionId.Value.ToString(),ct));
        });
        api.MapPost("/notes/{id:guid}/pdf",async(Guid id,HttpContext http,EmrStore store,CancellationToken ct)=>await ImportPdf(id,http,store,ct,false)).DisableAntiforgery(); // Explicit endpoint filter validates CSRF before reading the form.
        api.MapPost("/encounters/{id:guid}/pdf",async(Guid id,HttpContext http,EmrStore store,CancellationToken ct)=>await ImportPdf(id,http,store,ct,true)).DisableAntiforgery();
    }
    private static bool ValidText(string? text)=>!string.IsNullOrWhiteSpace(text)&&text.Length<=65536;
    private static IResult InvalidText()=>Results.BadRequest(new {code="INVALID_NOTE",detail="Enter a clinical note of up to 65,536 characters."});
    private static async Task<IResult> Save(Guid id,DraftRequest request,bool correction,HttpContext http,EmrStore store,CancellationToken ct)
    {
        if(!http.User.IsInRole("Doctor")) return Results.Forbid();
        if(!ValidText(request.Text)) return InvalidText();
        Guid doctor=EmrAuth.Doctor(http.User); if(await store.Note(doctor,id,ct) is null) return Results.NotFound();
        await store.UpdateDraft(doctor,id,request.Text!,request.ExpectedVersion,correction,ct);
        return Results.Ok(await store.Note(doctor,id,ct));
    }
    private static async Task<IResult> ImportPdf(Guid id,HttpContext http,EmrStore store,CancellationToken ct,bool newNote)
    {
        if(!http.User.IsInRole("Doctor")) return Results.Forbid();
        Guid doctor=EmrAuth.Doctor(http.User);
        if(newNote ? !await store.HasEncounter(doctor,id,ct) : await store.Note(doctor,id,ct) is null) return Results.NotFound();
        if(!http.Request.HasFormContentType) return Results.BadRequest(new {code="PDF_REQUIRED"});
        var form=await http.Request.ReadFormAsync(ct); var file=form.Files.GetFile("file");
        if(file is null || file.Length==0 || !file.FileName.EndsWith(".pdf",StringComparison.OrdinalIgnoreCase) || file.ContentType!="application/pdf") return Results.BadRequest(new {code="PDF_REQUIRED"});
        if(file.Length>20*1024*1024) return Results.Problem(statusCode:413,detail:"The PDF must be 20 MiB or smaller.");
        int version=0;
        if(!newNote && !int.TryParse(form["expectedVersion"],out version)) return Results.BadRequest(new {code="VERSION_REQUIRED"});
        using var bytes=new MemoryStream(); await file.CopyToAsync(bytes,ct); byte[] content=bytes.ToArray();
        if(content.Length<5 || System.Text.Encoding.ASCII.GetString(content,0,5)!="%PDF-") return Results.BadRequest(new {code="INVALID_PDF"});
        string text;
        try { bytes.Position=0; text=PdfTextReconstructor.ExtractText(bytes); }
        catch(Exception e) when(e is not OperationCanceledException) { return Results.BadRequest(new {code="INVALID_PDF",detail="This PDF could not be read. Upload an unencrypted, text-based PDF."}); }
        if(string.IsNullOrWhiteSpace(text)) return Results.UnprocessableEntity(new {code="OCR_REQUIRED",detail="No embedded text was found. This document requires OCR. Paste the note or upload a text-based PDF."});
        if(!ValidText(text)) return InvalidText();
        string safeName=Path.GetFileName(file.FileName); if(safeName.Length>200) safeName="clinical-note.pdf";
        if(newNote) id=await store.CreateDraft(doctor,id,text,ct,safeName,content);
        else await store.Attach(doctor,id,version,safeName,content,text,ct);
        return Results.Ok(await store.Note(doctor,id,ct));
    }
}
