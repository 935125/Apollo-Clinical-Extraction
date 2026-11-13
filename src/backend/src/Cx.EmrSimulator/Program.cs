using Cx.EmrSimulator.Models;
using Cx.EmrSimulator.Services;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSingleton<EmrNoteService>();

var app = builder.Build();

app.UseHttpsRedirection();

// 1. Create clinical note (POST /emr-sim/v1/encounters/{encounterId}/notes)
app.MapPost("/emr-sim/v1/encounters/{encounterId}/notes", async (
    string encounterId,
    [FromBody] NoteWriteRequest request,
    [FromServices] EmrNoteService service) =>
{
    try
    {
        var result = await service.CreateNoteAsync(encounterId, request);
        return Results.Created($"/emr/v1/notes/{result.NoteId}/versions/{result.NoteVersion}", result);
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new ProblemDetailsResponse("about:blank", "Resource Not Found", 404, "ENCOUNTER_NOT_FOUND", ex.Message));
    }
    catch (ArgumentOutOfRangeException ex)
    {
        return Results.Problem(ex.Message, statusCode: 413, title: "Payload Too Large");
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ProblemDetailsResponse("about:blank", "Bad Request", 400, "VALIDATION_FAILED", ex.Message));
    }
});

// 2. Amend note (PUT /emr-sim/v1/notes/{noteId}) with If-Match concurrency header
app.MapPut("/emr-sim/v1/notes/{noteId}", async (
    string noteId,
    [FromHeader(Name = "If-Match")] string? ifMatch,
    [FromBody] NoteWriteRequest request,
    [FromServices] EmrNoteService service) =>
{
    if (string.IsNullOrWhiteSpace(ifMatch) || !int.TryParse(ifMatch.Trim('"'), out var expectedVersion))
    {
        return Results.BadRequest(new ProblemDetailsResponse("about:blank", "Precondition Required", 428, "PRECONDITION_REQUIRED", "If-Match header with expected note version required."));
    }

    try
    {
        var result = await service.AmendNoteAsync(noteId, expectedVersion, request);
        return Results.Ok(result);
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new ProblemDetailsResponse("about:blank", "Not Found", 404, "NOTE_NOT_FOUND", ex.Message));
    }
    catch (InvalidOperationException)
    {
        return Results.StatusCode(412); // Precondition failed: stale version
    }
});

// 3. Claim-check note version read (GET /emr/v1/notes/{noteId}/versions/{version})
app.MapGet("/emr/v1/notes/{noteId}/versions/{version:int}", async (
    string noteId,
    int version,
    [FromServices] EmrNoteService service) =>
{
    var noteVersion = await service.GetNoteVersionAsync(noteId, version);
    return noteVersion is not null ? Results.Ok(noteVersion) : Results.NotFound();
});

// 4. Access check (POST /emr/v1/access-checks) - verifies care team assignment
app.MapPost("/emr/v1/access-checks", async (
    [FromBody] AccessCheckRequest request,
    IConfiguration config) =>
{
    var connStr = config.GetConnectionString("EmrDatabase") ?? "Host=localhost;Port=5433;Database=emr_sim;Username=postgres;Password=";
    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    await using var cmd = new NpgsqlCommand(
        "SELECT count(*) FROM emr.encounter_care_team WHERE encounter_id = @eid AND user_id = @uid", conn);
    cmd.Parameters.AddWithValue("eid", request.EncounterId);
    cmd.Parameters.AddWithValue("uid", request.UserId);

    var count = (long)(await cmd.ExecuteScalarAsync())!;
    if (count > 0)
    {
        return Results.Ok(new { allowed = true, reasonCode = (string?)null });
    }
    return Results.Ok(new { allowed = false, reasonCode = "NOT_ON_CARE_TEAM" });
});

// Health check
app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy", service = "emr-simulator" }));

app.Run();

public record AccessCheckRequest(string UserId, string PatientId, string EncounterId, string Purpose);

public partial class Program { }
