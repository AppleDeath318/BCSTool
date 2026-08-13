using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace BCSTool.Services;

/// <summary>
/// Tracks the authoritative player roster emitted by the dedicated server's
/// log-only @DS@ {"ev":"players", ...} snapshots.
///
/// The structured log event contains the
/// complete state text (for example "creating character") even when the
/// native terminal visually crops that state.
/// </summary>
public sealed class PlayerRosterTracker
{
    private readonly List<PlayerRosterEntry> _players = new();

    public int PlayerCount =>
        _players.Count;

    public IReadOnlyList<PlayerRosterEntry> Players =>
        _players;

    public IReadOnlyList<string> RosterLines =>
        _players
            .Select(FormatPlayerLine)
            .ToArray();

    /// <summary>
    /// Replaces the entire roster from one authoritative @DS@ players list.
    /// Returns true only when the roster actually changed.
    /// </summary>
    public bool ProcessPlayersList(
        JsonElement listElement)
    {
        if (listElement.ValueKind != JsonValueKind.Array)
            return false;

        var next =
            new List<PlayerRosterEntry>();

        foreach (var item in listElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id =
                TryGetInt32(
                    item,
                    "id",
                    out var parsedId)
                    ? parsedId
                    : -1;

            var name =
                TryGetString(
                    item,
                    "name") ??
                "(joining)";

            var state =
                TryGetString(
                    item,
                    "state") ??
                "unknown";

            var address =
                TryGetString(
                    item,
                    "addr") ??
                "";

            next.Add(
                new PlayerRosterEntry(
                    id,
                    name,
                    state,
                    address));
        }

        next.Sort(
            (left, right) =>
                left.Id.CompareTo(
                    right.Id));

        if (_players.SequenceEqual(next))
            return false;

        _players.Clear();
        _players.AddRange(next);

        return true;
    }

    public void Reset()
    {
        _players.Clear();
    }

    private static string FormatPlayerLine(
        PlayerRosterEntry player)
    {
        var idPrefix =
            player.Id >= 0
                ? $"[{player.Id}] "
                : "";

        return
            $"{idPrefix}{player.Name} — {player.State}";
    }

    private static bool TryGetInt32(
        JsonElement element,
        string propertyName,
        out int value)
    {
        value = 0;

        return
            element.TryGetProperty(
                propertyName,
                out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(
                out value);
    }

    private static string? TryGetString(
        JsonElement element,
        string propertyName)
    {
        if (
            !element.TryGetProperty(
                propertyName,
                out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return
            property.GetString();
    }
}

public sealed record PlayerRosterEntry(
    int Id,
    string Name,
    string State,
    string Address);
