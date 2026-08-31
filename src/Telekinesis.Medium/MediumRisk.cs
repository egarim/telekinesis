namespace Telekinesis.Medium;

/// <summary>
/// Safety classification of an action, used by Telekinesis for policy/confirmation.
/// <see cref="Unknown"/> is the default — Medium never silently guesses that an
/// unclassified action is harmless (issue #28). Risk is advisory metadata; it is not a
/// bypass of Telekinesis's action enablement or audit logging.
/// </summary>
public enum MediumRisk
{
    /// <summary>Not classified. Do not treat as safe.</summary>
    Unknown = 0,

    /// <summary>Read-only; no state mutates.</summary>
    Read,

    /// <summary>Mutates application state, but is non-destructive / reversible.</summary>
    Write,

    /// <summary>Destructive (delete, overwrite, irreversibly mutate).</summary>
    Destructive,

    /// <summary>Privileged or security-sensitive (admin, elevation, credentials).</summary>
    Privileged,
}
