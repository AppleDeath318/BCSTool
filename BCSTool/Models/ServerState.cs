namespace BCSTool.Models;

/// <summary>
/// High-level lifecycle states used by the UI and automation logic.
///
/// Modeling the server as an explicit state machine is much safer than having
/// many unrelated boolean flags. For example, scheduled restart automation
/// is allowed only when the state is Ready.
/// </summary>
public enum ServerState
{
    // No managed Bannerlord process exists.
    Stopped,
    // BCS Tool has requested process startup.
    Starting,
    // Process exists, but the server has not printed the readiness message.
    WaitingForReady,
    // Server printed "coop server up, waiting for clients".
    Ready,
    // BCS Tool is sending or waiting on the save sequence.
    Saving,
    // A graceful "stop" command has been sent.
    Stopping,
    // The controlled restart sequence is in progress.
    Restarting,
    // The managed process exited unexpectedly.
    Crashed,
    // Startup is blocked by an existing server process or configured port.
    PortBlocked,
    // An operation failed and requires attention.
    Error
}
