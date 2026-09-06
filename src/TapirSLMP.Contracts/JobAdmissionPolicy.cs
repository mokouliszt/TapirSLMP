using TapirSLMP.Protocol;

namespace TapirSLMP.Contracts;

public enum PolicyRejectionKind
{
    BadRequest,
    Forbidden,
}

public sealed record PolicyRejection(string Code, string Message, PolicyRejectionKind Kind)
{
    public JobGatewayException ToException() => new(Code, Message);
}

/// <summary>A request that has passed <see cref="JobAdmissionPolicy"/>.</summary>
public sealed record AdmittedJob(
    string Id,
    string RouteKey,
    SlmpTransportKind Transport,
    JobRisk Risk,
    ushort Command,
    ushort Subcommand,
    byte[] RequestFrame,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed class JobPolicyOptions
{
    public bool AllowStateChangingCommands { get; init; }

    public int DefaultJobTtlSeconds { get; init; } = 60;

    public int MaximumJobTtlSeconds { get; init; } = 300;

    public int MaximumAttempts { get; init; } = 3;

    public int MaximumLeaseSeconds { get; init; } = 120;

    /// <summary>
    /// Route keys this freezer serves. When empty, any syntactically valid route key is
    /// accepted, which is the original behaviour: a key no liberator serves then sits
    /// queued until its TTL expires. Populating this turns that silent stall into an
    /// immediate rejection at submission time, and rejects a liberator that leases for a
    /// route key nobody configured.
    /// </summary>
    public IReadOnlySet<string> KnownRouteKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public bool IsKnownRoute(string routeKey) =>
        KnownRouteKeys.Count == 0 || KnownRouteKeys.Contains(routeKey);

    public void Validate()
    {
        if (DefaultJobTtlSeconds is < 1 or > 86_400 ||
            MaximumJobTtlSeconds is < 1 or > 86_400 ||
            DefaultJobTtlSeconds > MaximumJobTtlSeconds)
        {
            throw new InvalidOperationException("Job TTL settings are invalid.");
        }

        if (MaximumAttempts is < 1 or > 100)
        {
            throw new InvalidOperationException("MaximumAttempts must be between 1 and 100.");
        }

        if (MaximumLeaseSeconds is < 5 or > 3600)
        {
            throw new InvalidOperationException("MaximumLeaseSeconds must be between 5 and 3600.");
        }

        if (KnownRouteKeys.Count > 4096)
        {
            throw new InvalidOperationException("KnownRouteKeys must contain at most 4096 entries.");
        }

        var invalid = KnownRouteKeys.FirstOrDefault(route => !JobAdmissionPolicy.IsValidRouteKey(route));
        if (invalid is not null)
        {
            throw new InvalidOperationException($"KnownRouteKeys contains an invalid route key '{invalid}'.");
        }
    }
}

/// <summary>
/// Transport-independent admission rules for freezer jobs: frame validation, the
/// read-only command allow-list, TTL clamping, and lease-identity checks.
/// Both the HTTP endpoints and the in-process gateway run every request through
/// this type, so the read-only guard cannot be bypassed by choosing a transport.
/// </summary>
public sealed class JobAdmissionPolicy(JobPolicyOptions options)
{
    public JobPolicyOptions Options { get; } = options;

    public bool TryAdmit(
        EnqueueJobRequest request,
        DateTimeOffset now,
        out AdmittedJob? admitted,
        out PolicyRejection? rejection)
    {
        admitted = null;

        if (request is null)
        {
            rejection = new PolicyRejection("invalid_request", "Request body is missing.", PolicyRejectionKind.BadRequest);
            return false;
        }

        if (!IsValidClientRequestId(request.ClientRequestId))
        {
            rejection = new PolicyRejection(
                "invalid_client_request_id",
                "Client request id must be 1-64 safe characters.",
                PolicyRejectionKind.BadRequest);
            return false;
        }

        if (!IsValidRouteKey(request.RouteKey))
        {
            rejection = new PolicyRejection(
                "invalid_route_key",
                "Route key must be 1-128 safe characters.",
                PolicyRejectionKind.BadRequest);
            return false;
        }

        if (!Options.IsKnownRoute(request.RouteKey))
        {
            rejection = new PolicyRejection(
                "unknown_route_key",
                $"Route key '{request.RouteKey}' is not served by this freezer.",
                PolicyRejectionKind.BadRequest);
            return false;
        }

        if (!Enum.IsDefined(request.Transport))
        {
            rejection = new PolicyRejection(
                "invalid_transport",
                "Transport must be Tcp or Udp.",
                PolicyRejectionKind.BadRequest);
            return false;
        }

        if (request.RequestFrame is null ||
            request.RequestFrame.Length > SlmpFrameCodec.MaximumFrameLength)
        {
            rejection = new PolicyRejection(
                "invalid_frame",
                "Request frame is missing or too large.",
                PolicyRejectionKind.BadRequest);
            return false;
        }

        var parseStatus = SlmpFrameCodec.TryParseRequest(
            request.RequestFrame,
            out var parsed,
            out var consumed,
            out var parseError);
        if (parseStatus != SlmpFrameParseStatus.Complete ||
            parsed is null ||
            consumed != request.RequestFrame.Length)
        {
            rejection = new PolicyRejection(
                "invalid_frame",
                parseError ?? "Request must contain exactly one binary 3E or 4E frame.",
                PolicyRejectionKind.BadRequest);
            return false;
        }

        if (!Options.AllowStateChangingCommands && parsed.Risk == SlmpCommandRisk.StateChanging)
        {
            rejection = new PolicyRejection(
                "state_change_disabled",
                $"Command 0x{parsed.Command:X4} is not in the read-only allow-list.",
                PolicyRejectionKind.Forbidden);
            return false;
        }

        var maximumExpiry = now.AddSeconds(Options.MaximumJobTtlSeconds);
        var expiry = request.ExpiresAt ?? now.AddSeconds(Options.DefaultJobTtlSeconds);
        if (expiry <= now || expiry > maximumExpiry)
        {
            rejection = new PolicyRejection(
                "invalid_expiry",
                $"Expiry must be in the future and at most {Options.MaximumJobTtlSeconds} seconds from now.",
                PolicyRejectionKind.BadRequest);
            return false;
        }

        admitted = new AdmittedJob(
            JobIdentifiers.FromClientRequestId(request.ClientRequestId),
            request.RouteKey,
            request.Transport,
            parsed.Risk == SlmpCommandRisk.ReadOnly ? JobRisk.ReadOnly : JobRisk.StateChanging,
            parsed.Command,
            parsed.Subcommand,
            request.RequestFrame,
            now,
            NormalizeDatabaseTime(expiry));
        rejection = null;
        return true;
    }

    public PolicyRejection? ValidateLeaseRequest(AcquireLeaseRequest request)
    {
        if (request is null ||
            !IsValidWorkerId(request.WorkerId) ||
            request.RouteKeys is null ||
            request.RouteKeys.Count is < 1 or > 256 ||
            request.RouteKeys.Any(route => !IsValidRouteKey(route)) ||
            request.RouteKeys.Distinct(StringComparer.Ordinal).Count() != request.RouteKeys.Count)
        {
            return new PolicyRejection(
                "invalid_lease_request",
                "Worker id or route list is invalid.",
                PolicyRejectionKind.BadRequest);
        }

        var unknown = request.RouteKeys.FirstOrDefault(route => !Options.IsKnownRoute(route));
        if (unknown is not null)
        {
            return new PolicyRejection(
                "unknown_route_key",
                $"Route key '{unknown}' is not served by this freezer.",
                PolicyRejectionKind.BadRequest);
        }

        if (request.LeaseSeconds is < 5 || request.LeaseSeconds > Options.MaximumLeaseSeconds)
        {
            return new PolicyRejection(
                "invalid_lease_duration",
                $"Lease duration must be between 5 and {Options.MaximumLeaseSeconds} seconds.",
                PolicyRejectionKind.BadRequest);
        }

        return null;
    }

    public static PolicyRejection? ValidateLeaseIdentity(string? workerId, string? leaseToken) =>
        IsValidLeaseIdentity(workerId, leaseToken)
            ? null
            : new PolicyRejection(
                "invalid_lease",
                "Worker id or lease token is invalid.",
                PolicyRejectionKind.BadRequest);

    public static PolicyRejection? ValidateFailure(FailJobRequest request)
    {
        if (request is null)
        {
            return new PolicyRejection("invalid_failure", "Request body is missing.", PolicyRejectionKind.BadRequest);
        }

        if (!IsValidLeaseIdentity(request.WorkerId, request.LeaseToken) ||
            !Enum.IsDefined(request.Disposition) ||
            string.IsNullOrWhiteSpace(request.Error) ||
            request.Error.Length > 1024)
        {
            return new PolicyRejection(
                "invalid_failure",
                "Lease identity or error text is invalid.",
                PolicyRejectionKind.BadRequest);
        }

        return null;
    }

    /// <summary>
    /// Confirms that a response frame is well formed and correlated to the stored request,
    /// so a worker cannot complete a job with an unrelated payload.
    /// </summary>
    public static PolicyRejection? ValidateResponseFrame(
        byte[] storedRequestFrame,
        byte[]? responseFrame)
    {
        if (responseFrame is null || responseFrame.Length > SlmpFrameCodec.MaximumFrameLength)
        {
            return new PolicyRejection(
                "invalid_response_frame",
                "Response frame is missing or too large.",
                PolicyRejectionKind.BadRequest);
        }

        var requestStatus = SlmpFrameCodec.TryParseRequest(
            storedRequestFrame,
            out var request,
            out var requestLength,
            out _);
        var responseStatus = SlmpFrameCodec.TryParseResponse(
            responseFrame,
            out var response,
            out var responseLength,
            out var responseError);
        string? correlationError = null;
        var isCorrelated = requestStatus == SlmpFrameParseStatus.Complete &&
            request is not null &&
            responseStatus == SlmpFrameParseStatus.Complete &&
            response is not null &&
            SlmpFrameCodec.IsCorrelated(request, response, out correlationError);
        if (requestStatus != SlmpFrameParseStatus.Complete ||
            request is null ||
            requestLength != storedRequestFrame.Length ||
            responseStatus != SlmpFrameParseStatus.Complete ||
            response is null ||
            responseLength != responseFrame.Length ||
            !isCorrelated)
        {
            return new PolicyRejection(
                "invalid_response_frame",
                responseError ?? correlationError ?? "Response is not correlated to the request.",
                PolicyRejectionKind.BadRequest);
        }

        return null;
    }

    public static bool IsValidRouteKey(string? value) => IsSafeIdentifier(value, 128);

    public static bool IsValidClientRequestId(string? value) => IsSafeIdentifier(value, 64);

    public static bool IsValidWorkerId(string? value) => IsSafeIdentifier(value, 128);

    public static bool IsValidLeaseIdentity(string? workerId, string? leaseToken) =>
        IsValidWorkerId(workerId) &&
        leaseToken is { Length: >= 40 and <= 128 } &&
        leaseToken.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static DateTimeOffset NormalizeDatabaseTime(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond,
            TimeSpan.Zero);
    }

    private static bool IsSafeIdentifier(string? value, int maximumLength) =>
        value is { Length: >= 1 } &&
        value.Length <= maximumLength &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
}
