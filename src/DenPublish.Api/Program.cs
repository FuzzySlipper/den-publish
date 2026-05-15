using DenPublish.Core;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    service = "den-publish"
}));

app.MapGet("/readyz", () => Results.Ok(new
{
    status = "ready",
    service = "den-publish",
    contract = "den-code-gate-v1"
}));

app.MapGet("/contracts/ref-example", () => Results.Ok(new
{
    immutable_ref = SubmissionRefBuilder.BuildImmutableRef("den-channels", 1416, "run-20260514-abc123", 1),
    current_ref = SubmissionRefBuilder.BuildCurrentRef("den-channels", 1416)
}));

app.Run();
