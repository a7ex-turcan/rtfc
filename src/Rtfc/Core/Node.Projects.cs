using Microsoft.Extensions.Logging;
using Rtfc.Protocol;
using Rtfc.Storage;

namespace Rtfc.Core;

/// <summary>
/// Projects (spec §10.2) and the messages addressed to them (spec §7.6). A project is what a
/// session runs in; a contact names it by its folder, and nothing about it crosses the wire
/// except that name.
/// </summary>
public sealed partial class Node
{
    /// <summary>Records the project a session runs in: the git root around <paramref name="directory"/>, else the directory. Idempotent and cheap.</summary>
    public ProjectRow RegisterProject(string directory)
    {
        var root = ProjectPaths.Root(directory);
        var key = ProjectPaths.Key(root);
        var existing = _db.GetProjectByRoot(key);
        var project = _db.UpsertProject(new ProjectRow(existing?.Id ?? Ulid.NewUlid(_clock.GetUtcNow()), key, ProjectPaths.Name(root)));
        if (existing is null)
        {
            _logger.LogInformation("Project {Name} registered at {Root}", project.Name, root);
        }

        return project;
    }

    /// <summary>
    /// Where an incoming message is stored: with the thread it continues, else in the project it names if exactly one
    /// project here has that name, else in the shared inbox, with a note when it named a project we could not use.
    /// </summary>
    private (string? ProjectId, string? Note) Route(string fromPerson, MessageFrame message)
    {
        if (message.ReplyTo is { } replyTo
            && _db.GetSent(replyTo) is { ProjectId: { } threadProject } sent
            && sent.ToPerson == fromPerson
            && _db.GetProject(threadProject) is not null)
        {
            return (threadProject, null);
        }

        if (message.Project is not { } name)
        {
            return (null, null);
        }

        // The note is rtfc's own words and is shown outside the untrusted wrapper, so it quotes the name only as a handle would be.
        var matches = _db.FindProjectsByName(name);
        return matches.Count switch
        {
            1 => (matches[0].Id, null),
            0 => (null, $"Sent for the project \"{ProjectName.ForDisplay(name)}\", which does not exist here, so it is in the shared inbox."),
            _ => (null, $"Sent for the project \"{ProjectName.ForDisplay(name)}\", which names {matches.Count} projects here, so it is in the shared inbox."),
        };
    }
}
