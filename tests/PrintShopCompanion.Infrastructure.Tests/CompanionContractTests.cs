using System.Text.Json;
using PrintShopCompanion.Shared.Constants;
using PrintShopCompanion.Shared.Dto;
using Xunit;

namespace PrintShopCompanion.Infrastructure.Tests;

/// <summary>
/// CONTRACT DRIFT TESTS — the client side of the Phase 1 "one contract" rule.
///
/// The SaaS repo owns docs/integration/companion-v1.schema.json (generated
/// from Zod). A byte-identical copy lives in docs/contracts/ here. These
/// tests fail when:
///   1. the copied artifact is for a different contractVersion than the
///      client implements (CompanionProtocol.ExpectedContractVersion)
///   2. a route the client calls disappeared from (or changed in) the artifact
///   3. an artifact example no longer deserializes into the client DTOs
///   4. a Socket.IO event the client listens for vanished from the artifact
///
/// When the contract changes: regenerate the artifact in the SaaS repo, copy
/// it here byte-identical, update DTOs/constants, bump ExpectedContractVersion
/// — all in the same commit. The SaaS contract test enforces the other side.
/// </summary>
public class CompanionContractTests
{
    private static JsonDocument LoadArtifact()
    {
        // Walk up from the test bin dir to the repo root that holds docs/contracts/.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "contracts", "companion-v1.schema.json")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "docs", "contracts", "companion-v1.schema.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static readonly JsonDocument Artifact = LoadArtifact();

    [Fact]
    public void Artifact_contract_version_matches_client()
    {
        var version = Artifact.RootElement.GetProperty("contractVersion").GetString();
        Assert.Equal(CompanionProtocol.ExpectedContractVersion, version);
    }

    [Fact]
    public void Every_client_route_exists_in_the_artifact()
    {
        // (method, artifact path, the client's ApiUrl relative segment)
        // clientRelative strings mirror HttpPrintShopBackend's ApiUrl(...) calls.
        var expected = new (string Method, string Path, string ClientRelative)[]
        {
            ("POST", "/api/v1/companion/pair", "companion/pair"),
            ("POST", "/api/v1/agents/heartbeat", "agents/heartbeat"),
            ("GET", "/api/v1/companion/commands", "companion/commands"),
            ("POST", "/api/v1/companion/commands", "companion/commands"),
            ("GET", "/api/v1/agents/jobs/pending", "agents/jobs/pending"),
            ("POST", "/api/v1/agents/jobs/:jobId/status", "agents/jobs/{jobId}/status"),
            ("POST", "/api/v1/agents/jobs/:jobId/received", "agents/jobs/{jobId}/received"),
            ("POST", "/api/v1/agents/token", "agents/token"),
            ("POST", "/api/v1/companion/token/rotate", "companion/token/rotate"),
            ("GET", "/api/v1/companion/shop-snapshot", "companion/shop-snapshot"),
            ("POST", "/api/v1/sync/push", "sync/push"),
            ("GET", "/api/v1/sync/pull", "sync/pull"),
            ("GET", "/api/v1/sync/status", "sync/status"),
        };

        var routes = Artifact.RootElement.GetProperty("routes");
        foreach (var (method, path, clientRelative) in expected)
        {
            bool found = false;
            foreach (var route in routes.EnumerateArray())
            {
                var rMethod = route.GetProperty("method").GetString();
                var rPath = route.GetProperty("path").GetString();
                // The client builds "{apiBase}/{relative}" where apiBase is /api/v1
                // and '{jobId}' matches the contract's ':jobId' parameter style.
                if (rMethod == method && rPath == path && path.EndsWith(clientRelative.Replace("{jobId}", ":jobId")))
                {
                    found = true;
                    break;
                }
            }
            Assert.True(found, $"client route {method} {clientRelative} missing from contract artifact (expected {method} {path})");
        }
    }

    private static T DeserializeData<T>(string exampleSet, int index)
        where T : class
    {
        var examples = Artifact.RootElement.GetProperty("examples").GetProperty(exampleSet);
        var body = examples[index];
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var envelope = body.Deserialize<ApiEnvelope<T>>(options) ?? throw new Xunit.Sdk.XunitException($"example {exampleSet}[{index}] deserialized to null envelope");
        Assert.True(envelope.Success, $"{exampleSet}[{index}] envelope.success must be true");
        Assert.NotNull(envelope.Data);
        return envelope.Data!;
    }

    [Fact]
    public void Pair_response_example_deserializes_into_RegisterAgentResponseDto()
    {
        var data = DeserializeData<RegisterAgentResponseDto>("pairResponse", 0);
        Assert.False(string.IsNullOrWhiteSpace(data.AgentId));
        Assert.Equal(64, data.DeviceToken.Length); // 32 bytes hex
        Assert.False(string.IsNullOrWhiteSpace(data.ShopId));
        Assert.False(string.IsNullOrWhiteSpace(data.ShopName));
    }

    [Fact]
    public void Pending_jobs_example_deserializes_into_PendingJobsEnvelopeDto()
    {
        var data = DeserializeData<PendingJobsEnvelopeDto>("pendingJobsResponse", 0);
        Assert.NotEmpty(data.Jobs);
        var job = data.Jobs[0];
        Assert.False(string.IsNullOrWhiteSpace(job.JobId));
        Assert.False(string.IsNullOrWhiteSpace(job.DownloadUrl));
        Assert.True(job.Copies >= 1);
        Assert.False(string.IsNullOrWhiteSpace(data.AgentId));
    }

    [Fact]
    public void Agent_token_example_deserializes_into_AgentTokenResponseDto()
    {
        var data = DeserializeData<AgentTokenResponseDto>("agentTokenResponse", 0);
        Assert.False(string.IsNullOrWhiteSpace(data.AccessToken));
        Assert.Equal(900, data.ExpiresIn); // 15-minute socket JWT
    }

    [Fact]
    public void Heartbeat_example_deserializes_into_AgentHeartbeatResponseDto()
    {
        var data = DeserializeData<AgentHeartbeatResponseDto>("heartbeatResponse", 0);
        Assert.True(data.Online);
        Assert.False(string.IsNullOrWhiteSpace(data.AgentId));
    }

    [Fact]
    public void Job_status_examples_deserialize_into_AgentJobStatusResponseDto()
    {
        // Example 0: applied. Example 1: skipped (duplicate ack) — the client must handle both.
        var applied = DeserializeData<AgentJobStatusResponseDto>("jobStatusResponse", 0);
        Assert.True(applied.Applied);
        var skipped = DeserializeData<AgentJobStatusResponseDto>("jobStatusResponse", 1);
        Assert.True(skipped.Skipped);
    }

    [Fact]
    public void Job_status_request_serializes_with_contract_field_names()
    {
        var dto = new AgentJobStatusRequestDto { Status = "printing", PrinterName = "HP LaserJet Pro" };
        var json = JsonSerializer.Serialize(dto);
        Assert.Contains("\"status\":\"printing\"", json);
        Assert.Contains("\"printerName\":\"HP LaserJet Pro\"", json);
    }

    [Fact]
    public void Socket_events_the_client_listens_for_exist_in_the_artifact()
    {
        var server = Artifact.RootElement.GetProperty("socketEvents").GetProperty("server");
        var names = server.EnumerateArray().Select(e => e.GetString()).ToHashSet();
        string[] required =
        {
            CompanionProtocol.SocketIoEventJobDispatch,
            CompanionProtocol.SocketIoEventJobPrinting,
            CompanionProtocol.SocketIoEventJobCompleted,
            CompanionProtocol.SocketIoEventJobFailed,
            CompanionProtocol.SocketIoEventJobNeedsReview,
            CompanionProtocol.SocketIoEventAgentRevoked,
            CompanionProtocol.SocketIoEventDeliveryReceived,
        };
        foreach (var ev in required)
        {
            Assert.True(names.Contains(ev), $"socket event {ev} missing from artifact");
        }
    }
}
