using System;
using System.Text.Json;
using Jellyfin.Plugin.SyncPlayV2.Wire;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

/// <summary>
/// The wire layer is the compatibility contract: plugin DTOs must serialize to
/// the same JSON the patched server (integration/syncplay-phase1) emits, under
/// stock envelope types. These tests pin the translation and the JSON shape.
/// </summary>
public class WireTests
{
    [Fact]
    public void GroupUpdate_translation_carries_type_as_string_and_stamps_version()
    {
        var groupId = Guid.NewGuid();
        var stock = new SyncPlayUserJoinedUpdate(groupId, "alice");

        var wire = WireGroupUpdate.From(stock, 42);

        Assert.Equal(groupId, wire.GroupId);
        Assert.Equal("UserJoined", wire.Type);
        Assert.Equal(42, wire.StateVersion);
        Assert.Equal("alice", wire.Data);
    }

    [Fact]
    public void SendCommand_translation_preserves_all_fields_and_stamps_version()
    {
        var groupId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var when = DateTime.UtcNow;
        var emitted = when.AddMilliseconds(-5);
        var stock = new SendCommand(groupId, itemId, when, SendCommandType.Unpause, 1234, emitted);

        var wire = WireSendCommand.From(stock, 7);

        Assert.Equal(groupId, wire.GroupId);
        Assert.Equal(itemId, wire.PlaylistItemId);
        Assert.Equal(when, wire.When);
        Assert.Equal(1234, wire.PositionTicks);
        Assert.Equal("Unpause", wire.Command);
        Assert.Equal(emitted, wire.EmittedAt);
        Assert.Equal(7, wire.StateVersion);
    }

    [Fact]
    public void GroupUpdate_serializes_open_type_strings_the_core_enum_lacks()
    {
        var wire = new WireGroupUpdate
        {
            GroupId = Guid.Empty,
            Type = "StateSnapshot",
            StateVersion = 3,
            Data = new WireGroupSnapshot { GroupName = "g" },
        };

        var json = JsonSerializer.Serialize(wire);

        Assert.Contains("\"Type\":\"StateSnapshot\"", json, StringComparison.Ordinal);
        Assert.Contains("\"StateVersion\":3", json, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupInfo_protocol_version_is_nullable_for_member_scoped_advertisement()
    {
        // Spec §2 reads "absence of GroupInfoDto.ProtocolVersion" as v1; the
        // plugin-binding rule advertises v2 only to members that negotiated it.
        var v1 = new WireGroupInfo { ProtocolVersion = null };
        var v2 = new WireGroupInfo { ProtocolVersion = 2 };

        Assert.Null(JsonSerializer.Deserialize<WireGroupInfo>(JsonSerializer.Serialize(v1))!.ProtocolVersion);
        Assert.Equal(2, JsonSerializer.Deserialize<WireGroupInfo>(JsonSerializer.Serialize(v2))!.ProtocolVersion);
    }

    [Fact]
    public void MemberStatus_defaults_match_the_fork_dto()
    {
        var m = new WireMemberStatus();

        Assert.True(m.IsConnected);
        Assert.False(m.IsBuffering);
        Assert.False(m.IgnoreGroupWait);
    }
}
