namespace PrintShopCompanion.Infrastructure.Tests;

using System.Text.Json;
using FluentAssertions;
using PrintShopCompanion.Shared.Dto;
using Xunit;

/// <summary>
/// Phase 3 — cross-repository contract fixture tests. These verify that the
/// ACTUAL SaaS response JSON (captured from the real route implementations in
/// printshop-saas commit 9f0e8ff) deserializes correctly into our C# DTOs.
/// If the SaaS changes a field name or type, one of these tests breaks —
/// catching drift before it reaches runtime. See docs/PHASE3_CONTRACT_MATRIX.md
/// for the per-endpoint matrix these fixtures are derived from.
///
/// <para>These are NOT unit tests of our client logic — they are schema
/// conformance tests against the real wire format. The JSON strings below
/// are copied verbatim from the SaaS route's response construction code.</para>
/// </summary>
public class ContractFixtureTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // ─── pairing ticket exchange (POST /companion/pair) ───────────────────

    [Fact]
    public void Pair_response_fixture_deserializes_into_PairTicketResponseDto()
    {
        // Verbatim from src/app/api/v1/companion/pair/route.ts success path.
        // Note the `warning` field — the SaaS adds it but our DTO ignores
        // unknown fields by default (JsonSerializerDefaults.Web is permissive).
        var fixture = """
        {
          "success": true,
          "data": {
            "agentId": "a1b2c3d4-0000-0000-0000-000000000001",
            "deviceToken": "e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f4a5b6c7d8e9f0a1b2c3d4e5f6",
            "shopId": "shop-uuid-0001",
            "shopName": "QuickPrint Demo Shop",
            "protocolVersion": 1,
            "warning": "Store this deviceToken securely — it is shown only once."
          }
        }
        """;

        var envelope = JsonSerializer.Deserialize<ApiEnvelope<PairTicketResponseDto>>(fixture, WebJson);
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        var dto = envelope.Data.Should().NotBeNull().Subject;
        dto.AgentId.Should().Be("a1b2c3d4-0000-0000-0000-000000000001");
        dto.DeviceToken.Should().HaveLength(64);
        dto.ShopId.Should().Be("shop-uuid-0001");
        dto.ShopName.Should().Be("QuickPrint Demo Shop");
        dto.ProtocolVersion.Should().Be(1);
    }

    [Theory]
    [InlineData("VALIDATION_ERROR", 400)]
    [InlineData("UNAUTHORIZED", 401)]
    [InlineData("PAIRING_TICKET_UNAVAILABLE", 409)]
    [InlineData("PAIRING_TICKET_CONSUMED", 409)]
    [InlineData("NOT_FOUND", 404)]
    [InlineData("RATE_LIMITED", 429)]
    public void Pair_error_fixture_deserializes_into_envelope_error(string code, int _)
    {
        // The SaaS error envelope shape (from src/lib/api/envelope.ts → fail()).
        var fixture = $$"""
        {
          "success": false,
          "error": { "code": "{{code}}", "message": "descriptive message" }
        }
        """;

        var envelope = JsonSerializer.Deserialize<ApiEnvelope<object>>(fixture, WebJson);
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeFalse();
        envelope.Error.Should().NotBeNull();
        envelope.Error!.Code.Should().Be(code);
        envelope.Error.Message.Should().Be("descriptive message");
    }

    // ─── command fetch (GET /companion/commands) ──────────────────────────

    [Fact]
    public void Commands_response_fixture_deserializes_into_PendingCommandsEnvelopeDto()
    {
        // Verbatim from src/app/api/v1/companion/commands/route.ts GET handler.
        // CRITICAL Phase 3 fix: the server uses `commandId` (not `id`), `payload`
        // as a parsed object (not a string), and includes `issuedAt`. The Phase 2
        // DTO used `id` + `payload` string → every poll silently dropped commands.
        var fixture = """
        {
          "success": true,
          "data": {
            "agentId": "agent-uuid-0001",
            "commands": [
              {
                "commandId": "cmd-uuid-0001",
                "commandType": "refresh_printers",
                "payload": {},
                "issuedAt": "2026-11-17T10:00:00.000Z",
                "expiresAt": "2026-11-17T10:05:00.000Z"
              },
              {
                "commandId": "cmd-uuid-0002",
                "commandType": "cancel_job",
                "payload": { "jobId": "job-uuid-0099" },
                "issuedAt": "2026-11-17T10:00:01.000Z",
                "expiresAt": "2026-11-17T10:05:01.000Z"
              }
            ]
          }
        }
        """;

        var envelope = JsonSerializer.Deserialize<ApiEnvelope<PendingCommandsEnvelopeDto>>(fixture, WebJson);
        envelope.Should().NotBeNull();
        var dto = envelope!.Data.Should().NotBeNull().Subject;
        dto.AgentId.Should().Be("agent-uuid-0001");
        dto.Commands.Should().HaveCount(2);

        // Command 1: empty payload object → re-serialized to "{}"
        var c1 = dto.Commands[0];
        c1.CommandId.Should().Be("cmd-uuid-0001");
        c1.CommandType.Should().Be("refresh_printers");
        c1.Payload.ValueKind.Should().Be(JsonValueKind.Object);
        c1.IssuedAt.Should().NotBeNull();
        c1.ExpiresAt.Should().Be(DateTimeOffset.Parse("2026-11-17T10:05:00.000Z"));

        // Command 2: payload object with a jobId
        var c2 = dto.Commands[1];
        c2.CommandId.Should().Be("cmd-uuid-0002");
        c2.CommandType.Should().Be("cancel_job");
        c2.Payload.ValueKind.Should().Be(JsonValueKind.Object);
        c2.Payload.GetProperty("jobId").GetString().Should().Be("job-uuid-0099");
    }

    [Fact]
    public void Commands_response_with_null_payload_deserializes_to_empty_object()
    {
        // The SaaS does `JSON.parse(payload ?? '{}')` server-side but a null
        // payload field arrives as JSON null → our DTO must tolerate it (the
        // HTTP client maps null/undefined → "{}" for the executor).
        var fixture = """
        {
          "success": true,
          "data": {
            "agentId": "agent-uuid-0001",
            "commands": [
              { "commandId": "cmd-x", "commandType": "diagnostics", "payload": null, "issuedAt": "2026-11-17T10:00:00Z", "expiresAt": "2026-11-17T10:05:00Z" }
            ]
          }
        }
        """;

        var envelope = JsonSerializer.Deserialize<ApiEnvelope<PendingCommandsEnvelopeDto>>(fixture, WebJson);
        var c = envelope!.Data!.Commands[0];
        // Null payload → JsonElement with ValueKind=Null (the HTTP client converts to "{}").
        c.Payload.ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ─── command report (POST /companion/commands) ───────────────────────

    [Fact]
    public void Command_report_response_fixture_deserializes_into_ReportCommandResponseDto()
    {
        // Verbatim from src/app/api/v1/companion/commands/route.ts POST handler.
        // CRITICAL Phase 3 fix: the server returns {commandId, status, updatedAt},
        // NOT {applied} as the published contract stated.
        var fixture = """
        {
          "success": true,
          "data": {
            "commandId": "cmd-uuid-0001",
            "status": "succeeded",
            "updatedAt": "2026-11-17T10:00:05.000Z"
          }
        }
        """;

        var envelope = JsonSerializer.Deserialize<ApiEnvelope<ReportCommandResponseDto>>(fixture, WebJson);
        var dto = envelope!.Data.Should().NotBeNull().Subject;
        dto.CommandId.Should().Be("cmd-uuid-0001");
        dto.Status.Should().Be("succeeded");
        dto.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public void Command_report_request_with_object_result_serializes_correctly()
    {
        // Verify the C# client serializes the `result` field as a JSON OBJECT
        // (not a string) — the SaaS zod requires z.record(z.string(), z.unknown()).
        // The HTTP client parses the executor's result JSON string back to a
        // JsonElement before building this DTO, so `Result` is a JsonElement here.
        var payloadJson = """{"printers":["HP","Canon"],"count":2}""";
        using var doc = JsonDocument.Parse(payloadJson);
        var resultElement = JsonSerializer.Deserialize<JsonElement>(doc.RootElement.GetRawText());

        var dto = new ReportCommandRequestDto
        {
            CommandId = "cmd-1",
            Status = "succeeded",
            Result = resultElement,
        };

        var serialized = JsonSerializer.Serialize(dto, WebJson);
        // The serialized JSON must contain the object literal `"printers"`, not a string-escaped version.
        serialized.Should().Contain("\"printers\"");
        serialized.Should().Contain("\"result\":{\"printers\":[\"HP\",\"Canon\"],\"count\":2}",
            "result must be a JSON object, not a string — the SaaS zod rejects strings with 400 VALIDATION_ERROR");
        serialized.Should().NotContain("\"result\":\"{");
    }

    // ─── shopkeeper device list (GET /shops/me/devices) ──────────────────

    [Fact]
    public void Devices_list_response_fixture_deserializes_minimal_shape()
    {
        // Verbatim shape from src/app/api/v1/shops/me/devices/route.ts.
        // (We don't have a C# DTO for this — it's shopkeeper-side, called by the
        // website, not PrintEXE. This test documents the shape for any future
        // shopkeeper-side client + guards the envelope shape consistency.)
        var fixture = """
        {
          "success": true,
          "data": {
            "devices": [
              {
                "id": "agent-uuid-0001",
                "name": "Shop-Front-PC",
                "isOnline": true,
                "isEnabled": true,
                "lastSeenAt": "2026-11-17T10:00:00.000Z",
                "printerName": "HP LaserJet Pro",
                "version": "1.0.0",
                "os": "Windows 11",
                "createdAt": "2026-11-16T12:00:00.000Z"
              }
            ]
          }
        }
        """;

        var envelope = JsonSerializer.Deserialize<ApiEnvelope<JsonElement>>(fixture, WebJson);
        envelope!.Success.Should().BeTrue();
        envelope.Data.ValueKind.Should().Be(JsonValueKind.Object);
        envelope.Data.GetProperty("devices").GetArrayLength().Should().Be(1);
        var d = envelope.Data.GetProperty("devices")[0];
        d.GetProperty("id").GetString().Should().Be("agent-uuid-0001");
        d.GetProperty("isOnline").GetBoolean().Should().BeTrue();
        d.GetProperty("isEnabled").GetBoolean().Should().BeTrue();
    }

    // ─── heartbeat (POST /agents/heartbeat) ──────────────────────────────

    [Fact]
    public void Heartbeat_response_fixture_deserializes_into_AgentHeartbeatResponseDto()
    {
        // Verbatim from src/app/api/v1/agents/heartbeat/route.ts.
        var fixture = """
        {
          "success": true,
          "data": { "agentId": "agent-uuid-0001", "online": true, "ts": "2026-11-17T10:00:00.000Z" }
        }
        """;

        var envelope = JsonSerializer.Deserialize<ApiEnvelope<AgentHeartbeatResponseDto>>(fixture, WebJson);
        var dto = envelope!.Data.Should().NotBeNull().Subject;
        dto.AgentId.Should().Be("agent-uuid-0001");
        dto.Online.Should().BeTrue();
        dto.Ts.Should().NotBeNull();
    }

    // ─── job claim race (POST /agents/jobs/[id]/status) ──────────────────

    [Fact]
    public void Job_status_skipped_response_fixture_deserializes_into_JobStatusReport()
    {
        // Verbatim from src/app/api/v1/agents/jobs/[id]/status/route.ts —
        // a lost claim race returns 200 with skipped:true (NOT 409).
        var fixture = """
        {
          "success": true,
          "data": {
            "jobId": "job-uuid-0099",
            "skipped": true,
            "reason": "job is 'printing' — already claimed by another agent"
          }
        }
        """;

        var envelope = JsonSerializer.Deserialize<ApiEnvelope<JobStatusReport>>(fixture, WebJson);
        var dto = envelope!.Data.Should().NotBeNull().Subject;
        dto.JobId.Should().Be("job-uuid-0099");
        dto.Skipped.Should().BeTrue();
        dto.Reason.Should().Contain("already claimed");
    }
}
