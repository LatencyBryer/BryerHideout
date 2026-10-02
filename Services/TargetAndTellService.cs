using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using BryersHideoutPlugin.Models;

namespace BryersHideoutPlugin.Services;

public sealed class TargetAndTellService
{
    private readonly ITargetManager targets;
    private readonly IPluginLog log;

    public TargetAndTellService(ITargetManager targets, IPluginLog log)
    {
        this.targets = targets;
        this.log = log;
    }

    public string? CurrentPlayerTargetName()
    {
        var target = targets.Target;
        if (target is null || target.ObjectKind != ObjectKind.Pc) return null;
        var name = target.Name.TextValue.Trim();
        return name.Length == 0 ? null : name;
    }

    public void SendCodeToCurrentTarget(PlayerModel player, MembershipModel membership, ProfileModel profile, string template)
    {
        var target = targets.Target as IPlayerCharacter
            ?? throw new InvalidOperationException("Target a player character first.");

        var targetName = target.Name.TextValue.Trim();
        if (targetName.Length == 0)
            throw new InvalidOperationException("Could not read the targeted character name.");

        if (!NamesMatch(targetName, player.Name))
            throw new InvalidOperationException($"Your current target is {targetName}, but the selected site Player is {player.Name}. Target the matching character before sending a code.");

        if (string.IsNullOrWhiteSpace(membership.AccessCode))
            throw new InvalidOperationException("This Player does not currently have an access code for the active Venue.");

        var worldName = target.HomeWorld.Value.Name.ToString().Trim();
        if (worldName.Length == 0)
            throw new InvalidOperationException($"Could not read {targetName}'s Home World, so the /tell recipient could not be built.");

        var text = (template ?? "").Trim();
        if (text.Length == 0)
            throw new InvalidOperationException("The Tell template is empty.");

        text = text.Replace("{code}", membership.AccessCode, StringComparison.OrdinalIgnoreCase)
                   .Replace("{player}", player.Name, StringComparison.OrdinalIgnoreCase)
                   .Replace("{venue}", profile.Name, StringComparison.OrdinalIgnoreCase)
                   .Replace('\r', ' ')
                   .Replace('\n', ' ');

        // Keep enough headroom for '/tell First Last@World ' and the game's input limit.
        if (text.Length > 400) text = text[..400];

        // FFXIV's Tell syntax requires the recipient in Name@World form. <t> is a
        // display placeholder and is not a valid replacement for that recipient slot.
        var recipient = $"{targetName}@{worldName}";
        var command = $"/tell {recipient} {text}";

        ExecuteNativeGameCommand(command, recipient);
    }

    public void SendShout(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidOperationException("The generated announcement is empty.");
        // Exactly one native chat command, never an embedded second command.
        var clean = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (clean.Length == 0 || clean.Contains('\0'))
            throw new InvalidOperationException("Invalid announcement text.");
        var command = "/shout " + clean;
        if (System.Text.Encoding.UTF8.GetByteCount(command) > 420)
            throw new InvalidOperationException("The announcement exceeds the in-game chat length limit. Shorten the template.");
        ExecuteNativeGameCommand(command, "Shout");
    }

    /// <summary>
    /// Send a real game /shout containing a native item-link payload, not a local
    /// IChatGui.Print message (which only the plugin owner would see).
    /// Returns false only if the item cannot be resolved or the encoded command is
    /// too large, so the caller can fall back to its existing plain-text shout.
    /// The caller must run this on the FFXIV framework thread.
    /// </summary>
    public bool SendShoutWithItemLink(string messageWithPrizePlaceholder, uint itemId)
    {
        if (itemId == 0 ||
            !Regex.IsMatch(messageWithPrizePlaceholder, @"\{prize\}", RegexOptions.IgnoreCase))
            return false;

        byte[] itemPayload;
        try
        {
            // Resolve the native, localized in-game item name. Invalid/non-item RowIds
            // cannot generate a link and must never prevent the ordinary announcement.
            itemPayload = SeString.CreateItemLink(itemId, false).Encode();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Ingame ID {ItemId} could not be linked; using plain-text announcement.", itemId);
            return false;
        }

        var segments = Regex.Split(messageWithPrizePlaceholder, @"\{prize\}", RegexOptions.IgnoreCase);
        var command = new List<byte>(Encoding.UTF8.GetBytes("/shout "));
        for (var i = 0; i < segments.Length; i++)
        {
            command.AddRange(Encoding.UTF8.GetBytes(segments[i]));
            if (i < segments.Length - 1) command.AddRange(itemPayload);
        }
        if (command.Count > 420 || command.Contains((byte)0))
        {
            log.Warning("Native item-linked shout for {ItemId} exceeds the chat command limit or contains an invalid byte; using text.", itemId);
            return false;
        }

        // Important: UTF-8-decoding or sanitizing this command would flatten the
        // embedded SeString item payload into ordinary text. The only binary payload
        // here is generated by Dalamud's CreateItemLink; all surrounding template
        // text was already normalized by AnnouncementService.Render.
        ExecuteNativeGamePayloadCommand(command.ToArray(), "Shout");
        return true;
    }

    /// <summary>
    /// Send a saved /hshout template, replacing every {id:12345} token with the
    /// game's native clickable item-link payload. No site lookup is involved.
    /// Ordinary templates still use the existing plain /shout path.
    /// Must be invoked on FFXIV's framework thread.
    /// </summary>
    public void SendShoutWithInlineItemLinks(string template)
    {
        var clean = (template ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (clean.Length == 0 || clean.Contains('\0'))
            throw new InvalidOperationException("The shout message is empty or invalid.");

        // Recognize all item tokens before sending, so a malformed token never
        // leaks into the public /shout and never consumes its rotation entry.
        var tokenMatches = Regex.Matches(clean, @"\{id:[^}]*\}", RegexOptions.IgnoreCase);
        if (tokenMatches.Count == 0)
        {
            SendShout(clean);
            return;
        }

        var validMatches = Regex.Matches(clean, @"\{id:(?<itemId>[0-9]+)\}", RegexOptions.IgnoreCase);
        if (validMatches.Count != tokenMatches.Count)
            throw new InvalidOperationException("Invalid item placeholder. Use {id:46600} with a valid numeric in-game item ID.");

        var command = new List<byte>(Encoding.UTF8.GetBytes("/shout "));
        var cursor = 0;
        foreach (Match match in validMatches)
        {
            command.AddRange(Encoding.UTF8.GetBytes(clean[cursor..match.Index]));
            if (!uint.TryParse(match.Groups["itemId"].Value, out var itemId) || itemId == 0)
                throw new InvalidOperationException($"Invalid FFXIV item ID in {match.Value}.");

            byte[] itemPayload;
            try
            {
                itemPayload = SeString.CreateItemLink(itemId, false).Encode();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Manual shout could not create an item link for ID {ItemId}.", itemId);
                throw new InvalidOperationException($"Could not create a clickable item link for ID {itemId}.", ex);
            }

            if (itemPayload.Length == 0 || itemPayload.Contains((byte)0))
                throw new InvalidOperationException($"The item-link payload for ID {itemId} is invalid.");

            command.AddRange(itemPayload);
            cursor = match.Index + match.Length;
        }
        command.AddRange(Encoding.UTF8.GetBytes(clean[cursor..]));

        if (command.Count > 420 || command.Contains((byte)0))
            throw new InvalidOperationException("The item-linked shout exceeds the in-game chat length limit. Shorten the message.");

        // Keep the SeString item payload encoded. Converting the command back
        // to UTF-8 text or sanitizing it would turn the clickable link into text.
        ExecuteNativeGamePayloadCommand(command.ToArray(), "Shout");
    }

    private void ExecuteNativeGameCommand(string command, string recipient) =>
        ExecuteNativeGameCommand(Encoding.UTF8.GetBytes(command), recipient);

    private unsafe void ExecuteNativeGamePayloadCommand(ReadOnlySpan<byte> command, string recipient)
    {
        if (command.Length == 0 || command.Contains((byte)0))
            throw new InvalidOperationException("Invalid native item-linked chat command.");
        ExecuteNativeGameCommandCore(command, recipient, sanitize: false);
    }

    private unsafe void ExecuteNativeGameCommand(ReadOnlySpan<byte> command, string recipient) =>
        ExecuteNativeGameCommandCore(command, recipient, sanitize: true);

    private unsafe void ExecuteNativeGameCommandCore(ReadOnlySpan<byte> command, string recipient, bool sanitize)
    {
        try
        {
            var uiModule = UIModule.Instance();
            var shell = RaptureShellModule.Instance();
            if (uiModule is null || shell is null)
                throw new InvalidOperationException("FFXIV's native chat command shell is not available right now.");

            using var text = new Utf8String(command);
            if (sanitize)
            {
                text.SanitizeString(
                    AllowedEntities.Unknown9 |
                    AllowedEntities.Payloads |
                    AllowedEntities.OtherCharacters |
                    AllowedEntities.SpecialCharacters |
                    AllowedEntities.Numbers |
                    AllowedEntities.LowercaseLetters |
                    AllowedEntities.UppercaseLetters |
                    AllowedEntities.CJK);
            }

            if (text.Length > 500)
                throw new InvalidOperationException("The generated native chat command is too long for FFXIV chat.");

            // ICommandManager.ProcessCommand only dispatches commands registered with
            // Dalamud/plugins. Native game commands such as /tell and /shout have to
            // go through the game's RaptureShellModule instead.
            shell->ExecuteCommandInner(&text, uiModule);
            log.Debug("Submitted native {ChatMode} command ({Recipient}).", recipient == "Shout" ? "/shout" : "/tell", recipient);
        }
        catch (Exception ex)
        {
            // Never log the command itself: it may contain the player's private access code.
            log.Warning(ex, "Failed to submit native {ChatMode} command ({Recipient}).", recipient == "Shout" ? "/shout" : "/tell", recipient);
            throw;
        }
    }

    public static bool NamesMatch(string a, string b) =>
        string.Equals(NormalizeName(a), NormalizeName(b), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeName(string name)
    {
        var value = (name ?? "").Trim();
        var at = value.IndexOf('@');
        if (at >= 0) value = value[..at];
        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
