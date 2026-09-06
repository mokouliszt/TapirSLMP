using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TapirSLMP.Contracts;
using TapirSLMP.Storage;
using TapirSLMP.Storage.Postgres;
using TapirSLMP.Storage.Sqlite;

namespace TapirSLMP.Freezer.Hosting;

public static class FreezerHostingExtensions
{
    public const string DefaultSectionName = "Freezer";

    /// <summary>
    /// Registers the job store, the admission policy, and the background reaper.
    /// Call <see cref="MapTapirFreezer"/> to expose the HTTP endpoints.
    /// </summary>
    public static IServiceCollection AddTapirFreezer(
        this IServiceCollection services,
        FreezerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new JobAdmissionPolicy(options.ToPolicyOptions()));
        services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false)));
        services.TryAddSingleton<IJobStore>(_ =>
            string.Equals(options.Provider, "Postgres", StringComparison.OrdinalIgnoreCase)
                ? new PostgresJobStore(new PostgresJobStoreOptions
                {
                    ConnectionString = options.PostgresConnectionString,
                })
                : new SqliteJobStore(new SqliteJobStoreOptions
                {
                    ConnectionString = options.SqliteConnectionString,
                }));
        services.AddHostedService<JobReaperService>();
        return services;
    }

    public static IServiceCollection AddTapirFreezer(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = DefaultSectionName) =>
        services.AddTapirFreezer(FreezerOptions.FromConfiguration(configuration, sectionName));


    /// <summary>
    /// Creates the job store schema. Call once during startup, before serving traffic.
    /// </summary>
    public static Task InitializeTapirFreezerAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default) =>
        services.GetRequiredService<IJobStore>().InitializeAsync(cancellationToken);

    /// <summary>
    /// Maps the freezer endpoints under <paramref name="prefix"/>. The health endpoint is
    /// mapped at <c>{prefix}/healthz</c> and the job API under <c>{prefix}/v1</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapTapirFreezer(
        this IEndpointRouteBuilder endpoints,
        string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup(prefix);

        group.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        group.MapPost("/v1/jobs", async (
            HttpContext context,
            EnqueueJobRequest request,
            IJobStore store,
            JobAdmissionPolicy policy,
            FreezerOptions freezer,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!TokenAuthentication.IsAuthorized(context.Request, freezer.SealerToken))
            {
                return Unauthorized();
            }

            var now = clock.GetUtcNow();
            if (!policy.TryAdmit(request, now, out var admitted, out var rejection))
            {
                return Rejected(rejection!);
            }

            var job = new NewJob(
                admitted!.Id,
                admitted.RouteKey,
                admitted.Transport,
                admitted.Risk,
                admitted.Command,
                admitted.Subcommand,
                admitted.RequestFrame,
                admitted.CreatedAt,
                admitted.ExpiresAt);
            if (await store.TryAddAsync(job, cancellationToken).ConfigureAwait(false))
            {
                return Results.Created($"/v1/jobs/{job.Id}", new EnqueueJobResponse(job.Id, now));
            }

            var existing = await store.GetAsync(job.Id, cancellationToken).ConfigureAwait(false);
            if (existing is null ||
                !string.Equals(existing.RouteKey, job.RouteKey, StringComparison.Ordinal) ||
                existing.Transport != job.Transport ||
                existing.Risk != job.Risk ||
                existing.Command != job.Command ||
                existing.Subcommand != job.Subcommand ||
                (request.ExpiresAt is not null && existing.ExpiresAt != job.ExpiresAt) ||
                !existing.RequestFrame.AsSpan().SequenceEqual(job.RequestFrame))
            {
                return Results.Conflict(new ErrorResponse(
                    "idempotency_conflict",
                    "Client request id is already associated with different request data."));
            }

            return Results.Ok(new EnqueueJobResponse(job.Id, existing.CreatedAt, Replayed: true));
        });

        group.MapGet("/v1/jobs/{id}", async (
            string id,
            HttpContext context,
            IJobStore store,
            FreezerOptions freezer,
            CancellationToken cancellationToken) =>
        {
            if (!TokenAuthentication.IsAuthorized(context.Request, freezer.SealerToken))
            {
                return Unauthorized();
            }

            var job = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            return job is null ? Results.NotFound() : Results.Ok(job.ToView());
        });

        group.MapPost("/v1/leases/acquire", async (
            HttpContext context,
            AcquireLeaseRequest request,
            IJobStore store,
            JobAdmissionPolicy policy,
            FreezerOptions freezer,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!TokenAuthentication.IsAuthorized(context.Request, freezer.LiberatorToken))
            {
                return Unauthorized();
            }

            var rejection = policy.ValidateLeaseRequest(request);
            if (rejection is not null)
            {
                return Rejected(rejection);
            }

            var lease = await store.TryAcquireAsync(
                request.WorkerId,
                request.RouteKeys.ToHashSet(StringComparer.Ordinal),
                TimeSpan.FromSeconds(request.LeaseSeconds),
                policy.Options.MaximumAttempts,
                clock.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return lease is null
                ? Results.NoContent()
                : Results.Ok(new LeaseView(lease.LeaseToken, lease.LeaseUntil, lease.Job.ToView()));
        });

        group.MapPost("/v1/jobs/{id}/executing", async (
            string id,
            HttpContext context,
            LeaseActionRequest request,
            IJobStore store,
            FreezerOptions freezer,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!TokenAuthentication.IsAuthorized(context.Request, freezer.LiberatorToken))
            {
                return Unauthorized();
            }

            var rejection = JobAdmissionPolicy.ValidateLeaseIdentity(request.WorkerId, request.LeaseToken);
            if (rejection is not null)
            {
                return Rejected(rejection);
            }

            var result = await store.MarkExecutingAsync(
                id,
                request.WorkerId,
                request.LeaseToken,
                clock.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return MutationResult(result);
        });

        group.MapPost("/v1/jobs/{id}/complete", async (
            string id,
            HttpContext context,
            CompleteJobRequest request,
            IJobStore store,
            FreezerOptions freezer,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!TokenAuthentication.IsAuthorized(context.Request, freezer.LiberatorToken))
            {
                return Unauthorized();
            }

            var rejection = JobAdmissionPolicy.ValidateLeaseIdentity(request.WorkerId, request.LeaseToken);
            if (rejection is not null)
            {
                return Rejected(rejection);
            }

            var job = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                return Results.NotFound();
            }

            rejection = JobAdmissionPolicy.ValidateResponseFrame(job.RequestFrame, request.ResponseFrame);
            if (rejection is not null)
            {
                return Rejected(rejection);
            }

            var result = await store.CompleteAsync(
                id,
                request.WorkerId,
                request.LeaseToken,
                request.ResponseFrame,
                clock.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return MutationResult(result);
        });

        group.MapPost("/v1/jobs/{id}/fail", async (
            string id,
            HttpContext context,
            FailJobRequest request,
            IJobStore store,
            JobAdmissionPolicy policy,
            FreezerOptions freezer,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!TokenAuthentication.IsAuthorized(context.Request, freezer.LiberatorToken))
            {
                return Unauthorized();
            }

            var rejection = JobAdmissionPolicy.ValidateFailure(request);
            if (rejection is not null)
            {
                return Rejected(rejection);
            }

            var result = await store.FailAsync(
                id,
                request.WorkerId,
                request.LeaseToken,
                request.Disposition,
                request.Error,
                policy.Options.MaximumAttempts,
                clock.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return MutationResult(result);
        });

        return endpoints;
    }

    /// <summary>Adds the no-store and nosniff response headers the freezer expects.</summary>
    public static IApplicationBuilder UseTapirFreezerHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            await next(context).ConfigureAwait(false);
        });
    }

    private static IResult Unauthorized() => Results.Json(
        new ErrorResponse("unauthorized", "A valid bearer token is required."),
        statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Rejected(PolicyRejection rejection) =>
        rejection.Kind == PolicyRejectionKind.Forbidden
            ? Results.Json(
                new ErrorResponse(rejection.Code, rejection.Message),
                statusCode: StatusCodes.Status403Forbidden)
            : Results.BadRequest(new ErrorResponse(rejection.Code, rejection.Message));

    private static IResult MutationResult(StoreMutationResult result) => result switch
    {
        StoreMutationResult.Success => Results.NoContent(),
        StoreMutationResult.NotFound => Results.NotFound(),
        _ => Results.Conflict(new ErrorResponse("lease_conflict", "Lease is stale or job state is incompatible.")),
    };
}
